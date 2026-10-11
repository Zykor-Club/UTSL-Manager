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
        instanceMutex = new Mutex(true, InstanceKey, out bool createdNew);
        if (!createdNew)
        {
            IntPtr h = FindWindow(null, "UTSLManager");
            if (h != IntPtr.Zero) { ShowWindow(h, 9); SetForegroundWindow(h); }   // 9 = SW_RESTORE
            return;
        }

        StartupArgs = args;
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        finally { instanceMutex.ReleaseMutex(); instanceMutex.Dispose(); }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
