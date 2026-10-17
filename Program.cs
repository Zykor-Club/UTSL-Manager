using System.Runtime.InteropServices;
using Avalonia;

namespace UTSLManager;

internal static class Program
{
    private const string InstanceKey = "UTSLManager_SingleInstance_9F2A6C";
    private static Mutex? instanceMutex;

    /// <summary>启动参数（MainWindow 用来处理 --open-plugin 等深链）。</summary>
    public static string[] StartupArgs = [];

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
        if (!ownedMutex)
        {
            for (int i = 0; i < 30; i++)
            {
                IntPtr h = FindWindow(null, "UTSLManager");
                if (h != IntPtr.Zero) { ShowWindow(h, 9); SetForegroundWindow(h); return; }   // 9 = SW_RESTORE
                if (instanceMutex.WaitOne(300)) { ownedMutex = true; break; }                 // 老实例已退出 → 接盘
            }
            if (!ownedMutex) { return; }   // 确实还有活着的实例：安静退出（这才是正确行为）
        }

        StartupArgs = args;
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        finally { try { if (ownedMutex) { instanceMutex.ReleaseMutex(); } } catch { } instanceMutex.Dispose(); }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
