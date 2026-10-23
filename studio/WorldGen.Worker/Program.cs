// =====================================================================
//  UTSLWorldGen.Worker —— 原生生成 worker（照社区 UTSL-MapStudio 的架构）
//
//  用法：UTSLWorldGen.Worker.exe <引擎目录> <工作目录> <宽> <高> <种子> <难度 0..3> [邪恶 0随机/1腐化/2猩红]
//
//  产物（都写在工作目录里）：
//    Worlds\SeamSource.wld     生成好的母图
//    result.json               { completed, path, width, height, difficulty, surface, rock, pid, ... }
//    live\progress.json        实时阶段（正在生成沙漠…）+ 百分比 + 地表/岩层线
//    live\frame.bin            实时帧（'UTWF' 头 + 2400×N 的 ushort 图格/墙/液体编码）
//    worker-stage.log          启动阶段流水（出问题先看它）
// =====================================================================
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;

namespace WorldGen.Worker;

internal static class Program
{
    private static string _engine = "";
    private static string _work = "";
    private static int _width = 4200, _height = 1200, _difficulty = 2, _evil = 0;
    private static string _seed = "20261023";

    public static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (args.Length < 6)
            {
                Console.Error.WriteLine("用法: UTSLWorldGen.Worker <引擎目录> <工作目录> <宽> <高> <种子> <难度> [邪恶]");
                return 2;
            }
            _engine = Path.GetFullPath(args[0]);
            _work = Path.GetFullPath(args[1]);
            _width = int.Parse(args[2]);
            _height = int.Parse(args[3]);
            _seed = args[4];
            _difficulty = int.Parse(args[5]);
            if (args.Length > 6) { _evil = int.Parse(args[6]); }

            Directory.CreateDirectory(_work);
            Directory.SetCurrentDirectory(_work);
            Stage($"worker-start pid={Environment.ProcessId} {_width}x{_height} seed={_seed} difficulty={_difficulty} evil={_evil}");

