using System.Runtime.InteropServices;
using Avalonia;

namespace UTSLManager;

internal static class Program
{
    private const string InstanceKey = "UTSLManager_SingleInstance_9F2A6C";
    private static Mutex? instanceMutex;

    /// <summary>启动参数（MainWindow 用来处理 --open-plugin 等深链）。</summary>
    public static string[] StartupArgs = [];

    /// <summary>
    /// 启动里程碑日志（写 switchperf.log）。★ 用途：诊断"点了图标到底卡在哪一步"——
    /// 曾经出现过"进程活着、8 分钟没有主窗口"，而 switchperf 里只有一行 start，看不出卡点。
    /// </summary>
    public static void Log(string msg)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "switchperf.log"),
                $"{DateTime.Now:HH:mm:ss.fff}	START	{msg}" + Environment.NewLine);
        }
        catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string win);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);

    [STAThread]
    public static void Main(string[] args)
    {
        // ★ 无界面自检（移植 PGame 的 --selfcheck 思路）：必须在单实例判断之前 ——
        //   否则管理器已在运行时会被"唤醒已有窗口"直接挡掉，跑不出结果。
        if (Array.Exists(args, a => string.Equals(a, "--selfcheck", StringComparison.OrdinalIgnoreCase)))
        {
            string outPath = Path.Combine(AppContext.BaseDirectory, "selfcheck.txt");
            try { File.WriteAllText(outPath, MainWindow.RunSelfCheck()); }
            catch (Exception ex) { try { File.WriteAllText(outPath, "自检异常: " + ex); } catch { } }
            return;
        }

        // ★ 单实例：重复双击 exe 只唤醒已有窗口，不创建第二个（移植自 TSManager）
        //   ⚠ 旧实现：抢不到 mutex 就找窗口，窗口没找到就【直接 return】——上一个实例正在退出/正在启动时
        //     窗口可能还不存在，于是这次启动静默消失（用户："点了图标没反应"；自动化脚本白等 60 秒）。
        //     现在：先重试找窗口（最多 6 秒），窗口始终没有就尝试接盘 mutex，接盘成功说明老实例真走了 → 继续启动。
        instanceMutex = new Mutex(false, InstanceKey);
        bool ownedMutex = instanceMutex.WaitOne(0);
        Log($"mutex owned={ownedMutex} pid={Environment.ProcessId}");
        if (!ownedMutex)
        {
            for (int i = 0; i < 30; i++)
            {
                IntPtr h = FindWindow(null, "UTSLManager");
                if (h != IntPtr.Zero)
                {
                    // ★ 只有【活着且有响应】的那个实例才值得唤醒：找到的窗口可能属于正在退出/已被强杀的进程，
                    //   那会儿唤醒它 = 什么都不发生，而本进程又 return 了 → 结果【一台也没起来】（自动化脚本白等）。
                    bool alive = WindowIsAlive(h, out int ownerPid);
                    Log($"found-window hwnd={h} ownerPid={ownerPid} alive={alive} try={i}");
                    if (alive) { ShowWindow(h, 9); SetForegroundWindow(h); Log("wake-existing"); return; }   // 9 = SW_RESTORE
                }
                if (instanceMutex.WaitOne(300)) { ownedMutex = true; break; }                 // 老实例已退出 → 接盘
            }
            if (!ownedMutex) { Log("give-up: another instance alive"); return; }   // 确实还有活着的实例：安静退出
            Log("took-over-mutex");
        }

        StartupArgs = args;
        try { Log("app-starting"); BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        finally { try { if (ownedMutex) { instanceMutex.ReleaseMutex(); } } catch { } instanceMutex.Dispose(); }
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);

    /// <summary>那个窗口背后的进程还在吗、还响应吗（WM_NULL 往返 1 秒内回）。</summary>
    private static bool WindowIsAlive(IntPtr h, out int ownerPid)
    {
        ownerPid = 0;
        try
        {
            GetWindowThreadProcessId(h, out uint pid);
            ownerPid = (int)pid;
            if (pid == 0 || !IsWindowVisible(h)) { return false; }
            IntPtr res;
            return SendMessageTimeout(h, 0x0000, IntPtr.Zero, IntPtr.Zero, 0x0002, 1000, out res) != IntPtr.Zero;
        }
        catch { return false; }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
