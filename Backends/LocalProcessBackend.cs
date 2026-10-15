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
    public static bool AnyAlive()
    {
        foreach (Process p in Process.GetProcessesByName(ProcName))
        {
            try { if (!p.HasExited) { return true; } } catch { }   // 取不到句柄的条目按"不活"算
        }
        return false;
    }

    public bool IsAlive() => AnyAlive();

    // ★ 不要给服务端做 stdin/stdout 重定向（实测过，会把它弄坏）：
    //   UTSL 启动时要拿控制台缓冲（ConsolePal.GetBufferInfo / WindowHeight），
    //   一旦没有真实控制台，ServerContext 的 ServerSurfaceConsole 构造就抛 IOException，
    //   导致 TShock 静态构造失败 → 20 个插件里 16 个初始化失败（VirtualWorldCore / 尘歌壶全挂）。
    //   所以"管理器接管控制台（MCSManager 那种喂 stdin）"这条路在当前 UTSL 上不可用，
    //   除非先让 UTSL 容忍没有控制台缓冲的环境。详见开发文档 §32。
    public Task<ProcessResult> StartAsync()
    {
        if (IsAlive()) { return Task.FromResult(new ProcessResult(true, "")); }
        try
        {
            Process.Start(new ProcessStartInfo(exePath)
            {
                WorkingDirectory = workDir,
                Arguments = "-joinserver first -logmode txt",
            });
            return Task.FromResult(new ProcessResult(true, ""));
        }
        catch (Exception ex) { return Task.FromResult(new ProcessResult(false, "启动失败: " + ex.Message)); }
    }

    public Task<ProcessResult> StopAsync()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(ProcName)) { p.Kill(); }
            return Task.FromResult(new ProcessResult(true, ""));
        }
        catch (Exception ex) { return Task.FromResult(new ProcessResult(false, "停止失败: " + ex.Message)); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr min, IntPtr max);

    public string TrimMemory()
    {
        var list = Process.GetProcessesByName(ProcName);
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