            // 引擎目录里的 dll 靠这个解析（worker 自己不复制 60MB 的 OTAPI）
            Environment.SetEnvironmentVariable("UTSL_WORLD_PATH", Path.Combine(_work, "Worlds"));
            Environment.SetEnvironmentVariable("UTSL_SAVE_PATH", Path.Combine(_work, "Saves"));
            Environment.SetEnvironmentVariable("UTSL_CONSOLE_MODE", "headless");
            Environment.SetEnvironmentVariable("UTSL_CUSTOM_HEIGHT", _height.ToString());
            AssemblyLoadContext.Default.Resolving += (_, name) =>
            {
                string candidate = Path.Combine(_engine, name.Name + ".dll");
                return File.Exists(candidate) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate) : null;
            };

            Directory.CreateDirectory(Path.Combine(_work, "config"));
            Directory.CreateDirectory(Path.Combine(_work, "plugins"));
            Directory.CreateDirectory(Path.Combine(_work, "Worlds"));
            Directory.CreateDirectory(Path.Combine(_work, "Saves"));
            Directory.CreateDirectory(Path.Combine(_work, "live"));
            File.WriteAllText(Path.Combine(_work, "config", "config.json"),
                """{"logging":{"mode":"txt"},"launcher":{"listenPort":29991,"joinServer":"none","colorfulConsoleStatus":false,"autoStartServers":[]}}""");

            return Generate();
        }
        catch (Exception ex)
        {
            Stage("FATAL " + ex);
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Generate()
    {
        var api = typeof(UnifierTSL.UnifierApi);
        UnifierTSL.Initializer.InitializeResolver();
        Stage("resolver-ready");
        Directory.CreateDirectory(Environment.GetEnvironmentVariable("UTSL_SAVE_PATH")!);
        Terraria.Program.SavePath = Environment.GetEnvironmentVariable("UTSL_SAVE_PATH");

        // 别让 ApplySavePathOverride 去导入用户自己的存档
        typeof(Terraria.Main).GetField("WorldPath", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(null, Environment.GetEnvironmentVariable("UTSL_WORLD_PATH"));

        var surface = api.Assembly.GetType("UnifierTSL.Surface.Hosting.LauncherSurfaceConsole", true)!;
        surface.GetMethod("Initialize", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
            .Invoke(null, [new UnifierTSL.Surface.Adapter.Cli.Terminal.TerminalLauncherSurfaceHost()]);
        Stage("surface-console-ready");

        static void Call(Type api, string name, params object[] parameters) =>
            (api.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic) ?? throw new MissingMethodException(name))
                .Invoke(null, parameters);

        Call(api, "HandleCommandLinePreRun", (object)new[] { "-lang", "7" });
        Call(api, "PrepareRuntime", (object)Array.Empty<string>());
        UnifierTSL.Initializer.Initialize();
        Call(api, "InitializeCore");
        Stage("core-initialized-offline");

        // 游戏内地图配色表（mod 那种"游戏自己画的图"就是用它）：纯 CPU，不需要显卡
        try
        {
            Terraria.Map.MapHelper.Initialize();
            var cl = Terraria.Map.MapHelper.colorLookup;
            Stage($"maphelper-init len={cl.Length} 样例(石头1)={cl[1].R},{cl[1].G},{cl[1].B}");
        }
        catch (Exception ex) { Stage("maphelper-init FAIL " + ex.GetType().Name + " " + ex.Message); }

        // 生成参数：尺寸/难度/种子
        On.Terraria.WorldGenSystemContext.GenerateWorld += (orig, ctx, progress, controller) =>
        {
            var world = (UnifierTSL.Servers.ServerContext)ctx.root;
            world.Main.maxTilesX = _width;
            world.Main.maxTilesY = _height;
            world.Main.GameMode = _difficulty;
            world.Main.ActiveWorldFileData.SetSeed(_seed);
            Stage($"generateworld-enter {_width}x{_height} seed={_seed} difficulty={_difficulty}");
            return orig(ctx, progress, controller);
        };

        // 实时采样：pass 名 + 进度 + 图格缩略图（worker 自己就是世界，帧=世界）
        var sampler = new LiveSampler();
        On.Terraria.WorldBuilding.GenPass.Apply += (orig, pass, root, progress, config) =>
        {
            sampler.Enter(pass.Name, progress, root);
            orig(pass, root, progress, config);
        };

        // 在联网之前落盘退出
        On.Terraria.NetplaySystemContext.StartServer += (orig, ctx) =>
        {
            var server = (UnifierTSL.Servers.ServerContext)ctx.root;
            Stage("startserver-intercepted-before-network");
            server.WorldFile.SaveWorld(resetTime: false, useTemps: false);
            sampler.Finish(server);
            string file = server.Main.worldPathName;
            File.WriteAllText(Path.Combine(_work, "result.json"), JsonSerializer.Serialize(new
            {
                completed = true,
                path = file,
                width = server.Main.maxTilesX,
                height = server.Main.maxTilesY,
                difficulty = server.Main.GameMode,
                surface = (int)server.Main.worldSurface,
                rock = (int)server.Main.rockLayer,
                spawnX = server.Main.spawnTileX,
                spawnY = server.Main.spawnTileY,
                seed = _seed,
                pid = Environment.ProcessId,
                networkStarted = false,
                pluginsLoaded = false,
                at = DateTimeOffset.Now,
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("WORKER_WORLD_SAVED " + file);
            Environment.Exit(0);
            return;
        };

        var provider = UnifierTSL.Servers.IWorldDataProvider.GenerateOrLoadExisting("SeamSource", _width, _difficulty, _evil, _seed);
        var world = new UnifierTSL.Servers.ServerContext("SeamSource", provider);
        world.Netplay.UseUPNP = false;
        Stage("server-context-ready; run-blocking");
        world.RunBlocking(["-lang", "7"]);
        throw new InvalidOperationException("worker 没能在 StartServer 处完成落盘。");
    }

    internal static string WorkDir => _work;

    internal static void Stage(string message)
    {
        try { File.AppendAllText(Path.Combine(_work, "worker-stage.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"); }
        catch { }
        Console.WriteLine("[worker] " + message);
    }
}
