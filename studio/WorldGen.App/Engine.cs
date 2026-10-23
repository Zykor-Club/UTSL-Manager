using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace WorldGen;

/// <summary>一次生成请求（与 92/93 号文档里约定的 gen-request.json 同构）。</summary>
public sealed record GenRequest(
    string WorldName, string Seed, int Width, int Height, int Difficulty, string Evil,
    int Slices, int Overlap, string[] Chain, string DeployRoot, string OutDir);

/// <summary>
/// 生成引擎（B′ 路线）：在【隔离部署副本】里用 UTSL 启动器生成母图 → 调 UTSLStitchTool 切分。
///
/// 为什么不是纯 OTAPI：1.4.5 的生成要整套 RootContext/图形单例初始化（等价于复刻 Program.LaunchGame），
/// 实测比直接用 UTSL 启动器贵得多。B′ 已实测：12000×2400 / 393 s / 峰值 1216 MB。
///
/// ★ 必须用 CREATE_NEW_CONSOLE 起 UTSL：它构造 ServerSurfaceConsole 时读 Console.WindowHeight，
///   stdout 被重定向（管道/文件）会抛 "句柄无效"。
/// </summary>
public sealed class StudioEngine
{
    public event Action<string>? LogLine;
    public event Action<string, double>? Stage;      // (阶段文案, 0..100)

    private Process? _engine;
    private string _lastStage = "";

    // ---- Win32：CREATE_NEW_CONSOLE + 最小化窗口 ----
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string? app, string cmdLine, IntPtr pa, IntPtr ta, bool inherit,
        uint flags, IntPtr env, string? cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    private const uint CREATE_NEW_CONSOLE = 0x00000010;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const int STARTF_USESHOWWINDOW = 0x00000001;
    private const short SW_SHOWMINIMIZED = 2;

    /// <summary>按请求写好部署副本的 config.json，清掉旧的源世界文件。返回随机端口。</summary>
    /// <summary>清掉同一部署根下遗留的引擎（上次被强杀 studio 时会留下子进程，两份同时写 progress.json 会串日志）。</summary>
    public static int KillStaleEngines(string deployRoot)
    {
        int killed = 0;
        string exe = Path.GetFullPath(Path.Combine(deployRoot, "UnifierTSL.exe"));
        foreach (var p in Process.GetProcessesByName("UnifierTSL"))
        {
            try
            {
                if (string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) { p.Kill(true); killed++; }
            }
            catch { }
        }
        return killed;
    }
    public int Prepare(GenRequest r)
    {
        int stale = KillStaleEngines(r.DeployRoot);
        if (stale > 0) { Log($"清理遗留引擎 {stale} 个"); }
        Directory.CreateDirectory(Path.Combine(r.DeployRoot, "config"));
        int port = FindFreePort();
        var cfg = new
        {
            logging = new { mode = "txt" },
            launcher = new
            {
                listenPort = port,
                serverPassword = "",
                joinServer = "first",
                colorfulConsoleStatus = false,
                autoStartServers = new[]
                {
                    new
                    {
                        name = r.WorldName,
                        worldName = r.WorldName,
                        seed = r.Seed,
                        difficulty = r.Difficulty.ToString(),
                        size = r.Width.ToString(),
                        evil = r.Evil,
                    },
                },
            },
        };
        string json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(r.DeployRoot, "config", "config.json"), json, new UTF8Encoding(false));

