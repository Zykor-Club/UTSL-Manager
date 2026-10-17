using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UTSLManager.Backends;

/// <summary>本机服务端的进程操作。</summary>
public sealed class LocalProcessBackend(string exePath, string workDir) : IProcessBackend
{
    private const string ProcName = "UnifierTSL";

    public bool IsRemote => false;

    /// <summary>
    /// 有没有【活着】的服务端进程。
    /// ★ 不能只看"名字在不在进程列表里"：这台机器上被杀掉的进程会留下工作集 0、线程 0 的
    ///   僵尸条目（实测 HasExited=True），按名字判会以为服务端还活着 —— 看门狗因此永远不触发。
    /// </summary>
    /// <summary>该进程是否属于"这个部署目录"（本机可能同时跑着多份部署，绝不能按名字一刀切）。</summary>
    private bool BelongsHere(Process p, string workDir)
    {
        try
        {
            string? fp = p.MainModule?.FileName;
            if (fp is null) { return false; }
            string root = Path.GetFullPath(workDir).TrimEnd('\\', '/');
            return fp.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static bool AnyAlive() => AnyAliveIn(null);

    /// <summary>本机是否存在"属于某个部署目录"的活服务端；root 为 null 时任意一份都算。</summary>
    public static bool AnyAliveIn(string? root)
    {
        foreach (Process p in Process.GetProcessesByName(ProcName))
        {
            using (p)
            {
                try
                {
                    if (p.HasExited) { continue; }
                    if (root is null) { return true; }
                    string? fp = p.MainModule?.FileName;
                    if (fp is null) { continue; }
                    if (fp.StartsWith(Path.GetFullPath(root).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) { return true; }
                }
                catch { }
            }
        }
        return false;
    }

    // ★ 不要给服务端做 stdin/stdout 重定向（实测过，会把它弄坏）：
    //   UTSL 启动时要拿控制台缓冲（ConsolePal.GetBufferInfo / WindowHeight），
    //   一旦没有真实控制台，ServerContext 的 ServerSurfaceConsole 构造就抛 IOException，
    //   导致 TShock 静态构造失败 → 20 个插件里 16 个初始化失败（VirtualWorldCore / 尘歌壶全挂）。
    //   所以"管理器接管控制台（MCSManager 那种喂 stdin）"这条路在当前 UTSL 上不可用，
    //   除非先让 UTSL 容忍没有控制台缓冲的环境。详见开发文档 §32。
    /// <summary>
    /// 启动服务端时是否用 headless 世界控制台（不开每世界窗口）。由管理器按设置赋值。
    /// ★ 依据 UTSL 源码 ServerContext.DefaultConsoleMode：模式在【进程启动时】读环境变量 UTSL_CONSOLE_MODE
    ///   决定（PerWorld=默认，每个世界一个 ConsoleClient 窗口）。而且每个世界的会话驱动是 while(true) 循环，
    ///   手动关掉窗口它还会 RestartClient() 再拉起来 —— 唯一可靠的办法就是启动时 headless。
    /// </summary>
    public static bool HeadlessConsole = true;

    public Task<ProcessResult> StartAsync()
    {
        if (AnyAliveIn(workDir)) { return Task.FromResult(new ProcessResult(true, "")); }   // ★ 原来调 IProcessBackend.IsAlive()，那个成员已删（零调用）
        try
        {
            var psi = new ProcessStartInfo(exePath)
            {
                WorkingDirectory = workDir,
                Arguments = "-joinserver first -logmode txt",
            };
            if (HeadlessConsole) { psi.Environment["UTSL_CONSOLE_MODE"] = "headless"; }
            Process.Start(psi);
            return Task.FromResult(new ProcessResult(true, ""));
        }
        catch (Exception ex) { return Task.FromResult(new ProcessResult(false, "启动失败: " + ex.Message)); }
    }

    public Task<ProcessResult> StopAsync()
    {
        // ★ 逐个进程独立 try/catch：这台机器上被杀进程会留 HasExited=True 的僵尸条目，
        //   对僵尸 Kill() 会抛 —— 原来整个循环共用一个 try，一个条目抛异常后【后面的活进程就都不杀了】，
        //   同时返回"停止失败"，上层对服务端状态的判断随之出错。
        int killed = 0, failed = 0;
        foreach (Process p in Process.GetProcessesByName(ProcName))
        {
            using (p)
            {
                try
                {
                    // ★ 只杀"本档部署目录下"的进程：以前按名字一刀切，停本机服务端会把同机另一份部署
                    //   （例如测试用的 deploy-b）一起杀掉，那张卡就一直"REST 不通"。
                    if (p.HasExited) { continue; }
                    string? fp = p.MainModule?.FileName;
                    if (fp is null) { continue; }
                    if (!fp.StartsWith(Path.GetFullPath(workDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) { continue; }
                    p.Kill(); p.WaitForExit(3000); killed++;
                }
                catch { failed++; }
            }
        }
        try { return Task.FromResult(new ProcessResult(failed == 0, $"已停止 {killed} 个" + (failed > 0 ? $"，{failed} 个未能停止" : ""))); }
        catch (Exception ex) { return Task.FromResult(new ProcessResult(false, "停止失败: " + ex.Message)); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr min, IntPtr max);

    public string TrimMemory()
    {
        var list = Process.GetProcessesByName(ProcName).Where(p => BelongsHere(p, workDir)).ToArray();
        if (list.Length == 0) { return "服务端没在运行"; }
        var parts = new List<string>();
        foreach (var pr in list)
        {
            long before = pr.WorkingSet64;
            SetProcessWorkingSetSize(pr.Handle, new IntPtr(-1), new IntPtr(-1));
            pr.Refresh();
            parts.Add($"{before / 1048576} → {pr.WorkingSet64 / 1048576} MB");
        }
        return "已把物理内存标记为可回收: " + string.Join("；", parts) + " —— 注意：私有提交(commit)不变 ✗ 世界尺寸上限不会因此提高 ✓ 真正用途是【重操作前腾物理内存】✓";
    }
}
