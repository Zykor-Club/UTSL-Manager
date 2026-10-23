using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace WorldGen;

/// <summary>一次生成请求（与 gen-request.json 同构）。</summary>
public sealed record GenRequest(
    string WorldName, string Seed, int Width, int Height, int Difficulty, string Evil,
    int Slices, int Overlap, string[] Chain, string OutDir);

/// <summary>
/// 生成引擎 —— 现在走**我们自己的 worker**（照社区 UTSL-MapStudio 架构）。
///
/// 为什么不再起 UTSL 启动器：
///   1) 从外部进程抽 Main.tile 写帧，抽到的不是落盘那份数组（实测帧顶部 38% 有图格，
///      落盘世界 &lt;5% 的列在 178 行以上有图格）→ 实时图永远多一条"天花板"；
///   2) UTSL 启动器要真控制台（ServerSurfaceConsole 读 Console.WindowHeight）。
/// worker 自己就是世界，帧=世界，也不需要控制台（headless + TerminalLauncherSurfaceHost）。
/// </summary>
public sealed class StudioEngine
{
    /// <summary>引擎目录（OTAPI/UnifierTSL 就在这里，worker 靠 AssemblyLoadContext 解析）。</summary>
    public const string EngineDir = @"C:\Users\星梦\Desktop\01_UTSL平台\utsl-deploy\lib";

    /// <summary>worker 可执行文件（仓库里 build 出来的）。</summary>
    public const string WorkerExe = @"D:\Projects\服务器\UTSLManager.Avalonia\studio\WorldGen.Worker\bin\Release\net9.0\UTSLWorldGen.Worker.exe";

    /// <summary>当前工作目录（worker 生成的 Worlds/Saves/live 都在这里，UI 也读这里）。</summary>
    public static string WorkDir { get; private set; } = "";

    public event Action<string>? LogLine;
    public event Action<string, double>? Stage;      // (阶段文案, 0..100)

    private Process? _worker;
    private string _lastStage = "";

    /// <summary>按请求准备一个干净的工作目录（worker 要求目录可重建）。</summary>
    public void Prepare(GenRequest r)
    {
        WorkDir = Path.Combine(r.OutDir, "work");
        if (Directory.Exists(WorkDir)) { Directory.Delete(WorkDir, recursive: true); }
        Directory.CreateDirectory(WorkDir);
        int stale = KillStaleWorkers();
        if (stale > 0) { Log($"清理遗留 worker {stale} 个"); }
        Log($"工作目录已准备 {WorkDir}");
    }

    /// <summary>拉起 worker：<c>worker &lt;引擎目录&gt; &lt;工作目录&gt; &lt;宽&gt; &lt;高&gt; &lt;种子&gt; &lt;难度&gt; &lt;邪恶&gt;</c></summary>
    public void Launch(GenRequest r)
    {
        if (!File.Exists(WorkerExe)) { throw new FileNotFoundException("找不到 worker: " + WorkerExe); }

        int evil = r.Evil switch { "corruption" => 1, "crimson" => 2, _ => 0 };
        var psi = new ProcessStartInfo(WorkerExe)
        {
            WorkingDirectory = WorkDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { EngineDir, WorkDir, r.Width.ToString(), r.Height.ToString(), r.Seed, r.Difficulty.ToString(), evil.ToString() })
        {
            psi.ArgumentList.Add(a);
        }

        _worker = Process.Start(psi) ?? throw new InvalidOperationException("worker 起不来");
        _worker.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) { Log("worker> " + Trim(e.Data)); } };
        _worker.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) { Log("worker! " + Trim(e.Data)); } };
        _worker.BeginOutputReadLine();
        _worker.BeginErrorReadLine();
        Log($"worker 已启动 pid={_worker.Id}");
    }

    /// <summary>等 worker 写出 result.json（生成完成），期间把阶段/进度报给 UI。</summary>
    public async Task<string> WaitWorldAsync(GenRequest r, CancellationToken ct, TimeSpan timeout)
    {
        string resultFile = Path.Combine(WorkDir, "result.json");
        string progressFile = Path.Combine(WorkDir, "live", "progress.json");
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(1000, ct);

            if (File.Exists(resultFile))
            {
                await Task.Delay(500, ct);      // 等它写完
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(resultFile));
                    string path = doc.RootElement.GetProperty("path").GetString() ?? "";
                    int sf = doc.RootElement.TryGetProperty("surface", out var s) ? s.GetInt32() : 0;
                    int rk = doc.RootElement.TryGetProperty("rock", out var k) ? k.GetInt32() : 0;
                    Log($"worker 完成：{path}（地表 {sf} / 岩层 {rk}）");
                    Stage?.Invoke("母图落盘", 96);
                    return path;
                }
                catch (Exception ex) { Log("result.json 读不动（再等）: " + ex.Message); }
            }

            if (File.Exists(progressFile))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(progressFile));
                    string stage = doc.RootElement.TryGetProperty("stage", out var st) ? st.GetString() ?? "" : "";
                    double pct = doc.RootElement.TryGetProperty("pct", out var p) ? p.GetDouble() : 0;
                    if (stage.Length > 0)
                    {
                        if (stage != _lastStage) { _lastStage = stage; Log("阶段> " + stage); }
                        Stage?.Invoke(stage, Math.Clamp(5 + pct * 90, 5, 95));
                    }
                }
                catch { }
            }

            if (_worker is { HasExited: true })
            {
                int code = -1;
                try { code = _worker.ExitCode; } catch { }
                throw new InvalidOperationException($"worker 异常退出（exit={code}）—— 看 {Path.Combine(WorkDir, "worker-stage.log")}");
            }
        }
        throw new TimeoutException($"生成超时（{timeout.TotalMinutes:F0} 分钟）");
    }

    /// <summary>
    /// 读 worker 写出的实时帧：live\frame.bin（'UTWF' · int32 w · int32 h · ushort[w*h]）。
    /// bit15=墙 id · bit14=液体 · 其余=图格 id · 0=空。
    /// </summary>
    public static (int W, int H, ushort[] Px)? ReadFrame()
    {
        try
        {
            if (WorkDir.Length == 0) { return null; }
            string path = Path.Combine(WorkDir, "live", "frame.bin");
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
            if (line.Contains("自检通过") || line.Contains("已输出")) { Log("split> " + Trim(line)); }
        }
        await p.WaitForExitAsync(ct);

        var parts = Directory.GetFiles(splitDir, "*.wld").OrderBy(x => x).ToList();
        foreach (var part in parts)
        {
            Log($"产出 {Path.GetFileName(part)}  {new FileInfo(part).Length / 1048576.0:F2} MB");
        }
        if (parts.Count != r.Slices) { throw new InvalidOperationException($"切分片数不对：期望 {r.Slices}，实际 {parts.Count}"); }
        return parts;
    }

    public void KillWorker()
    {
        try { if (_worker is { HasExited: false }) { _worker.Kill(entireProcessTree: true); } } catch { }
        _worker = null;
    }

    /// <summary>清掉遗留 worker（上次强杀 studio 会留下）。</summary>
    public static int KillStaleWorkers()
    {
        int killed = 0;
        foreach (var p in Process.GetProcessesByName("UTSLWorldGen.Worker"))
        {
            try { p.Kill(entireProcessTree: true); killed++; } catch { }
        }
        return killed;
    }

    private static string Trim(string s) => s.Length > 240 ? s[..240] : s;
    private void Log(string s) => LogLine?.Invoke(s);
}