        string wldDir = Path.Combine(r.DeployRoot, "lib", "Saves", "Worlds");
        Directory.CreateDirectory(wldDir);
        foreach (var f in Directory.GetFiles(wldDir, r.WorldName + ".wld*")) { File.Delete(f); }
        return port;
    }

    /// <summary>用 CREATE_NEW_CONSOLE 起 UTSL（它会拿到自己的控制台，不会崩在 WindowHeight）。</summary>
    public void Launch(GenRequest r, int port)
    {
        string exe = Path.Combine(r.DeployRoot, "UnifierTSL.exe");
        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        si.dwFlags = STARTF_USESHOWWINDOW;
        si.wShowWindow = SW_SHOWMINIMIZED;
        string cmd = $"\"{exe}\" -joinserver first -logmode txt";
        if (!CreateProcess(exe, cmd, IntPtr.Zero, IntPtr.Zero, false,
                CREATE_NEW_CONSOLE | CREATE_UNICODE_ENVIRONMENT, IntPtr.Zero, r.DeployRoot, ref si, out var pi))
        {
            throw new InvalidOperationException("CreateProcess 失败，err=" + Marshal.GetLastWin32Error());
        }
        CloseHandle(pi.hThread);
        _engine = Process.GetProcessById(pi.dwProcessId);
        Log($"引擎已启动 pid={pi.dwProcessId} 端口 {port}");
    }

    /// <summary>等母图落盘（大小连续两次不变），期间把 UTSL 日志里的阶段名报给 UI。</summary>
    public async Task<string> WaitWorldAsync(GenRequest r, CancellationToken ct, TimeSpan timeout)
    {
        string wld = Path.Combine(r.DeployRoot, "lib", "Saves", "Worlds", r.WorldName + ".wld");
        string logDir = Path.Combine(r.DeployRoot, "logs");
        var sw = Stopwatch.StartNew();
        long lastSize = -1; int stable = 0;
        double lastPct = 0;

        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(2000, ct);

            // 看门狗：引擎要是没了（崩了/被杀了），立刻报出来，别让人干等 20 分钟
            if (_engine is { HasExited: true })
            {
                int code = -1;
                try { code = _engine.ExitCode; } catch { }
                throw new InvalidOperationException($"引擎异常退出（exit={code}）—— 见 {Path.Combine(r.DeployRoot, "logs")} 下最新日志");
            }

            // 进度：无阶段回调前，用"时间 + 世界文件是否出现"做粗粒度
            if (File.Exists(wld))
            {
                long size = new FileInfo(wld).Length;
                if (size == lastSize) { stable++; } else { stable = 0; }
                lastSize = size;
                if (size > 0) { Stage?.Invoke("母图落盘", 95); }
                if (stable >= 2 && size > 1024 * 1024) { return wld; }
            }
            else
            {
                lastPct = Math.Min(90, lastPct + 100.0 * 2 / 600);   // 约 10 分钟走满 90%
                Stage?.Invoke("生成中（UTSL 无阶段回调前按时间估算）", lastPct);
            }

            // ★ 引擎写出的实时阶段（"正在生成沙漠/清理树/放置陷阱"）+ 真实百分比
            try
            {
                string live = Path.Combine(r.DeployRoot, "live", "progress.json");
                if (File.Exists(live))
                {
                    using var fs = File.OpenRead(live);
                    using var doc = JsonDocument.Parse(fs);
                    string stage = doc.RootElement.TryGetProperty("stage", out var sEl) ? sEl.GetString() ?? "" : "";
                    double p = doc.RootElement.TryGetProperty("pct", out var pEl) ? pEl.GetDouble() : 0;
                    if (stage.Length > 0)
                    {
                        if (stage != _lastStage) { _lastStage = stage; Log("阶段> " + stage); }
                        Stage?.Invoke(stage, Math.Clamp(8 + p * 85, 8, 93));
                    }
                }
            }
            catch { }
            // 抓 UTSL 日志最后一行当阶段名
            try
            {
                var log = Directory.GetFiles(logDir, "*.log")
                                   .Where(f => !f.EndsWith("out.log") && !f.EndsWith("err.log"))
                                   .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc).FirstOrDefault();
                if (log is not null)
                {
                    string? last = File.ReadLines(log).LastOrDefault();
                    if (!string.IsNullOrWhiteSpace(last)) { Log("utsl> " + Trim(last)); }
                }
            }
            catch { }
        }
        throw new TimeoutException("生成超时（" + timeout.TotalMinutes + " 分钟）");
    }

    /// <summary>调 UTSLStitchTool split，返回三片路径。</summary>
    public async Task<List<string>> SplitAsync(GenRequest r, string motherWld, CancellationToken ct)
    {
        string tool = Path.Combine(@"C:\Users\星梦\Desktop\01_UTSL平台\repo-compare", "UTSLStitchTool", "bin", "Release", "net10.0", "UTSLStitchTool.exe");
        if (!File.Exists(tool)) { throw new FileNotFoundException("找不到切分工具: " + tool); }

        string splitDir = Path.Combine(r.OutDir, "split");
        if (Directory.Exists(splitDir)) { Directory.Delete(splitDir, true); }
        Directory.CreateDirectory(splitDir);

        var psi = new ProcessStartInfo(tool)
        {
            Arguments = $"split \"{motherWld}\" \"{splitDir}\" {r.Slices} {r.Overlap}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("切分工具起不来");
        var lines = new List<string>();
        while (!p.StandardOutput.EndOfStream)
        {
            string? line = await p.StandardOutput.ReadLineAsync(ct);
            if (line is null) { break; }
            lines.Add(line);
            if (line.Contains("已输出")) { Log("split> " + Trim(line)); }
        }
        await p.WaitForExitAsync(ct);

        var parts = Directory.GetFiles(splitDir, "*.wld").OrderBy(x => x).ToList();
        foreach (var part in parts)
        {
            var selfCheck = lines.FirstOrDefault(l => l.Contains("自检通过"));
            Log($"产出 {Path.GetFileName(part)}  {new FileInfo(part).Length / 1048576.0:F2} MB");
        }
        if (parts.Count != r.Slices) { throw new InvalidOperationException($"切分片数不对：期望 {r.Slices}，实际 {parts.Count}"); }
        return parts;
    }

    /// <summary>
    /// 读引擎写出的实时帧：live\frame.bin（int32 magic 'UTWF' · int32 w · int32 h · ushort[w*h]）。
    /// bit15=1 表示墙 id，否则是图格 id，0 表示空。返回 null 表示还没有帧或读失败。
    /// </summary>
    public static (int W, int H, ushort[] Px)? ReadFrame(string deployRoot)
    {
        try
        {
            string path = Path.Combine(deployRoot, "live", "frame.bin");
            if (!File.Exists(path)) { return null; }
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);
            if (br.ReadInt32() != 0x46575455) { return null; }
            int w = br.ReadInt32(), h = br.ReadInt32();
            if (w <= 0 || h <= 0 || w > 4096 || h > 4096) { return null; }
            var px = new ushort[w * h];
            for (int i = 0; i < px.Length; i++) { px[i] = br.ReadUInt16(); }
            return (w, h, px);
        }
        catch { return null; }
    }
    public void KillEngine()
    {
        try
        {
            if (_engine is { HasExited: false }) { _engine.Kill(true); }
        }
        catch { }
        _engine = null;
    }

    private static int FindFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string Trim(string s) => s.Length > 200 ? s[..200] : s;
    private void Log(string s) => LogLine?.Invoke(s);
}


