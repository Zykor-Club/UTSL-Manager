using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using UTSLManager.Plugins;

namespace UTSLManager;

public sealed class WorldRow
{
    public string Name { get; set; } = "";
    public string Size { get; set; } = "";
    public bool Running { get; set; }
    public IBrush DotBrush => Running ? new SolidColorBrush(Color.Parse("#32D74B")) : new SolidColorBrush(Color.Parse("#6B6B70"));
}

/// <summary>终端里的一行（文字 + 颜色）。</summary>
public sealed class TermLine
{
    public string Text { get; set; } = "";
    public IBrush Brush { get; set; } = new SolidColorBrush(Color.Parse("#B9B9C0"));
}

public partial class MainWindow : Window
{
    // ===== 连接配置（本地 connection.json，支持远端 UTSL）=====
    private static string ConnHost = "127.0.0.1";
    private static int ConnPort = 7878;
    private static string Token = "";   // ★ 公开仓库不内置任何令牌：请在「连接设置」里填自己的 REST 令牌（会 DPAPI 加密后写入 connection.json）
    private static string ManagerLanguage = "zh-CN";
    private static string Rest => "http://" + ConnHost + ":" + ConnPort;
    private static string ConnFile => Path.Combine(AppContext.BaseDirectory, "connection.json");

    private static bool migratePlainToken;   // 需要在 LoadConn 末尾把明文 token 迁移成 DPAPI 加密

    private static void LoadConn()
    {
        try
        {
            if (!File.Exists(ConnFile)) { return; }
            if (JsonNode.Parse(File.ReadAllText(ConnFile)) is not JsonObject o) { return; }
            ConnHost = o["host"]?.ToString() ?? ConnHost;
            if (int.TryParse(o["port"]?.ToString(), out int p) && p > 0) { ConnPort = p; }
            // ★ 优先读加密字段（DPAPI）；兼容旧的明文 token 字段
            string encTok = o["tokenEnc"]?.ToString() ?? "";
            if (encTok.Length > 0)
            {
                string dec = DpapiUnprotect(encTok);
                if (dec.Length > 0) { Token = dec; }
                else if (o["token"] is not null) { Token = o["token"]?.ToString() ?? Token; }
            }
            else
            {
                Token = o["token"]?.ToString() ?? Token;
                migratePlainToken = true;   // ★ 明文 token：等所有字段都读完再迁移（早期版本在这里直接 SaveConn 会把还没读到的 ssh 段写成空 → 配置被清空）
            }
            ManagerLanguage = o["language"]?.ToString() ?? ManagerLanguage;
            if (o["ssh"] is JsonObject s)
            {
                SshHost = s["host"]?.ToString() ?? SshHost;
                if (int.TryParse(s["port"]?.ToString(), out int sp) && sp > 0) { SshPort = sp; }
                SshUser = s["user"]?.ToString() ?? SshUser;
                SshKey = s["key"]?.ToString() ?? SshKey;
                RemoteDir = s["dir"]?.ToString() ?? RemoteDir;
                RemoteDotnet = s["dotnet"]?.ToString() ?? RemoteDotnet;
                RemoteLauncher = s["launcher"]?.ToString() ?? RemoteLauncher;
                if (int.TryParse(s["restPort"]?.ToString(), out int rp) && rp > 0) { RemoteRestPort = rp; }
            }
            if (o["paths"] is JsonObject pj)
            {
                LocalDir = pj["localDir"]?.ToString() ?? LocalDir;
            }
            // ★ 全部字段读完后再迁移明文 token（避免把未加载的字段写成空）
            if (migratePlainToken) { migratePlainToken = false; SaveConn(); }
        }
        catch { }
    }


    private static void SaveConn()
    {
        try
        {
            var o = new JsonObject
            {
                ["host"] = ConnHost, ["port"] = ConnPort, ["language"] = ManagerLanguage,
                ["ssh"] = new JsonObject
                {
                    ["host"] = SshHost, ["port"] = SshPort, ["user"] = SshUser, ["key"] = SshKey,
                    ["dir"] = RemoteDir, ["dotnet"] = RemoteDotnet, ["launcher"] = RemoteLauncher, ["restPort"] = RemoteRestPort
                },
                ["paths"] = new JsonObject { ["localDir"] = LocalDir }
            };
            // ★ 安全（对标 MCSManager CVE-2025-50691 的教训）：token 用 DPAPI 加密后再落盘，
            //   同机其它用户即使读到 connection.json 也解不开；只有加密不可用时才退回明文。
            string encTok = DpapiProtect(Token);
            if (encTok.Length > 0) { o["tokenEnc"] = encTok; }
            else { o["token"] = Token; }
            File.WriteAllText(ConnFile, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
    // ===== 路径配置 =====
    // 原来全是硬编码的 C:\Users\星梦\... ✗ —— 别人装了用不了。改为可在 connection.json 的 paths.localDir 里改，
    // 默认取「当前用户桌面\utsl-deploy」。
    private static string LocalDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "utsl-deploy");
    private static string Logs => Path.Combine(LocalDir, "logs");
    private static int RemoteRestPort = 7878;    // 远端 TShock REST 端口（隧道另一端）
    private static int TunnelPid;                 // 隧道 ssh 进程（避免重复启动）
    private static bool TunnelAlive;              // ★ 状态灯：隧道可用
    private static bool RestAlive;                // ★ 状态灯：REST 接口可用
    private static bool lastDotTunnel, lastDotShell, lastDotRest, dotsInit;

    // ===== 远端（SSH）配置：connection.json 里的 "ssh" 段；填了就走远端启停/读日志 =====
    private static string SshHost = "";
    private static int SshPort = 22;
    private static string SshUser = "Administrator";
    private static string SshKey = "";
    private static string RemoteDir = @"C:\utsl-deploy";
    private static string RemoteDotnet = @"C:\dotnet\dotnet.exe";
    private static string RemoteLauncher = @"C:\utsl-launch\utsl-launch.dll";
    private static bool HasSsh => SshHost.Length > 0 && SshKey.Length > 0;
    // ★ 注意：管理器是经 SSH 隧道连远端的，ConnHost 就是 127.0.0.1 —— 所以不能用 TargetIsLocal 判定，
    //   只认「connection.json 里配了 ssh 段」。想切回本地就把 ssh 段清空（连接设置里保存空值即可）。
    private static bool UseRemote => HasSsh;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly ObservableCollection<WorldRow> rows = [];
    private readonly ObservableCollection<TermLine> term = [];
    private string lastLogFile = "";
    private long logPos;
    // ★ 自检修复（卡顿大主因）：ItemsControl 不虚拟化，1500 个 TextBlock（每行还 Wrap）
    //   在任何布局变化时都要重新测量 —— 切页/拖窗口/任务栏唤出都会卡。降到 300。
    private const int MaxTermLines = 300;
    // 静态画刷缓存：之前每行都 new SolidColorBrush ✗ 1500 行就是 1500 个对象 ✓
    private static readonly Dictionary<string, IBrush> BrushCache = [];
    private static IBrush GetBrush(string hex)
    {
        if (BrushCache.TryGetValue(hex, out var b)) { return b; }
        b = new SolidColorBrush(Color.Parse(hex));
        BrushCache[hex] = b;
        return b;
    }
    private readonly List<string> history = [];
    private int histIdx = -1;

    public MainWindow()
    {
        InitializeComponent();
        RemoteShell.Report = (msg) => { try { Dispatcher.UIThread.Post(() => Term(msg, "#8A8A90")); } catch { } };   // 通道状态显示在终端，便于排障
        // ★ 传输进度显示在底部状态栏（后台线程调用 → Post 回 UI）
        TransferStatus = (msg) =>
        {
            try { Dispatcher.UIThread.Post(() => { if (ErrText is not null) { ErrText.Text = msg; } }); } catch { }
        };
        LoadConn();
        WarmupRemote();   // ★ 启动即后台预热常驻通道与常用配置，第一次点击就秒开   // ★ 必须在任何 REST 调用之前（TSManager 也是先读配置再启动 ✓）
        WorldList.ItemsSource = rows;
        TermLines.ItemsSource = term;
        Term("UTSLManager 终端已就绪 —— 上方是服务端日志流，下方可直接输入指令", "#8E8E93");
        rows.Add(new WorldRow { Name = "全部世界", Size = "", Running = true });
        WorldList.SelectedIndex = 0;
        WorldList.SelectionChanged += (_, _) =>
        {
            UpdateTarget();
            try { pluginHost?.RaiseWorldChanged((WorldList.SelectedItem as WorldRow)?.Name ?? "全部世界"); } catch { }
        };

        SendBtn.Click += async (_, _) => await SendAsync();
        CmdBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) { await SendAsync(); e.Handled = true; return; }
            if (e.Key == Avalonia.Input.Key.Up)
            {
                if (history.Count == 0) { return; }
                histIdx = Math.Min(histIdx + 1, history.Count - 1);
                CmdBox.Text = history[history.Count - 1 - histIdx];
                CmdBox.CaretIndex = CmdBox.Text?.Length ?? 0;
                e.Handled = true;
            }
            else if (e.Key == Avalonia.Input.Key.Down)
            {
                if (histIdx > 0)
                {
                    histIdx--;
                    CmdBox.Text = history[history.Count - 1 - histIdx];
                }
                else { histIdx = -1; CmdBox.Text = ""; }
                CmdBox.CaretIndex = CmdBox.Text?.Length ?? 0;
                e.Handled = true;
            }
        };

        var listTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        listTimer.Tick += async (_, _) =>
        {
            UpdateHealthDots();                       // 状态灯永远刷新（本地判断，零成本）
            if (!IsActive) { return; }                // ★ 自适应节流：窗口不在前台就跳过轮询与采样
            await RefreshWorldsAsync();
            SampleMetrics();
        };
        listTimer.Start();

        var logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        logTimer.Tick += (_, _) => { if (!IsActive) { return; } RefreshLog(); };   // ★ 失焦不刷日志
        logTimer.Start();
            // 远端模式：每 20 秒确保 SSH 隧道还活着（断了自动重建）
            var tunnelTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            tunnelTimer.Tick += (_, _) => { if (HasSsh) { _ = Task.Run(() => EnsureTunnel()); } };   // 自检修复：隧道探测也放后台
            tunnelTimer.Start();

        // ★ 150% DPI 下窗口初始位置/尺寸会算错，导致右侧跑到屏幕外（控制区看不见）
        //   这里按屏幕工作区强制钳制并居中。
        Opened += (_, _) => ClampToScreen();

        var chainTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };   // ★ 10s→30s：侧栏世界链摘要不必那么勤（且已走 90s 缓存）
        chainTimer.Tick += (_, _) =>
        {
            // 快照始终刷新（只发一次 ssh 覆盖多个页面），窗口失焦时也保持数据新鲜
            if (Environment.TickCount64 - SnapshotAtMs > 45000) { _ = Task.Run(() => RefreshSnapshot()); }
            if (!IsActive) { return; }
            RefreshChain();
        };
        chainTimer.Start();
        RefreshChain();

        if (ChkHideWorlds is not null)
        {
            ChkHideWorlds.IsCheckedChanged += (_, _) => ApplyHideWorlds();
        }
        // 服务端重启/新世界加载会重新弹窗 ✗ → 每 5 秒复查一次（仅在勾选时）
        var hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        hideTimer.Tick += (_, _) => { if (ChkHideWorlds?.IsChecked == true) { HideUtslWindows(true); } };
        hideTimer.Start();

        InitPluginHost();
        ApplyStartupArgs();
        _ = RefreshWorldsAsync();
        RefreshLog();

        // 关闭时通知插件（P0-4：保存状态 / 停后台任务 / 卸载可回收 ALC）
        Closing += (_, _) => { try { pluginHost?.Shutdown(); } catch { } };
    }

    private void UpdateTarget()
    {
        var row = WorldList.SelectedItem as WorldRow;
        string name = row?.Name ?? "全部世界";
        TargetText.Text = "目标: " + (name == "全部世界" ? "全部世界" : name);
        if (PromptText is not null) { PromptText.Text = name == "全部世界" ? "@ 全部世界" : "@ " + name; }
    }

    private async Task RefreshWorldsAsync()
    {
        try
        {
            RestAlive = false;   // ★ 状态灯：先按失败算，取到数据再置为可用
            string txt = await Http.GetStringAsync(Rest + "/world/list?token=" + Uri.EscapeDataString(Token));
            RestAlive = true;
            using var doc = JsonDocument.Parse(txt);
            var sel = (WorldList.SelectedItem as WorldRow)?.Name ?? "全部世界";
            // 收集新列表
            var incoming = new List<(string name, string size, bool running)>();
            if (doc.RootElement.TryGetProperty("worlds", out var ws))
            {
                foreach (var w in ws.EnumerateArray())
                {
                    incoming.Add((
                        w.GetProperty("name").GetString() ?? "?",
                        w.GetProperty("width").GetInt32() + "x" + w.GetProperty("height").GetInt32(),
                        w.GetProperty("running").GetBoolean()));
                }
            }
            // ★ 只有列表内容真的变了才重建 ✗ 否则每 2 秒 Clear+重建会让 ListBox 选中丢失 → 闪一下 ✓（用户反馈）
            bool changed = rows.Count != incoming.Count + 1;
            if (!changed)
            {
                for (int i = 0; i < incoming.Count; i++)
                {
                    var r = rows[i + 1];
                    if (r.Name != incoming[i].name || r.Size != incoming[i].size || r.Running != incoming[i].running) { changed = true; break; }
                }
            }
            if (changed)
            {
                rows.Clear();
                rows.Add(new WorldRow { Name = "全部世界", Size = "", Running = true });
                foreach (var (n, s, run) in incoming) { rows.Add(new WorldRow { Name = n, Size = s, Running = run }); }
                WorldList.SelectedIndex = Math.Max(0, rows.ToList().FindIndex(r => r.Name == sel));
            }
            int on = rows.Count(r => r.Running) - 1;
            ConnState.Text = $"{on} 世界";
            if (SrvDot is not null) { SrvDot.Background = new SolidColorBrush(Color.Parse("#32D74B")); }
            serverWasUp = true;
            downTicks = 0;
            watchdogFailures = 0;
            ErrText.Text = "";
            if (changed) { try { pluginHost?.RaiseWorldListRefreshed(RowsSnapshot()); } catch { } }
            NotifyServerState(true);
        }
        catch (Exception ex)
        {
            ConnState.Text = ServerRunning ? "REST 无响应" : "未运行";
            if (SrvDot is not null) { SrvDot.Background = new SolidColorBrush(Color.Parse(ServerRunning ? "#FEBC2E" : "#6B6B70")); }
            // 看门狗：本来在跑 → 现在没了 → 自动拉起
            downTicks++;
            // ★ 存活判据改成「进程是否还在」，不再用「REST 通不通」——
            //   世界加载/换图期间 REST 本来就没就绪，用 REST 判会把"正在加载"误判成"掉线"→ 重启循环（调研 D-6 的坑）。
            bool procAlive = UseRemote ? SnapshotProcs > 0 : ServerRunning;
            if (procAlive)
            {
                downTicks = 0;      // 进程还在 → 只是 REST 未就绪（加载中），看门狗不介入
            }
            else if (ChkWatchdog?.IsChecked == true && serverWasUp && downTicks >= 2)
            {
                downTicks = 0;
                // ★ 换图保护窗口：刚启动/重启/整链重置后，世界加载需要时间（大图可达数分钟），期间不自动重启
                if (Environment.TickCount64 < watchdogProtectUntilMs)
                {
                    int left = (int)((watchdogProtectUntilMs - Environment.TickCount64) / 1000);
                    ERR($"看门狗：进程不在，但仍在启动/换图保护窗口内（剩余约 {left} 秒），暂不自动重启");
                }
                else if (UseRemote && TargetIsLocal == false)
                {
                    // ★ 远端也能自动恢复了（以前只警告不重启；现在启停链路已打通）
                    watchdogFailures++;
                    if (watchdogFailures >= 3)
                    {
                        ChkWatchdog!.IsChecked = false;
                        watchdogFailures = 0;
                        ERR("⚠ 看门狗熔断：连续 3 次远端自动重启后进程仍未存活，已停止重试（请手动检查）");
                    }
                    else
                    {
                        ERR($"看门狗：远端服务端进程消失，正在自动重启…（第 {watchdogFailures}/3 次）");
                        ProtectWatchdog(600, "watchdog-remote");
                        _ = Task.Run(() =>
                        {
                            try
                            {
                                string r = SshRun(RemoteStartScript(), 60000);
                                Dispatcher.UIThread.Post(() => ERR("看门狗：远端重启指令已下发"));
                            }
                            catch (Exception ex) { Dispatcher.UIThread.Post(() => ERR("看门狗：远端重启失败 " + ex.Message)); }
                        });
                    }
                }
                else
                {
                    watchdogFailures++;
                    if (watchdogFailures >= 3)
                    {
                        // 连续 3 次拉起失败 → 熔断：停止重试并告警（移植自 TSManager 的看门狗策略）
                        ChkWatchdog!.IsChecked = false;
                        watchdogFailures = 0;
                        ERR("⚠ 看门狗熔断：连续 3 次自动重启后服务端仍未存活，已停止重试（请手动检查并发起）");
                    }
                    else
                    {
                        ERR($"看门狗：检测到服务端退出，正在自动重启…（第 {watchdogFailures}/3 次）");
                        OnStartServer(null, new RoutedEventArgs());
                    }
                }
            }
            if (serverWasUp) { NotifyServerState(false); }
        }
    }

    /// <summary>往终端追加一行（带颜色 + 上限 + 自动滚动）。</summary>
    private void Term(string text, string color)
    {
        // ★ 修 bug#3：插件命令在 Task.Run 里执行、后台任务异常也会走 ctx.Log → Term，
        //   直接改绑定到 ItemsControl 的 ObservableCollection 会实抛
        //   InvalidOperationException: The calling thread cannot access this object...
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Term(text, color));
            return;
        }
        term.Add(new TermLine { Text = text, Brush = GetBrush(color) });
        if (term.Count > MaxTermLines)
        {
            int drop = term.Count - MaxTermLines;
            for (int i = 0; i < drop; i++) { term.RemoveAt(0); }
        }
        // 事件总线（P1-5）：没有插件订阅时零开销；再入保护防止插件在处理里又写日志造成递归
        if (!raisingTerminal && pluginHost is not null && pluginHost.Events.HasTerminalLineSubscribers)
        {
            raisingTerminal = true;
            try { pluginHost.RaiseTerminalLine(text, LevelOf(color)); }
            catch { }
            finally { raisingTerminal = false; }
        }
        if (ChkAutoScroll?.IsChecked == true)
        {
            Dispatcher.UIThread.Post(() =>
            {
                try { if (term.Count > 0) { TermLines.ScrollIntoView(term[^1]); } } catch { }
            }, DispatcherPriority.Background);
        }
    }

    private static string LevelOf(string color) => color switch
    {
        "#FF7B72" => "error",
        "#E3B341" => "warning",
        "#7EE787" => "success",
        "#9AA0A6" or "#8E8E93" => "info",
        "#6E7681" => "debug",
        _ => "normal",
    };

    private static string PickColor(string line)
    {
        if (line.Contains("[Error]")) { return "#FF7B72"; }
        if (line.Contains("[Warning]") || line.Contains("[Warn]")) { return "#E3B341"; }
        if (line.Contains("[Success]")) { return "#7EE787"; }
        if (line.Contains("[Info]")) { return "#9AA0A6"; }
        if (line.Contains("[Debug]")) { return "#6E7681"; }
        return "#B9B9C0";
    }

    // ===== 统一文件访问：本机模式直接读盘；远端模式走 SSH（列表/读/写/下载/改名）=====
    private static string FsRoot => UseRemote ? RemoteDir : LocalDir;
    private static string Q(string s) => s.Replace("'", "''");
    /// <summary>C:\a\b → /C:/a/b（Windows OpenSSH 的 scp 远端路径必须带前导斜杠）</summary>
    private static string ScpPath(string winPath)
    {
        string p = winPath.Replace('\\', '/');
        return (p.Length > 1 && p[1] == ':') ? "/" + p : p;
    }
    private sealed record FsEntry(string Name, bool IsDir, long Size, DateTime Mtime);

    // ===== 短 TTL 缓存（5 秒）：重复访问同一路径瞬时返回；任何写操作清空（见 FsInvalidate）=====
    private sealed class FsMemoEntry { public string? Text; public List<FsEntry>? List; public bool? Bool; public long AtMs; }
    private static readonly Dictionary<string, FsMemoEntry> FsMemo = new();
    private const int FsMemoTtlMs = 90000;   // ★ 远端每次读取约 1.5 秒 → 缓存 90 秒，来回切页面零等待
    private static void FsInvalidate() { lock (FsMemo) { FsMemo.Clear(); } }
    private static string FsKey(string op, string path) => op + "|" + path.ToLowerInvariant();

    /// <summary>稳健 JSON 解析：空/坏内容返回 null 而不抛异常（避免把整个视图的后续代码一起打断）。</summary>
    private static JsonObject? ParseJsonObj(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        try { return JsonNode.Parse(text) as JsonObject; } catch { return null; }
    }

    private static long SnapshotAtMs;                 // 上次快照时间
    // ★ 远端指标（来自合并快照，不额外发 ssh）：相邻两次快照算速率 —— 监控的才是被管的那台服务器
    private static long SnapPrevCpuMs = -1, SnapPrevNetSent, SnapPrevNetRecv, SnapPrevAtMs;
    private static long SnapMemBytes;
    private static double SnapCpuPct, SnapUpKbps, SnapDownKbps;
    private static bool SnapHasRate;
    private static int samplesSeen;                   // 本机模式：首帧只记基线，避免"开机至今总CPU"假尖峰
    private static int SnapshotProcs;                 // 快照里的服务端进程数
    private static string SnapshotListen = "";        // 快照里的监听端口

    /// <summary>
    /// 一次 ssh 拿回所有常用数据（进程数/监听端口/config.json/EdgeStitch.json/根目录列表），
    /// 直接写进 FsMemo 缓存 —— 之后三个页面读缓存即刻返回，不需要各自再发一次 ssh（每次 3~8 秒）。
    /// </summary>
    private static void RefreshSnapshot()
    {
        try
        {
            string root = FsRoot;
            string cfg = Path.Combine(root, "config", "config.json");
            string edge = Path.Combine(root, "config", "EdgeStitch", "EdgeStitch.json");
            if (!UseRemote)
            {
                // 本机模式：直接读，无需 ssh
                lock (FsMemo)
                {
                    FsMemo[FsKey("read", cfg)] = new FsMemoEntry { Text = File.Exists(cfg) ? File.ReadAllText(cfg) : "", AtMs = Environment.TickCount64 };
                    FsMemo[FsKey("read", edge)] = new FsMemoEntry { Text = File.Exists(edge) ? File.ReadAllText(edge) : "", AtMs = Environment.TickCount64 };
                }
                SnapshotAtMs = Environment.TickCount64;
                return;
            }
            string script =
                "$ProgressPreference='SilentlyContinue'; " +
                "$b='" + Q(root) + "'; $o=[ordered]@{}; " +
                "$o.p=((Get-Process UnifierTSL -ErrorAction SilentlyContinue)|Measure-Object).Count; " +
                "$o.l=((Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue|Where-Object{$_.LocalPort -in 7788," + RemoteRestPort + "})|ForEach-Object{$_.LocalPort}) -join ','; " +
                "$c=Join-Path (Join-Path $b 'config') 'config.json'; if(Test-Path -LiteralPath $c){$o.c=[Convert]::ToBase64String([IO.File]::ReadAllBytes($c))}else{$o.c=''}; " +
                "$e=Join-Path (Join-Path (Join-Path $b 'config') 'EdgeStitch') 'EdgeStitch.json'; if(Test-Path -LiteralPath $e){$o.e=[Convert]::ToBase64String([IO.File]::ReadAllBytes($e))}else{$o.e=''}; " +
                "$o.r=@(Get-ChildItem -LiteralPath $b -ErrorAction SilentlyContinue|ForEach-Object{ $(if($_.PSIsContainer){'D'}else{'F'})+'|'+$_.Length+'|'+$_.LastWriteTime.ToString('o')+'|'+$_.Name }); " +
                "$md=Join-Path (Join-Path $b 'GenerateMap') 'Images'; $o.m=@(if(Test-Path -LiteralPath $md){Get-ChildItem -LiteralPath $md -Filter *.png -ErrorAction SilentlyContinue|ForEach-Object{'F|'+$_.Length+'|'+$_.LastWriteTime.ToString('o')+'|'+$_.Name}}); " +
                "$o.d=[ordered]@{}; foreach($n in @('config','Worlds','ServerPlugins','logs')){ $p=Join-Path $b $n; if(Test-Path -LiteralPath $p){ $o.d[$n]=@(Get-ChildItem -LiteralPath $p -ErrorAction SilentlyContinue|ForEach-Object{ $(if($_.PSIsContainer){'D'}else{'F'})+'|'+$_.Length+'|'+$_.LastWriteTime.ToString('o')+'|'+$_.Name }) } }; " +
                "$u=Get-Process UnifierTSL -ErrorAction SilentlyContinue|Select-Object -First 1; " +
                "if($u){$o.uc=[long]$u.TotalProcessorTime.TotalMilliseconds;$o.um=[long]$u.PrivateMemorySize64}else{$o.uc=-1;$o.um=0}; " +
                "$ns=0;$nr=0; foreach($ni in [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()){ " +
                "  if($ni.OperationalStatus -ne 'Up'){continue}; if($ni.NetworkInterfaceType -eq 'Loopback'){continue}; " +
                "  $st=$ni.GetIPStatistics(); $ns+=[long]$st.BytesSent; $nr+=[long]$st.BytesReceived }; " +
                "$o.ns=$ns;$o.nr=$nr; " +
                "$o|ConvertTo-Json -Depth 3 -Compress";
            string raw2 = SshRun(script, 60000);
            if (raw2.Length == 0) { return; }
            int lb = raw2.IndexOf('{');
            if (lb > 0) { raw2 = raw2.Substring(lb); }
            if (JsonNode.Parse(raw2) is not JsonObject o) { return; }
            string cfgText = DecB64(o["c"]?.ToString());
            string edgeText = DecB64(o["e"]?.ToString());
            var entries = new List<FsEntry>();
            if (o["r"] is JsonArray arr)
            {
                foreach (var it in arr)
                {
                    var parts = (it?.ToString() ?? "").Split('|', 4);
                    if (parts.Length < 4) { continue; }
                    long size = long.TryParse(parts[1], out long s2) ? s2 : 0;
                    DateTime mt = DateTime.TryParse(parts[2], out DateTime m2) ? m2 : DateTime.MinValue;
                    entries.Add(new FsEntry(parts[3], parts[0] == "D", size, mt));
                }
            }
            else if (o["r"] is JsonValue single)
            {
                var parts = (single.ToString() ?? "").Split('|', 4);
                if (parts.Length >= 4)
                {
                    long size = long.TryParse(parts[1], out long s2) ? s2 : 0;
                    DateTime mt = DateTime.TryParse(parts[2], out DateTime m2) ? m2 : DateTime.MinValue;
                    entries.Add(new FsEntry(parts[3], parts[0] == "D", size, mt));
                }
            }
            long now = Environment.TickCount64;
            lock (FsMemo)
            {
                FsMemo[FsKey("read", cfg)] = new FsMemoEntry { Text = cfgText, AtMs = now };
                FsMemo[FsKey("read", edge)] = new FsMemoEntry { Text = edgeText, AtMs = now };
                FsMemo[FsKey("exists", cfg)] = new FsMemoEntry { Bool = cfgText.Length > 0, AtMs = now };
                FsMemo[FsKey("exists", edge)] = new FsMemoEntry { Bool = edgeText.Length > 0, AtMs = now };
                if (entries.Count > 0)
                {
                    FsMemo[FsKey("list", root)] = new FsMemoEntry { List = entries, AtMs = now };
                    FsMemo[FsKey("exists", root)] = new FsMemoEntry { Bool = true, AtMs = now };
                }
            }
            // 地图 PNG 列表也写进缓存 → 地图页选图不再单独发 ssh
            var mapEntries = new List<FsEntry>();
            if (o["m"] is JsonArray marr)
            {
                foreach (var it in marr)
                {
                    var parts = (it?.ToString() ?? "").Split('|', 4);
                    if (parts.Length < 4) { continue; }
                    long size = long.TryParse(parts[1], out long s2) ? s2 : 0;
                    DateTime mt = DateTime.TryParse(parts[2], out DateTime m2) ? m2 : DateTime.MinValue;
                    mapEntries.Add(new FsEntry(parts[3], false, size, mt));
                }
            }
            if (mapEntries.Count > 0) { lock (FsMemo) { FsMemo[FsKey("list", MapRemoteDir)] = new FsMemoEntry { List = mapEntries, AtMs = now }; } }
            // ★ 常用子目录（config/Worlds/ServerPlugins/logs）也一并进缓存 → 进子目录同样是毫秒级
            if (o["d"] is JsonObject dirmap)
            {
                foreach (var kv in dirmap)
                {
                    var sub = new List<FsEntry>();
                    if (kv.Value is JsonArray sa)
                    {
                        foreach (var it in sa)
                        {
                            var parts = (it?.ToString() ?? "").Split('|', 4);
                            if (parts.Length < 4) { continue; }
                            long s3 = long.TryParse(parts[1], out long ss) ? ss : 0;
                            DateTime m3 = DateTime.TryParse(parts[2], out DateTime mm) ? mm : DateTime.MinValue;
                            sub.Add(new FsEntry(parts[3], parts[0] == "D", s3, m3));
                        }
                    }
                    else if (kv.Value is JsonValue sv)
                    {
                        var parts = (sv.ToString() ?? "").Split('|', 4);
                        if (parts.Length >= 4)
                        {
                            long s3 = long.TryParse(parts[1], out long ss) ? ss : 0;
                            DateTime m3 = DateTime.TryParse(parts[2], out DateTime mm) ? mm : DateTime.MinValue;
                            sub.Add(new FsEntry(parts[3], parts[0] == "D", s3, m3));
                        }
                    }
                    if (sub.Count > 0)
                    {
                        string subPath = Path.Combine(root, kv.Key);
                        lock (FsMemo)
                        {
                            FsMemo[FsKey("list", subPath)] = new FsMemoEntry { List = sub, AtMs = now };
                            FsMemo[FsKey("exists", subPath)] = new FsMemoEntry { Bool = true, AtMs = now };
                        }
                    }
                }
            }
            // ★ 远端指标：用相邻快照算速率（首次只记基线，不产生假尖峰）
            long at = Environment.TickCount64;
            long cpuMs = long.TryParse(o["uc"]?.ToString(), out long uc) ? uc : -1;
            long memB = long.TryParse(o["um"]?.ToString(), out long um) ? um : 0;
            long ns2 = long.TryParse(o["ns"]?.ToString(), out long nb1) ? nb1 : 0;
            long nr2 = long.TryParse(o["nr"]?.ToString(), out long nb2) ? nb2 : 0;
            if (SnapPrevCpuMs >= 0 && cpuMs >= 0 && SnapPrevAtMs > 0)
            {
                double dtMs = Math.Max(500, at - SnapPrevAtMs);
                SnapCpuPct = Math.Max(0, (cpuMs - SnapPrevCpuMs) / dtMs * 100.0);
                SnapUpKbps = Math.Max(0, (ns2 - SnapPrevNetSent) / 1024.0 / (dtMs / 1000.0));
                SnapDownKbps = Math.Max(0, (nr2 - SnapPrevNetRecv) / 1024.0 / (dtMs / 1000.0));
                SnapHasRate = true;
            }
            SnapPrevCpuMs = cpuMs; SnapMemBytes = memB;
            SnapPrevNetSent = ns2; SnapPrevNetRecv = nr2; SnapPrevAtMs = at;
            SnapshotProcs = int.TryParse(o["p"]?.ToString(), out int pc) ? pc : 0;
            SnapshotListen = o["l"]?.ToString() ?? "";
            SnapshotAtMs = now;
        }
        catch { }
    }

    private static string DecB64(string? b64)
    {
        if (string.IsNullOrEmpty(b64)) { return ""; }
        try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64)); } catch { return ""; }
    }

    /// <summary>启动/切换连接后后台预热：隧道 + 一次合并快照（进程/端口/配置/目录），让页面秒开。</summary>
    private void WarmupRemote()
    {
        if (!HasSsh) { return; }
        _ = Task.Run(() =>
        {
            try
            {
                EnsureTunnel();
                // ★ 合并请求（对标面板的"批量合并"）：一次 ssh 取回 进程/端口/启动器配置/世界链配置/根目录，
                //   写进 90 秒缓存 → 之后 文件管理/配置编辑/世界链 三个页面读缓存，都是毫秒级。
                //   这比"常驻命令通道"稳妥得多（那条路已验证不可靠，见 SshRunInner 的说明）。
                RefreshSnapshot();
                // ★ 地图缩略图预热：默认预览用的是"拼接图"，首次生成要解码 11958px 原图（约 5 秒），
                //   在这里后台先做好，用户点开地图时直接命中缩略图（0.7MB / 约 2 秒），不用等生成。
                try
                {
                    var pngs = FsList(MapRemoteDir).Where(e => !e.IsDir && e.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                                                            && !e.Name.EndsWith(".thumb.png", StringComparison.OrdinalIgnoreCase)).ToList();
                    var newest = pngs.Where(e => e.Name.Contains("stitched")).OrderByDescending(e => e.Mtime).FirstOrDefault()
                                 ?? pngs.OrderByDescending(e => e.Mtime).FirstOrDefault();
                    if (newest is not null) { EnsureRemoteThumb(Path.Combine(MapRemoteDir, newest.Name)); }
                }
                catch { }
            }
            catch { }
        });
    }

    private static List<FsEntry> FsList(string dir)
    {
        string key = FsKey("list", dir);
        lock (FsMemo) { if (FsMemo.TryGetValue(key, out var m) && m.List is not null && Environment.TickCount64 - m.AtMs < FsMemoTtlMs) { return m.List; } }
        var list = new List<FsEntry>();
        if (!UseRemote)
        {
            if (!Directory.Exists(dir)) { return list; }
            foreach (string d in Directory.GetDirectories(dir)) { var di = new DirectoryInfo(d); list.Add(new FsEntry(di.Name, true, 0, di.LastWriteTime)); }
            foreach (string fn in Directory.GetFiles(dir)) { var fi = new FileInfo(fn); list.Add(new FsEntry(fi.Name, false, fi.Length, fi.LastWriteTime)); }
            lock (FsMemo) { FsMemo[key] = new FsMemoEntry { List = list, AtMs = Environment.TickCount64 }; }
            return list;
        }
        string outp = SshRun("$ProgressPreference='SilentlyContinue';[Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
            "Get-ChildItem -LiteralPath '" + Q(dir) + "' -ErrorAction SilentlyContinue | ForEach-Object { $(if ($_.PSIsContainer) { 'D' } else { 'F' }) + '|' + $_.Length + '|' + $_.LastWriteTime.ToString('o') + '|' + $_.Name }", 25000);
        foreach (string ln in outp.Split('\n'))
        {
            var parts = ln.Trim().Split('|', 4);
            if (parts.Length < 4) { continue; }
            long size = long.TryParse(parts[1], out long s2) ? s2 : 0;
            DateTime mt = DateTime.TryParse(parts[2], out DateTime m2) ? m2 : DateTime.MinValue;
            list.Add(new FsEntry(parts[3], parts[0] == "D", size, mt));
        }
        lock (FsMemo) { FsMemo[key] = new FsMemoEntry { List = list, AtMs = Environment.TickCount64 }; }
        return list;
    }

    private static bool FsExists(string path)
    {
        string key = FsKey("exists", path);
        lock (FsMemo) { if (FsMemo.TryGetValue(key, out var m) && m.Bool is bool bv && Environment.TickCount64 - m.AtMs < FsMemoTtlMs) { return bv; } }
        if (!UseRemote)
        {
            bool lb = File.Exists(path) || Directory.Exists(path);
            lock (FsMemo) { FsMemo[key] = new FsMemoEntry { Bool = lb, AtMs = Environment.TickCount64 }; }
            return lb;
        }
        string r = SshRun("$ProgressPreference='SilentlyContinue'; if (Test-Path -LiteralPath '" + Q(path) + "') { 'YES' } else { 'NO' }", 20000);
        bool rb = r.Contains("YES");
        lock (FsMemo) { FsMemo[key] = new FsMemoEntry { Bool = rb, AtMs = Environment.TickCount64 }; }
        return rb;
    }

    private static string FsReadText(string path)
    {
        string key = FsKey("read", path);
        lock (FsMemo) { if (FsMemo.TryGetValue(key, out var m) && m.Text is not null && Environment.TickCount64 - m.AtMs < FsMemoTtlMs) { return m.Text; } }
        if (!UseRemote)
        {
            string lt = File.Exists(path) ? File.ReadAllText(path) : "";
            lock (FsMemo) { FsMemo[key] = new FsMemoEntry { Text = lt, AtMs = Environment.TickCount64 }; }
            return lt;
        }
        string b64 = SshRun("$ProgressPreference='SilentlyContinue';[Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
            "if (Test-Path -LiteralPath '" + Q(path) + "') { [Convert]::ToBase64String([IO.File]::ReadAllBytes('" + Q(path) + "')) }", 25000).Trim();
        if (b64.Length == 0) { return ""; }
        string rt;
        try { rt = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64)); } catch { rt = ""; }
        lock (FsMemo) { FsMemo[key] = new FsMemoEntry { Text = rt, AtMs = Environment.TickCount64 }; }
        return rt;
    }

    private static bool FsWriteText(string path, string text)
    {
        FsInvalidate();
        if (!UseRemote) { File.WriteAllText(path, text); return true; }
        string b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));
        const int CH = 1400;   // EncodedCommand 命令行有长度上限，分块追加
        // ★ 原子写（移植面板的"临时文件 → 原子 rename"）：先写 .tmp，全部写完再改名覆盖，
        //   避免中途失败/断电留下半个配置文件（原实现直接原地覆写目标文件）。
        string tmp = path + ".tmp";
        SshRun("$ProgressPreference='SilentlyContinue'; [IO.File]::WriteAllBytes('" + Q(tmp) + "', [Convert]::FromBase64String('" + b64.Substring(0, Math.Min(CH, b64.Length)) + "'))", 30000);
        for (int i = CH; i < b64.Length; i += CH)
        {
            string chunk = b64.Substring(i, Math.Min(CH, b64.Length - i));
            SshRun("$ProgressPreference='SilentlyContinue'; $p='" + Q(tmp) + "'; $b=[IO.File]::ReadAllBytes($p); [IO.File]::WriteAllBytes($p, $b + [Convert]::FromBase64String('" + chunk + "'))", 30000);
        }
        SshRun("$ProgressPreference='SilentlyContinue'; Move-Item -LiteralPath '" + Q(tmp) + "' -Destination '" + Q(path) + "' -Force; 'ok'", 30000);
        return true;
    }

    private static bool FsMkdir(string dir)
    {
        FsInvalidate();
        if (!UseRemote) { Directory.CreateDirectory(dir); return true; }
        SshRun("$ProgressPreference='SilentlyContinue'; New-Item -ItemType Directory -Force -Path '" + Q(dir) + "' | Out-Null; 'ok'", 20000);
        return true;
    }

    private static bool FsRename(string from, string to)
    {
        FsInvalidate();
        if (!UseRemote) { File.Move(from, to); return true; }
        SshRun("$ProgressPreference='SilentlyContinue'; Move-Item -LiteralPath '" + Q(from) + "' -Destination '" + Q(to) + "'; 'ok'", 20000);
        return true;
    }

    private static bool FsDelete(string path)
    {
        FsInvalidate();
        if (!UseRemote) { try { if (Directory.Exists(path)) { Directory.Delete(path, true); } else { File.Delete(path); } return true; } catch { return false; } }
        SshRun("$ProgressPreference='SilentlyContinue'; Remove-Item -LiteralPath '" + Q(path) + "' -Recurse -Force -ErrorAction SilentlyContinue; 'ok'", 30000);
        return true;
    }

    /// <summary>本地 → 远端（scp；远端路径用 /C:/ 形式）</summary>
    private static bool FsUpload(string localPath, string remotePath)
    {
        FsInvalidate();
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("scp") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(SshKey);
            psi.ArgumentList.Add("-P"); psi.ArgumentList.Add(SshPort.ToString());
            psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("BatchMode=yes");
            psi.ArgumentList.Add(localPath);
            psi.ArgumentList.Add(SshUser + "@" + SshHost + ":" + ScpPath(remotePath));
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) { return false; }
            // ★ 上传进度：scp 不回传进度，这里用「本地文件大小 + 已用时间 + 上一轮实测的带宽」给出诚实的估算与取消入口
            transferCancel = false;
            long size = 0; try { size = new FileInfo(localPath).Length; } catch { }
            var sw2 = System.Diagnostics.Stopwatch.StartNew();
            while (!p.WaitForExit(500))
            {
                if (transferCancel) { try { p.Kill(true); } catch { } Progress("传输已取消"); return false; }
                if (sw2.ElapsedMilliseconds > 600000) { try { p.Kill(true); } catch { } Progress("上传超时（600 秒）"); return false; }
                double secs2 = sw2.Elapsed.TotalSeconds;
                Progress($"上传 {size / 1048576.0:F1} MB · 已用 {secs2:F0} 秒 · 链路约 2.4 MB/s（可点「取消传输」）");
            }
            Progress("");
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>远端 → 本地（走 scp；远端路径用 /C:/ 形式）</summary>
    private static bool FsDownload(string remotePath, string localPath, long knownTotal = 0)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("scp") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(SshKey);
            psi.ArgumentList.Add("-P"); psi.ArgumentList.Add(SshPort.ToString());
            psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("BatchMode=yes");
            psi.ArgumentList.Add(SshUser + "@" + SshHost + ":" + ScpPath(remotePath));
            psi.ArgumentList.Add(localPath);
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) { return false; }
            // ★ 进度：scp 边传边写目标文件，轮询本地文件大小即可得到真实进度 + 预计剩余时间；并支持取消
            transferCancel = false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long total = knownTotal;
            while (!p.WaitForExit(400))
            {
                if (transferCancel) { try { p.Kill(true); } catch { } Progress("传输已取消"); return false; }
                if (sw.ElapsedMilliseconds > 180000) { try { p.Kill(true); } catch { } Progress("传输超时（180 秒）"); return false; }
                long done = 0;
                try { var fi = new FileInfo(localPath); if (fi.Exists) { done = fi.Length; } } catch { }
                double secs = Math.Max(0.2, sw.Elapsed.TotalSeconds);
                double mbps = done / secs / 1048576.0;
                if (total > 0)
                {
                    int eta = mbps > 0.01 ? (int)((total - done) / 1048576.0 / mbps) : 0;
                    Progress($"下载 {done / 1048576.0:F1}/{total / 1048576.0:F1} MB · {mbps:F2} MB/s · 剩余约 {Math.Max(0, eta)} 秒（可点「取消传输」）");
                }
                else { Progress($"下载 {done / 1048576.0:F1} MB · {mbps:F2} MB/s（可点「取消传输」）"); }
            }
            Progress("");
            return File.Exists(localPath);
        }
        catch { return false; }
    }

    /// <summary>确保 SSH 隧道在跑：本地 ConnPort → 远端 RemoteRestPort。已在监听就不重复启动。</summary>
    /// <summary>
    /// 常驻命令通道的**原始探针**：自己做一遍建连/自检/两次命令，输出每步耗时与原始回包行。
    /// 用途：当"通道没被用上"时定位到底卡在哪一步（建连？自检？第一次命令？第二次命令？）。
    /// </summary>
    public static string ProbeChannel()
    {
        var sb = new System.Text.StringBuilder();
        if (!HasSsh) { return "（本机模式，无需通道）" + Environment.NewLine; }
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("ssh")
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                StandardInputEncoding = new System.Text.UTF8Encoding(false),
            };
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(SshKey);
            psi.ArgumentList.Add("-p"); psi.ArgumentList.Add(SshPort.ToString());
            psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("BatchMode=yes");
            psi.ArgumentList.Add(SshUser + "@" + SshHost);
            string loop =
                "$ProgressPreference='SilentlyContinue';[Console]::OutputEncoding=[Text.Encoding]::UTF8;" +
                "while($true){$line=[Console]::In.ReadLine();if($null -eq $line){break};if($line -eq '__EXIT__'){break};" +
                "$id=$line.Substring(0,8);$b64=$line.Substring(8);" +
                "$code=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($b64));" +
                "try{$out=(& ([ScriptBlock]::Create($code)) 2>&1 | Out-String)}catch{$out='ERR: '+$_.Exception.Message};" +
                "[Console]::Out.WriteLine('##B'+$id);[Console]::Out.Write($out);[Console]::Out.WriteLine();[Console]::Out.WriteLine('##E'+$id);[Console]::Out.Flush()}";
            psi.ArgumentList.Add("powershell -NoProfile -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(loop)));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var p = System.Diagnostics.Process.Start(psi);
            if (p is null) { return "启动 ssh 失败" + Environment.NewLine; }
            sb.AppendLine("  ssh 进程已起 " + sw.ElapsedMilliseconds + " ms");

            // 后台排空 stderr（不排空可能把它填满导致远端阻塞 —— 这是本次要验证的怀疑点之一）
            var errLines = new System.Collections.Concurrent.ConcurrentQueue<string>();
            _ = Task.Run(() => { try { string? l; while ((l = p.StandardError.ReadLine()) is not null) { errLines.Enqueue(l); } } catch { } });

            var probeQ = new System.Collections.Concurrent.BlockingCollection<string>();
            _ = Task.Run(() => { try { string? l; while ((l = p.StandardOutput.ReadLine()) is not null) { probeQ.Add(l); } } catch { } finally { try { probeQ.CompleteAdding(); } catch { } } });

            int Exchange(string tag, string code, int budgetMs)
            {
                string id = (sw.ElapsedMilliseconds % 100000000).ToString("D8");
                var swx = System.Diagnostics.Stopwatch.StartNew();
                p.StandardInput.WriteLine(id + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(code)));
                p.StandardInput.Flush();
                int raw = 0; string last = "";
                while (swx.ElapsedMilliseconds < budgetMs)
                {
                    if (!probeQ.TryTake(out string? ln, 2000))
                    {
                        if (probeQ.IsCompleted) { sb.AppendLine($"  {tag}: 流结束（收到 {raw} 行）"); return 1; }
                        continue;   // 只是暂时没有新行，继续等
                    }
                    raw++;
                    last = ln;
                    if (ln == "##E" + id) { sb.AppendLine($"  {tag}: 成功 {swx.ElapsedMilliseconds} ms（{raw} 行，末行 {last}）"); return 0; }
                }
                sb.AppendLine($"  {tag}: 超预算 {budgetMs} ms（收到 {raw} 行，末行 {last}）");
                return 1;
            }

            Exchange("第1次命令(warm-up)", "'warm'", 15000);
            Exchange("第2次命令", "Get-Date | Out-String", 4000);
            Exchange("第3次命令", "(Get-ChildItem '" + RemoteDir + "' | Measure-Object).Count", 4000);

            var errs = errLines.ToArray();
            sb.AppendLine("  stderr 行数: " + errs.Length + (errs.Length > 0 ? "  首行: " + (errs[0].Length > 100 ? errs[0][..100] : errs[0]) : ""));
            sb.AppendLine("  进程存活: " + !p.HasExited);
            try { p.Kill(true); } catch { }
            return sb.ToString();
        }
        catch (Exception ex) { return "  探针异常: " + ex.Message + Environment.NewLine; }
    }

    /// <summary>
    /// 无界面自检：把各页面/接口的**真实耗时**跑一遍并汇总成报告。
    /// 用途：以后调优/排障都有毫秒级数据，不必再靠"模拟点击 + 截图"（那条路不可靠）。
    /// 由 Program.cs 的 --selfcheck 调用，结果写到 exe 同目录的 selfcheck.txt。
    /// </summary>
    public static string RunSelfCheck()
    {
        var sb = new System.Text.StringBuilder();
        var total = System.Diagnostics.Stopwatch.StartNew();
        int pass = 0, fail = 0, skip = 0;
        sb.AppendLine("UTSLManager 自检报告  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine(new string('-', 72));

        void Run(string name, Func<(bool ok, string detail)> body)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var (ok, detail) = body();
                sw.Stop();
                if (ok) { pass++; sb.AppendLine($"  [OK]   {name,-22} {sw.ElapsedMilliseconds,7} ms   {detail}"); }
                else { fail++; sb.AppendLine($"  [FAIL] {name,-22} {sw.ElapsedMilliseconds,7} ms   {detail}"); }
            }
            catch (Exception ex)
            {
                sw.Stop(); fail++;
                string m = ex.Message.Length > 70 ? ex.Message[..70] : ex.Message;
                sb.AppendLine($"  [FAIL] {name,-22} {sw.ElapsedMilliseconds,7} ms   {m}");
            }
        }
        void SkipLine(string name, string detail)
        {
            skip++;
            sb.AppendLine($"  [SKIP] {name,-22} {"-",7}      {detail}");
        }

        // 1) 连接配置
        Run("连接配置", () =>
        {
            LoadConn();
            return HasSsh
                ? (true, $"远端模式 {SshUser}@{SshHost}:{SshPort} 目录 {RemoteDir}")
                : (true, $"本机模式 {ConnHost}:{ConnPort} 目录 {LocalDir}");
        });

        // 2) 隧道端口
        Run("隧道端口", () =>
        {
            bool ok = false;
            try { var tc = new System.Net.Sockets.TcpClient(); ok = tc.ConnectAsync("127.0.0.1", ConnPort).Wait(1500); tc.Close(); } catch { }
            return ok ? (true, $"127.0.0.1:{ConnPort} 可连接") : (false, $"127.0.0.1:{ConnPort} 不可连接（隧道未起？）");
        });

        // 3) REST 世界列表（管理器世界列表用的就是这个接口）
        Run("REST 世界列表", () =>
        {
            string txt = Http.GetStringAsync(Rest + "/world/list?token=" + Uri.EscapeDataString(Token)).GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(txt);
            int n = doc.RootElement.TryGetProperty("worlds", out var ws) ? ws.GetArrayLength() : 0;
            return (true, $"HTTP 200，{n} 个世界");
        });

        // 3.5) 合并快照：一次 ssh 覆盖 进程/端口/配置/目录（页面读缓存即刻返回）
        Run("合并快照(仅1次ssh)", () =>
        {
            RefreshSnapshot();
            bool ok = SnapshotAtMs > 0;
            return (ok, $"进程={SnapshotProcs} 端口[{SnapshotListen}]（此后各页面读缓存）");
        });

        // 4) 远端/本机 读取配置文件
        Run("读取 config.json", () =>
        {
            string txt = FsReadText(Path.Combine(FsRoot, "config", "config.json"));
            return txt.Length > 0 ? (true, $"{txt.Length} 字符") : (false, "返回空（文件不存在或读取失败）");
        });

        // 5) 目录列表
        Run("列根目录", () =>
        {
            var list = FsList(FsRoot);
            return (list.Count > 0, $"{list.Count} 个条目（{list.Count(e => e.IsDir)} 目录 / {list.Count(e => !e.IsDir)} 文件）");
        });

        // 6) 地图图片与缩略图
        Run("地图图片", () =>
        {
            var pngs = FsList(MapRemoteDir).Where(e => !e.IsDir && e.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).ToList();
            if (pngs.Count == 0) { return (false, MapRemoteDir + " 下没有 png（可用 GenerateMap 插件生成）"); }
            int thumbs = pngs.Count(e => e.Name.EndsWith(".thumb.png", StringComparison.OrdinalIgnoreCase));
            return (true, $"{pngs.Count - thumbs} 张原图 / {thumbs} 张缩略图（缩略图命中则地图秒开）");
        });

        // 7) 远端服务端状态（进程 + 监听端口）
        if (UseRemote)
        {
            Run("远端服务端(读快照)", () =>
            {
                // 数据来自上面那次合并快照，不再多发一次 ssh
                string r = "procs=" + SnapshotProcs + " listen=" + SnapshotListen;
                return (SnapshotProcs > 0, r);
            });
        }
        else { SkipLine("远端服务端", "本机模式"); }

        // 8) 管理器扩展插件
        Run("管理器扩展", () =>
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "plugins");
            int n = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.dll").Length : 0;
            return (n > 0, $"{n} 个扩展 dll");
        });

        // 文件传输往返（用管理器真实的 FsUpload/FsDownload/FsDelete，真实数据比对）
        Run("文件传输往返", () =>
        {
            if (!UseRemote) { return (true, "本机模式：直接文件操作，无需传输"); }
            string up = Path.Combine(Path.GetTempPath(), "utsl_sc_up.bin");
            var payload = new byte[64 * 1024];
            new Random(20251011).NextBytes(payload);
            File.WriteAllBytes(up, payload);
            string remote = Path.Combine(RemoteDir, "selfcheck_probe.bin");
            try
            {
                if (!FsUpload(up, remote)) { return (false, "上传失败"); }
                long rsize = FsList(RemoteDir).Where(e => e.Name == "selfcheck_probe.bin").Select(e => e.Size).FirstOrDefault();
                string back = Path.Combine(Path.GetTempPath(), "utsl_sc_down.bin");
                if (File.Exists(back)) { File.Delete(back); }
                bool down = FsDownload(remote, back);
                bool same = down && File.Exists(back) && File.ReadAllBytes(back).SequenceEqual(payload);
                FsDelete(remote);
                bool gone = !FsList(RemoteDir).Any(e => e.Name == "selfcheck_probe.bin");
                return (same && gone, $"上传 {payload.Length} B（远端 {rsize} B）→ 下载逐字节比对 {(same ? "一致" : "不一致")} → 删除 {(gone ? "已清除" : "仍残留")}");
            }
            finally
            {
                try { File.Delete(up); } catch { }
                try { File.Delete(Path.Combine(Path.GetTempPath(), "utsl_sc_down.bin")); } catch { }   // ★ 审查发现：下载临时文件原先没清理
            }
        });

        // 地图原图全量下载（验证「载入原图」走的是完整图片路径）
        Run("地图原图(全量)", () =>
        {
            string dir = Path.Combine(FsRoot, "GenerateMap", "Images");
            var png = FsList(dir).Where(e => !e.IsDir && e.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && !e.Name.EndsWith(".thumb.png", StringComparison.OrdinalIgnoreCase))
                                .OrderByDescending(e => e.Mtime).FirstOrDefault();
            if (png is null) { return (false, "没有原图可测"); }
            string local = Path.Combine(MapCacheDir, "selfcheck_full.png");
            Directory.CreateDirectory(MapCacheDir);
            if (File.Exists(local)) { File.Delete(local); }
            bool ok = FsDownload(Path.Combine(dir, png.Name), local);
            long sz = ok && File.Exists(local) ? new FileInfo(local).Length : 0;
            if (ok && sz == png.Size) { try { File.Delete(local); } catch { } }
            return (ok && sz == png.Size, $"{png.Name}：{png.Size / 1048576.0:F2} MB，下载 {(ok ? "成功" : "失败")}，大小{(sz == png.Size ? "一致" : "不一致")}");
        });

        // 8.4) DPAPI 往返验证（确认加密真的可用，而不是静默退回明文）
        Run("DPAPI 加密", () =>
        {
            const string probe = "utsl-probe-测试-123";
            string enc2 = DpapiProtect(probe);
            if (enc2.Length == 0) { return (false, "DPAPI 不可用（将退回明文存储，建议检查系统加密服务）"); }
            string dec2 = DpapiUnprotect(enc2);
            return (dec2 == probe, dec2 == probe ? "加密/解密往返一致 ✓" : "解出来不一致 ✗");
        });

        // 8.5) 凭据存储安全状态
        try
        {
            string connTxt = File.Exists(ConnFile) ? File.ReadAllText(ConnFile) : "";
            bool enc = connTxt.Contains("tokenEnc");
            bool plain = connTxt.Contains("\"token\"");
            pass++;
            sb.AppendLine($"  [OK]   {"凭据存储",-22} {"-",7}      " + (enc ? "token 已用 DPAPI 加密 ✓" + (plain ? "（但仍有明文字段，请点一次「保存并应用」清掉）" : "") : "仍是明文 token（点一次「保存并应用」即可加密）"));
        }
        catch { }

        // 9) 本机备份
        try
        {
            string bk = Path.Combine(LocalDir, "backup");
            int n = Directory.Exists(bk) ? Directory.GetFiles(bk, "backup_*.zip").Length : 0;
            string newest = n > 0 ? Path.GetFileName(Directory.GetFiles(bk, "backup_*.zip").OrderByDescending(x => x).First()) : "";
            pass++;
            sb.AppendLine($"  [OK]   {"本机备份",-22} {"-",7}      " + (n == 0 ? "暂无备份" : $"{n} 份，最新 {newest}"));
        }
        catch { }

        sb.AppendLine(new string('-', 72));
        sb.AppendLine("命令通道原始探针：");
        try { sb.Append(ProbeChannel()); } catch (Exception ex) { sb.AppendLine("  探针失败: " + ex.Message); }
        sb.AppendLine(new string('-', 72));

        total.Stop();
        sb.AppendLine($"  通过 {pass} / 失败 {fail} / 跳过 {skip}    总耗时 {total.ElapsedMilliseconds} ms");
        sb.AppendLine("  判读：『合并快照』是唯一的一次 ssh（3~8 秒），它之后的读取都应是毫秒级；");
        sb.AppendLine("        若某个页面又是几秒，说明它没走缓存（把它需要的路径并进 RefreshSnapshot 即可）。");
        return sb.ToString();
    }

    /// <summary>刷新连接健康三灯（只在状态变化时改画刷，避免每 2 秒产生垃圾）。</summary>
    private void UpdateHealthDots()
    {
        try
        {
            bool tunnel = TunnelAlive;
            bool shell = RemoteShell.Status.StartsWith("就绪", StringComparison.Ordinal);
            bool rest = RestAlive;
            if (dotsInit && tunnel == lastDotTunnel && shell == lastDotShell && rest == lastDotRest) { return; }
            dotsInit = true; lastDotTunnel = tunnel; lastDotShell = shell; lastDotRest = rest;
            SetDot(DotTunnel, !HasSsh ? "#6B6B70" : tunnel ? "#30D158" : "#FF9F0A");
            SetDot(DotShell, !HasSsh ? "#6B6B70" : shell ? "#30D158" : "#FF9F0A");
            SetDot(DotRest, rest ? "#30D158" : "#FF9F0A");
        }
        catch { }
    }

    private static void SetDot(Avalonia.Controls.Border? dot, string hex)
    {
        if (dot is null) { return; }
        dot.Background = new SolidColorBrush(Color.Parse(hex));
    }

    private static void EnsureTunnel()
    {
        if (!HasSsh) { return; }
        try
        {
            var t = new System.Net.Sockets.TcpClient();
            bool alive = false;
            try { alive = t.ConnectAsync("127.0.0.1", ConnPort).Wait(800); } catch { alive = false; }
            t.Close();
            TunnelAlive = alive;
            if (alive) { return; }
            if (TunnelPid > 0)
            {
                try { System.Diagnostics.Process.GetProcessById(TunnelPid); return; } catch { TunnelPid = 0; }
            }
            var psi = new System.Diagnostics.ProcessStartInfo("ssh")
            {
                UseShellExecute = false, CreateNoWindow = true,
                Arguments = "-i \"" + SshKey + "\" -p " + SshPort + " -o BatchMode=yes -o ExitOnForwardFailure=yes -o ServerAliveInterval=30 -N -L " +
                            ConnPort + ":127.0.0.1:" + RemoteRestPort + " " + SshUser + "@" + SshHost
            };
            var proc = System.Diagnostics.Process.Start(psi);
            TunnelPid = proc?.Id ?? 0;
        }
        catch { }
    }

    /// <summary>把 PowerShell 脚本编码后经 ssh 执行（远端命令一律走 EncodedCommand，避免引号/转义问题）。</summary>
    /// <summary>
    /// 常驻远端命令通道：**一条** ssh 上跑 PowerShell 循环，stdin 收 base64 命令、按 ##B/##E 帧回包。
    /// 省掉"每次请求重新握手 + 远端重启 PowerShell"（实测单次 3~5 秒 → 通道内 ~0.1 秒）。
    /// 三重保险：① StandardInput 显式不带 BOM（否则远端取 id 会带上 BOM → 永远收不到回包，这就是上一版失败的原因）；
    /// ② 建连后自检，6 秒内必须收到 ##E 帧；③ 任何异常/超时立即标记不可用并永久回退一次性 ssh。
    /// 生命周期：管理器退出时 stdin 关闭 → 远端 ReadLine 返回 null → 循环退出 → 本机 ssh 退出（自清理，不留孤儿）。
    /// </summary>
    private static class RemoteShell
    {
        private static System.Diagnostics.Process? _proc;
        private static readonly object _gate = new();
        private static int _seq;
        private static bool _broken;
        private static int _strikes;      // 连续超时计数：2 次才判死
        private static bool _warm;        // 是否已成功跑过一次（冷启动那次给足时间且不计失败）
        // ★ 关键修复：ReadLineAsync().Wait(超时) 在超时后会留下"进行中的读"，
        //   下一次读直接抛 "stream is currently in use" → 通道被永久废弃 → 全部回退（实测 7~11 秒/次）。
        //   正确做法：后台线程阻塞式读入队列，调用方从队列带超时取，绝不放弃进行中的读。
        private static System.Collections.Concurrent.BlockingCollection<string>? _outQ;
        public static string Status = "未启用";
        private static bool _reported;
        public static Action<string>? Report;

        private static void Say()
        {
            if (_reported) { return; }
            _reported = true;
            try { Report?.Invoke("远端命令通道: " + Status); } catch { }
        }

        /// <summary>★ 关键修复：ReadLine() **没有超时**，远端一旦不返回就会永久阻塞
        ///   （实测导致自检进程卡死数分钟、ssh 堆积，"一直读取中"也是它）。这里改成带超时的异步读。</summary>
        private static string? ReadLineTimed(int ms)
        {
            try
            {
                var q = _outQ;
                if (q is null) { return null; }
                if (q.TryTake(out string? line, ms)) { return line; }
                if (q.IsCompleted) { return null; }     // 队列已结束 = 通道断了
                return null;                            // 超时（注意：这不影响后台的读，绝不会留下"进行中的读"）
            }
            catch { return null; }
        }

        private static bool Ensure()
        {
            if (_broken) { return false; }
            if (_proc is not null && !_proc.HasExited) { return true; }
            try { _proc?.Kill(true); } catch { }
            try { _proc?.Dispose(); } catch { }
            _proc = null;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("ssh")
                {
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8,
                    StandardInputEncoding = new System.Text.UTF8Encoding(false),   // ★ 关键：不能带 BOM
                };
                psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(SshKey);
                psi.ArgumentList.Add("-p"); psi.ArgumentList.Add(SshPort.ToString());
                psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("BatchMode=yes");
                psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("ServerAliveInterval=30");
                psi.ArgumentList.Add(SshUser + "@" + SshHost);
                string loop =
                    "$ProgressPreference='SilentlyContinue';[Console]::OutputEncoding=[Text.Encoding]::UTF8;" +
                    "while($true){$line=[Console]::In.ReadLine();if($null -eq $line){break};if($line -eq '__EXIT__'){break};" +
                    "$id=$line.Substring(0,8);$b64=$line.Substring(8);" +
                    "$code=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($b64));" +
                    "try{$out=(& ([ScriptBlock]::Create($code)) 2>&1 | Out-String)}catch{$out='ERR: '+$_.Exception.Message};" +
                    "[Console]::Out.WriteLine('##B'+$id);[Console]::Out.Write($out);[Console]::Out.WriteLine();[Console]::Out.WriteLine('##E'+$id);[Console]::Out.Flush()}";
                psi.ArgumentList.Add("powershell -NoProfile -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(loop)));
                _proc = System.Diagnostics.Process.Start(psi);
                if (_proc is null) { _broken = true; Status = "启动失败（已回退一次性 ssh）"; Say(); return false; }
                var proc = _proc;
                _outQ = new System.Collections.Concurrent.BlockingCollection<string>();
                var q = _outQ;
                _ = Task.Run(() =>
                {
                    try { string? l; while ((l = proc.StandardOutput.ReadLine()) is not null) { q.Add(l); } }
                    catch { }
                    finally { try { q.CompleteAdding(); } catch { } }
                });
                _ = Task.Run(() => { try { while (proc.StandardError.ReadLine() is not null) { } } catch { } });   // 排空 stderr，避免管道填满阻塞远端
                if (!SelfTest())
                {
                    _broken = true; Status = "自检失败（已回退一次性 ssh）"; Say();
                    try { _proc.Kill(true); } catch { }
                    _proc = null;
                    return false;
                }
                Status = "就绪（每次请求约 0.1 秒）"; Say();
                _warm = false;   // 新建的通道还没跑过真实命令，第一次要给足时间
                return true;
            }
            catch
            {
                _broken = true; Status = "异常（已回退一次性 ssh）"; Say();
                _proc = null; return false;
            }
        }

        private static bool SelfTest()
        {
            try
            {
                if (_proc is null) { return false; }
                const string id = "99999999";
                _proc.StandardInput.WriteLine(id + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes("'ok'")));
                _proc.StandardInput.Flush();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 6000)
                {
                    string? ln = ReadLineTimed(2000);      // ★ 每次读最多等 2 秒，绝不无限阻塞
                    if (ln is null) { return false; }
                    if (ln == "##E" + id) { return true; }
                }
                return false;
            }
            catch { return false; }
        }

        public static string? Run(string psScript, int timeoutMs)
        {
            lock (_gate)
            {
                if (_broken) { return null; }
                if (!Ensure() || _proc is null) { return null; }
                try
                {
                    string id = (++_seq % 100000000).ToString("D8");
                    _proc.StandardInput.WriteLine(id + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(psScript)));
                    _proc.StandardInput.Flush();
                    var sb = new System.Text.StringBuilder();
                    string end = "##E" + id;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    // ★ 冷启动第一次要等远端 PowerShell 起来（实测 7~12 秒），给 25 秒且失败不计 strike；
                    //   预热成功后每次只给 8 秒，超时才计数（否则一次慢请求就把通道废掉 → 全部回退，实测就是这个现象）。
                    int budget = _warm ? Math.Min(timeoutMs, 8000) : Math.Min(timeoutMs, 25000);
                    bool first = !_warm;
                    while (sw.ElapsedMilliseconds < budget)
                    {
                        string? ln = ReadLineTimed(2000);   // ★ 带超时，绝不无限阻塞
                        if (ln is null) { break; }
                        if (ln == end) { _strikes = 0; _warm = true; return sb.ToString(); }
                        if (ln.StartsWith("##B", StringComparison.Ordinal)) { continue; }
                        sb.AppendLine(ln);
                    }
                    // 冷启动那次超时不计数（远端 PowerShell 刚起来本来就慢）
                    if (!first) { _strikes++; }
                    if (_strikes < 2)
                    {
                        try { _proc.Kill(true); } catch { }
                        _proc = null;
                        return null;            // 本次先回退，下次仍会尝试通道
                    }
                    _broken = true; Status = "连续超时（已回退一次性 ssh）"; Say();
                    try { _proc.Kill(true); } catch { }
                    _proc = null;
                    return null;
                }
                catch
                {
                    _broken = true; Status = "异常（已回退一次性 ssh）"; Say();
                    try { _proc?.Kill(true); } catch { }
                    _proc = null; return null;
                }
            }
        }
    }

    // ★ 实测：单次 ssh 握手 ~1.5 秒；但并发堆到 40 个时单次会变成 14 秒（互相拖慢）。
    //   这里用信号量限制并发（最多 2 个），并且最多等 15 秒就放行（避免互相死等）。
    private static readonly System.Threading.SemaphoreSlim SshGate = new(2, 2);

    private static string SshRun(string psScript, int timeoutMs = 30000)
    {
        bool got = SshGate.Wait(15000);
        try
        {
            return SshRunInner(psScript, timeoutMs);
        }
        finally
        {
            if (got) { SshGate.Release(); }
        }
    }

    private static string SshRunInner(string psScript, int timeoutMs = 30000)
    {
        // ⚠ 常驻命令通道已停用（保留实现备查）：用独立脚本实测发现，远端 PowerShell 在 ssh 循环里读 stdin
        //   并不稳定 —— 第一次命令正常，之后 [Console]::In.ReadLine() 要么再也收不到、要么疯狂空转刷屏
        //   （实测 9 秒内收到 1 万多行垃圾），导致通道被判失败后永久回退，反而比一次性 ssh 更糟。
        //   结论：不再走这条通道，改用"减少调用次数 + 缓存 + 后台预热"来优化体感（见 WarmupRemote / FsMemo）。
        // string? fast = RemoteShell.Run(psScript, timeoutMs);
        // if (fast is not null) { return fast.Trim(); }
        string b64 = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(psScript));
        var psi = new System.Diagnostics.ProcessStartInfo("ssh")
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(SshKey);
        psi.ArgumentList.Add("-p"); psi.ArgumentList.Add(SshPort.ToString());
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("BatchMode=yes");
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("ConnectTimeout=10");
        psi.ArgumentList.Add(SshUser + "@" + SshHost);
        psi.ArgumentList.Add("powershell -NoProfile -EncodedCommand " + b64);
        using var p = System.Diagnostics.Process.Start(psi) ?? throw new Exception("无法启动 ssh");
        string o = p.StandardOutput.ReadToEnd();
        string err = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } }   // 自检修复：超时杀掉 ssh，避免僵留进程
        // 远端 PowerShell 会把进度/错误流以 CLIXML 形式吐出来，这里滤掉，日志面板才干净
        var keep = new List<string>();
        foreach (string ln in o.Split('\n'))
        {
            string s = ln.TrimEnd('\r');
            if (s.StartsWith("#< CLIXML") || s.StartsWith("<Objs") || s.StartsWith("<S ") || s.Contains("P S C u s t o m O b j e c t")) { continue; }
            keep.Add(s);
        }
        string clean = string.Join("\n", keep).Trim();
        if (clean.Length == 0) { clean = err.Trim(); }
        return clean;
    }

    /// <summary>远端启动脚本：先清旧进程，再用 WMI 创建「控制台启动器」（脱离 SSH 会话，断开不会被回收）。</summary>
    private string RemoteStartScript()
    {
        string cmd = RemoteDotnet + " " + RemoteLauncher + " " + RemoteDir + " " + RemoteDir + "\\UnifierTSL.exe -joinserver first -logmode txt";
        return "$ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
               "Get-Process UnifierTSL,UnifierTSL.ConsoleClient -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue; " +
               "Start-Sleep -Seconds 2; " +
               "$c='" + cmd + "'; " +
               "$r=Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{CommandLine=$c}; " +
               "'wmi rc=' + $r.ReturnValue + ' pid=' + $r.ProcessId";
    }

    private static string RemoteStopScript() =>
        "$ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
        "Get-Process UnifierTSL,UnifierTSL.ConsoleClient -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue; " +
        "Start-Sleep -Seconds 1; 'remaining=' + ((Get-Process UnifierTSL,UnifierTSL.ConsoleClient -ErrorAction SilentlyContinue) | Measure-Object).Count";

    private long lastRemoteLogMs;
    private long lastLogErrMs;
    private string lastLogErr = "";
    private string lastRemoteLine = "";

    /// <summary>远端日志：每 3 秒取最新日志尾部，按上一行做去重后只追加新行。</summary>
    private bool logFetching;

    private async void RefreshRemoteLog()
    {
        // ★ 自检修复：这里原来是同步 SshRun —— 每 1.5 秒阻塞 UI 线程一次，窗口卡到拖不动。
        //   改为后台线程执行 + 防重入（上一次没回来就不再发起）。
        long now = Environment.TickCount64;
        if (logFetching || now - lastRemoteLogMs < 12000) { return; }
        lastRemoteLogMs = now;
        logFetching = true;
        string outp;
        try { outp = await Task.Run(() => SshRun("$ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
              "$f=Get-ChildItem '" + RemoteDir + "\\logs\\*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1; " +
              "if ($f) { '###' + $f.Name; Get-Content $f.FullName -Tail 40 }")); }
        catch (Exception ex)
        {
            // 自检修复：读日志失败时别每 3 秒刷一条（SSH 配错会刷屏），同一错误最多每分钟提示一次
            long nowMs2 = Environment.TickCount64;
            if (nowMs2 - lastLogErrMs > 60000 || ex.Message != lastLogErr)
            {
                lastLogErrMs = nowMs2; lastLogErr = ex.Message;
                Term("远端日志读取失败（每分钟最多提示一次）: " + ex.Message, "#FF7B72");
            }
            logFetching = false;
            return;
        }
        logFetching = false;
        // ★ 不在控制台页时不往 UI 里塞日志行（隐藏的 300 行 TextBlock 每次追加都会让整个布局失效）
        if (!ConsoleView.IsVisible)
        {
            lastRemoteLine = "";
            return;
        }
        var lines = outp.Split('\n').Select(x => x.TrimEnd('\r')).ToList();
        if (lines.Count == 0) { return; }
        string header = lines[0].StartsWith("###") ? lines[0][3..] : "";
        if (header.Length > 0 && header != lastLogFile)
        {
            lastLogFile = "remote:" + header;
            lastRemoteLine = "";
            Term("——— 远端日志 " + header + " ———", "#6E7681");
        }
        int start = 1;
        if (lastRemoteLine.Length > 0)
        {
            int idx = lines.LastIndexOf(lastRemoteLine);
            start = idx >= 0 ? idx + 1 : 1;
        }
        for (int i = start; i < lines.Count; i++)
        {
            if (lines[i].Length == 0) { continue; }
            Term(lines[i], PickColor(lines[i]));
            lastRemoteLine = lines[i];
        }
    }

    private void OnClearTerm(object? sender, RoutedEventArgs e) => term.Clear();

    private void RefreshLog()
    {
        if (UseRemote) { RefreshRemoteLog(); return; }
        if (!ConsoleView.IsVisible) { logPos = 0; lastLogFile = ""; return; }   // ★ 隐藏时不做日志 UI 更新
        try
        {
            var f = new DirectoryInfo(Logs).GetFiles("*.log").OrderByDescending(x => x.LastWriteTime).FirstOrDefault();
            if (f is null) { return; }
            if (f.FullName != lastLogFile)
            {
                lastLogFile = f.FullName;
                logPos = 0;
                Term("——— 日志文件 " + f.Name + " ———", "#6E7681");
            }
            using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < logPos) { logPos = 0; }
            fs.Seek(logPos, SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            string? line;
            while ((line = sr.ReadLine()) is not null) { Term(line, PickColor(line)); }
            logPos = fs.Position;
        }
        catch { }
    }
    private async Task SendAsync()
    {
        string raw = (CmdBox.Text ?? "").Trim();
        if (raw.Length == 0) { return; }
        history.Add(raw);
        histIdx = -1;
        var row = WorldList.SelectedItem as WorldRow;
        string world = row is null || row.Name == "全部世界" ? "" : row.Name;
        string text = raw;
        var m = System.Text.RegularExpressions.Regex.Match(raw, @"^@(\S+)\s+([\s\S]*)$");
        if (m.Success) { world = m.Groups[1].Value; text = m.Groups[2].Value; }

        // 插件命令（P1-6）：以 ! 开头 → 交给插件宿主，不发给服务端
        if (text.StartsWith('!'))
        {
            TryPluginCommand(text, world.Length == 0 ? null : world);
            CmdBox.Text = "";
            return;
        }

        try
        {
            string url = Rest + "/world/command?token=" + Uri.EscapeDataString(Token) + "&world=" + Uri.EscapeDataString(world) + "&cmd=" + Uri.EscapeDataString(text);
            string res = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(res);
            Term("> " + (world.Length == 0 ? "" : "@" + world + " ") + text, "#79C0FF");
            if (doc.RootElement.TryGetProperty("response", out var arr))
            {
                foreach (var it in arr.EnumerateArray()) { Term("  " + (it.GetString() ?? ""), "#7EE787"); }
            }
            else { Term("  " + res, "#7EE787"); }
            ErrText.Text = "";
        }
        catch (Exception ex)
        {
            ErrText.Text = "指令发送失败: " + ex.Message;
        }
        CmdBox.Text = "";
    }

    // ===== 自绘标题栏（macOS 交通灯）=====
    private void ClampToScreen()
    {
        var scr = Screens.Primary;
        if (scr is null) { return; }
        double s = scr.Scaling;
        var wa = scr.WorkingArea;
        double maxW = wa.Width / s - 40;
        double maxH = wa.Height / s - 40;
        Width = Math.Min(Width, maxW);
        Height = Math.Min(Height, maxH);
        int w = (int)(Width * s), h = (int)(Height * s);
        Position = new Avalonia.PixelPoint(wa.X + (wa.Width - w) / 2, wa.Y + (wa.Height - h) / 2);
    }

    /// <summary>地图预览：显示 GenerateMap 出的最新一张图片。</summary>
    // ===== 实时监控（CPU / 内存 / 网络，自绘折线）=====
    private const int MaxSamples = 60;
    private readonly List<double> cpuHist = [], memHist = [], upHist = [], downHist = [];
    private readonly List<DateTime> timeHist = [];
    private DateTime lastSample = DateTime.Now;
    private TimeSpan lastCpuTime = TimeSpan.Zero;
    private long lastBytesSent, lastBytesRecv;
    private bool lastBytesSeen;                       // ★ 首帧只记基线，避免"开机至今总字节"假尖峰
    private long snapPushedAt;                        // 远端模式：已入图的那次快照时间（避免同一快照被重复压入）

    private static void Push(List<double> list, double v)
    {
        list.Add(v);
        if (list.Count > MaxSamples) { list.RemoveAt(0); }
    }

    private void SampleMetrics()
    {
        var now = DateTime.Now;
        double secs = Math.Max(0.05, (now - lastSample).TotalSeconds);

        // 口径标注（只在第一帧写一次）
        if (samplesSeen == 0)
        {
            try
            {
                if (CpuTitle is not null) { CpuTitle.Text = UseRemote ? "CPU（远端 · 单核 %）" : "CPU 使用量（本机 · 单核 %）"; }
                if (MemTitle is not null) { MemTitle.Text = UseRemote ? "内存（远端 · 私有）" : "内存使用量（本机 · 私有）"; }
                if (NetTitle is not null) { NetTitle.Text = UseRemote ? "网络（远端网卡）" : "网络流量（本机全部网卡）"; }
            }
            catch { }
        }

        double cpuVal = 0, memVal = 0, up = 0, down = 0;

        if (UseRemote)
        {
            // ★ 远端模式：数据来自「合并快照」（每 45 秒一次，不额外发 ssh）—— 监控的是被管的那台服务器。
            //   旧实现用 Process.GetProcessesByName 读的是【本机】进程、网络是本机全部网卡合计 → 测错对象。
            if (!SnapHasRate)
            {
                if (CpuNow is not null) { CpuNow.Text = "—"; }
                if (MemNow is not null) { MemNow.Text = "—"; }
                if (NetNow is not null) { NetNow.Text = "—"; }
                lastSample = now;
                return;                       // 还没有第二次快照 → 没有速率可算
            }
            if (SnapPrevAtMs == snapPushedAt) { return; }   // 同一份快照不重复入图（图表间隔=快照间隔）
            snapPushedAt = SnapPrevAtMs;

            cpuVal = SnapCpuPct;
            memVal = SnapMemBytes / 1073741824.0;
            up = SnapUpKbps;
            down = SnapDownKbps;
            Push(cpuHist, cpuVal);
            CpuNow.Text = $"{cpuVal:F2}%";
            if (SnapMemBytes > 0) { Push(memHist, memVal); MemNow.Text = $"{memVal:F2} GB"; } else { MemNow.Text = "—"; }
            Push(upHist, up); Push(downHist, down);
            NetNow.Text = up >= 1024 || down >= 1024
                ? $"↑ {up / 1024.0:F2}  ↓ {down / 1024.0:F2} MB/s"
                : $"↑ {up:F2}  ↓ {down:F2} KB/s";
        }
        else
        {
            var srv = System.Diagnostics.Process.GetProcessesByName("UnifierTSL").FirstOrDefault();
            if (srv is not null)
            {
                try
                {
                    srv.Refresh();
                    // ★ 首帧只记基线：否则第一帧 = 该进程开机至今的总 CPU 时间 ÷ 秒数 → 假尖峰（曾把 Y 轴顶到 400%）
                    if (samplesSeen > 0)
                    {
                        double cpu = (srv.TotalProcessorTime - lastCpuTime).TotalMilliseconds / (secs * 1000) * 100.0;
                        cpuVal = Math.Max(0, Math.Min(400, cpu));
                        Push(cpuHist, cpuVal);
                    }
                    lastCpuTime = srv.TotalProcessorTime;
                    CpuNow.Text = $"{cpuVal:F2}%";
                    memVal = srv.PrivateMemorySize64 / 1073741824.0;
                    Push(memHist, memVal);
                    MemNow.Text = $"{memVal:F2} GB";
                }
                catch { }
            }
            else
            {
                // 进程没了就显示 —（旧实现会一直留着上一次的读数，看着像还在跑）
                CpuNow.Text = "—"; MemNow.Text = "—";
                if (samplesSeen > 0) { Push(cpuHist, 0); }
            }

            long sent = 0, recv = 0;
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) { continue; }
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) { continue; }   // 回环不计（避免隧道流量重复计）
                    var st = ni.GetIPStatistics();
                    sent += st.BytesSent; recv += st.BytesReceived;
                }
            }
            catch { }
            // ★ 首帧只记基线：否则第一帧 = 网卡开机至今的总字节 → 假尖峰（曾出现 18823250 这种天文数字）
            if (lastBytesSeen && samplesSeen > 0)
            {
                up = Math.Max(0, (sent - lastBytesSent) / 1024.0 / secs);
                down = Math.Max(0, (recv - lastBytesRecv) / 1024.0 / secs);
                Push(upHist, up); Push(downHist, down);
                NetNow.Text = $"↑ {up:F2}  ↓ {down:F2} KB/s";
            }
            else { NetNow.Text = "—"; }
            lastBytesSent = sent; lastBytesRecv = recv; lastBytesSeen = true;
        }

        timeHist.Add(now);
        if (timeHist.Count > MaxSamples) { timeHist.RemoveAt(0); }
        samplesSeen++;
        lastSample = now;

        try { pluginHost?.RaiseMetricsSampled(new MetricsSample(cpuVal, memVal, up, down, now)); } catch { }
        if (pluginHost is not null && pluginHost.Metrics.Count > 0) { RefreshPluginMetrics(); }

        // ★ 图表：控制台可见 且 窗口在前台 才重绘。
        //   数值采样不受前台与否影响（所以仪表盘不会"冻结"），只有重绘被节流。
        double m1 = 0, m2 = 0, m3 = 0;
        if (ConsoleView.IsVisible && IsActive)
        {
            m1 = DrawChart(CpuChart, [cpuHist], ["#0A84FF"]);
            m2 = DrawChart(MemChart, [memHist], ["#30D158"]);
            m3 = DrawChart(NetChart, [upHist, downHist], ["#FF9F0A", "#BF5AF2"]);
        }
        UpdateAxes([CpuY0, CpuY1, CpuY2, CpuY3], [MemY0, MemY1, MemY2, MemY3], [NetY0, NetY1, NetY2, NetY3],
                    [CpuX0, CpuX1, CpuX2, CpuX3], [MemX0, MemX1, MemX2, MemX3], [NetX0, NetX1, NetX2, NetX3],
                    m1, m2, m3);
    }

    private void UpdateAxes(TextBlock[] cy, TextBlock[] my, TextBlock[] ny, TextBlock[] cx, TextBlock[] mx, TextBlock[] nx, double m1, double m2, double m3)
    {
        SetY(cy, m1, "cpu");
        SetY(my, m2, "mem");
        SetY(ny, m3, "net");
        SetX(cx); SetX(mx); SetX(nx);
    }

    /// <summary>Y 轴标签：带单位、短数字（避免出现 18823250 这种把轴挤爆的值）。</summary>
    private static string FmtAxis(double v, string kind)
    {
        if (kind == "cpu") { return v.ToString("F0") + "%"; }
        if (kind == "mem") { return v.ToString(v >= 10 ? "F1" : "F2") + " GB"; }
        // net：≥1 MB/s 用 MB/s，否则 KB/s
        if (v >= 1024) { double mb = v / 1024.0; return mb.ToString(mb >= 10 ? "F0" : "F1") + " MB/s"; }
        return v.ToString(v >= 100 ? "F0" : v >= 10 ? "F1" : "F2") + " KB/s";
    }

    private static void SetY(TextBlock[] labels, double max, string kind)
    {
        double[] ratios = [1.0, 0.75, 0.5, 0.25];
        for (int i = 0; i < labels.Length && i < ratios.Length; i++)
        {
            labels[i].Text = FmtAxis(max * ratios[i], kind);
        }
    }

    private void SetX(TextBlock[] labels)
    {
        if (timeHist.Count == 0) { return; }
        int n = timeHist.Count;
        int[] idx = [0, n / 3, n * 2 / 3, n - 1];
        for (int i = 0; i < labels.Length && i < idx.Length; i++)
        {
            int k = Math.Clamp(idx[i], 0, n - 1);
            labels[i].Text = timeHist[k].ToString("HH:mm:ss");
        }
    }

    private static double DrawChart(Canvas canvas, List<double>[] series, string[] colors)
    {
        canvas.Children.Clear();
        double w = canvas.Bounds.Width, h = canvas.Bounds.Height;
        if (w < 12 || h < 12) { return 0; }
        double max = 0.0001;
        foreach (var s in series) { foreach (var v in s) { if (v > max) { max = v; } } }
        // ★ 轴取"顺眼"刻度（1/2/5 × 10^n）：数字短、刻度整，避免天文数字把 Y 轴挤爆
        double scale = NiceMax(max);

        // 网格线（4 条水平参考线，模仿示例卡片）
        for (int gi = 0; gi <= 4; gi++)
        {
            double gy = 4 + gi * (h - 8) / 4.0;
            canvas.Children.Add(new Avalonia.Controls.Shapes.Line
            {
                StartPoint = new Avalonia.Point(0, gy),
                EndPoint = new Avalonia.Point(w, gy),
                Stroke = new SolidColorBrush(Color.Parse("#242429")),
                StrokeThickness = 1,
            });
        }

        for (int si = 0; si < series.Length; si++)
        {
            var s = series[si];
            if (s.Count == 0) { continue; }
            var pts = new List<Avalonia.Point>();
            for (int i = 0; i < s.Count; i++)
            {
                double x = 1 + i * (w - 2) / (MaxSamples - 1);
                double y = h - 2 - (s[i] / scale) * (h - 8);
                pts.Add(new Avalonia.Point(x, y));
            }
            var brush = new SolidColorBrush(Color.Parse(colors[si]));
            var areaPts = new List<Avalonia.Point>();
            foreach (var p in pts) { areaPts.Add(p); }
            areaPts.Add(new Avalonia.Point(pts[pts.Count - 1].X, h));
            areaPts.Add(new Avalonia.Point(pts[0].X, h));
            canvas.Children.Add(new Avalonia.Controls.Shapes.Polygon { Points = areaPts, Fill = new SolidColorBrush(Color.Parse(colors[si]), 0.18) });
            canvas.Children.Add(new Avalonia.Controls.Shapes.Polyline
            {
                Points = pts,
                Stroke = brush,
                StrokeThickness = 2,
                StrokeJoin = Avalonia.Media.PenLineJoin.Round,
            });
        }
        return scale;
    }

    /// <summary>把轴最大值抬到 1/2/5 × 10^n 的"顺眼"刻度。</summary>
    private static double NiceMax(double v)
    {
        if (v <= 0) { return 1; }
        double exp = Math.Floor(Math.Log10(v));
        double pow = Math.Pow(10, exp);
        double f = v / pow;
        double nf = f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10;
        return nf * pow;
    }

    // ===== 内存压缩（移植自 TSManager 的"空服压缩工作集"）=====
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr min, IntPtr max);

    private void OnTrimMemory(object? sender, RoutedEventArgs e)
    {
        try
        {
            var list = System.Diagnostics.Process.GetProcessesByName("UnifierTSL");
            if (list.Length == 0) { ERR("服务端没在运行"); return; }
            var parts = new List<string>();
            foreach (var pr in list)
            {
                long before = pr.WorkingSet64;
                SetProcessWorkingSetSize(pr.Handle, new IntPtr(-1), new IntPtr(-1));
                pr.Refresh();
                long after = pr.WorkingSet64;
                parts.Add($"{before / 1048576} → {after / 1048576} MB");
            }
            ERR("已把物理内存标记为可回收: " + string.Join("；", parts) + " —— 注意：私有提交(commit)不变 ✗ 世界尺寸上限不会因此提高 ✓ 真正用途是【重操作前腾物理内存】✓");
        }
        catch (Exception ex) { ERR("压缩失败: " + ex.Message); }
    }

    // ===== 备份（移植自 TSManager 的备份思路）=====
    private async void OnBackup(object? sender, RoutedEventArgs e)
    {
        if (UseRemote)
        {
            // ★ 功能补齐：远端模式下在远端打包（Compress-Archive）再 scp 下载到本机 backup/，形成离机副本
            try
            {
                string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
                string remoteBackupDir = Path.Combine(RemoteDir, "backup");
                string remoteZip = Path.Combine(remoteBackupDir, "backup_" + stamp + ".zip");
                string w = Q(Path.Combine(RemoteDir, "Worlds"));
                string c = Q(Path.Combine(RemoteDir, "config"));
                string script =
                    "$ProgressPreference='SilentlyContinue'; " +
                    "New-Item -ItemType Directory -Force -Path '" + Q(remoteBackupDir) + "' | Out-Null; " +
                    "$src=@(); if (Test-Path '" + w + "') { $src += '" + w + "' }; if (Test-Path '" + c + "') { $src += '" + c + "' }; " +
                    "if ($src.Count -eq 0) { 'NODATA' } else { Compress-Archive -Path $src -DestinationPath '" + Q(remoteZip) + "' -Force; 'OK ' + (Get-Item '" + Q(remoteZip) + "').Length }";
                ERR("远端备份中（在远端打包 Worlds + config），大存档请稍候…");
                string res = await Task.Run(() => SshRun(script, 900000));
                if (res.Contains("NODATA")) { ERR("远端没有 Worlds/config 目录，未备份"); return; }
                string localDir = Path.Combine(LocalDir, "backup");
                Directory.CreateDirectory(localDir);
                string localZip = Path.Combine(localDir, "backup_" + stamp + ".zip");
                ERR("远端打包完成，正在下载到本机 backup/ …");
                bool ok = await Task.Run(() => FsDownload(remoteZip, localZip));
                ERR(ok
                    ? ("远端备份完成并已下载: " + Path.GetFileName(localZip))
                    : ("远端备份已生成（" + remoteZip + "），但下载到本机失败（文件仍留在远端）"));
            }
            catch (Exception ex) { ERR("远端备份失败: " + ex.Message); }
            return;
        }
        try
        {
            string root = LocalDir;
            string outDir = Path.Combine(root, "backup");
            Directory.CreateDirectory(outDir);
            string zipPath = Path.Combine(outDir, "backup_" + DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + ".zip");
            int n = 0, skip = 0;
            using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
            {
                foreach (var f in Directory.GetFiles(Path.Combine(root, "Worlds"), "*.wld"))
                {
                    try { zip.CreateEntryFromFile(f, "Worlds/" + Path.GetFileName(f)); n++; } catch { skip++; }
                }
                string cfgDir = Path.Combine(root, "config");
                if (Directory.Exists(cfgDir))
                {
                    foreach (var f in Directory.GetFiles(cfgDir, "*.json", SearchOption.AllDirectories))
                    {
                        try
                        {
                            string rel = "config/" + Path.GetRelativePath(cfgDir, f).Replace('\\', '/');
                            zip.CreateEntryFromFile(f, rel); n++;
                        }
                        catch { skip++; }
                    }
                }
            }
            ERR($"备份完成: {Path.GetFileName(zipPath)}（{n} 个文件" + (skip > 0 ? $"，{skip} 个被占用跳过" : "") + "）");
        }
        catch (Exception ex) { ERR("备份失败: " + ex.Message); }
    }

    // ===== 隐藏 UTSL 子世界控制台窗口（用户反馈：不想看到弹出的三个悬浮窗）=====
    //   背景：UTSL 为每个子世界创建独立控制台 ✗ 管理器无法阻止它创建 ✗
    //   实测把子世界改成 Headless 会让服务端 20~40 秒后崩溃 ✗（CreateSurfaceConsole 内部机制）
    //   → 折中方案：枚举 UTSL 进程的窗口并隐藏 ✓ 效果上等于"不弹" ✓
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private static int HideUtslWindows(bool hide)
    {
        int n = 0;
        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName("UnifierTSL");
            var pids = procs.Select(p => (uint)p.Id).ToHashSet();
            if (pids.Count == 0) { return 0; }
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (pids.Contains(pid) && IsWindowVisible(h))
                {
                    ShowWindow(h, hide ? 0 : 9);   // 0=SW_HIDE  9=SW_RESTORE
                    n++;
                }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return n;
    }

    private void ApplyHideWorlds()
    {
        try
        {
            bool hide = ChkHideWorlds?.IsChecked == true;
            int n = HideUtslWindows(hide);
            ERR(n == 0
                ? "没找到 UTSL 的窗口（服务端没在运行？）"
                : (hide ? $"已隐藏 {n} 个 UTSL 窗口（服务端重启后会再弹一次，届时会自动再隐藏）"
                        : $"已恢复显示 {n} 个 UTSL 窗口"));
        }
        catch (Exception ex) { ERR("操作失败: " + ex.Message); }
    }

    // ===== 管理器插件系统 v2（宿主：加载/重载/命令/指标/事件/配置/权限/i18n）=====
    private PluginHost? pluginHost;
    private readonly Dictionary<LoadedPlugin, Control?> pluginViews = [];
    private bool raisingTerminal;
    private FileSystemWatcher? pluginWatcher;
    private DispatcherTimer? pluginReloadTimer;
    private bool? lastServerUp;

    private void InitPluginHost()
    {
        try
        {
            string pluginDir = Path.Combine(AppContext.BaseDirectory, "plugins");
            var opts = new PluginHostOptions
            {
                PluginDir = pluginDir,
                DataRoot = Path.Combine(AppContext.BaseDirectory, "plugin-data"),
                DeployRoot = () => DeployRoot,
                RestBase = () => Rest,
                Token = () => Token,
                Worlds = RowsSnapshot,
                Log = m => Term(m, PickColor(m)),
                Language = () => ManagerLanguage,
                // ★ v3：把「统一部署目录访问」注入插件宿主 —— 远端走 SSH / scp，本机直接读盘。
                //   插件因此不再受"只能读本机目录"的限制（旧 ReadDeployText 在远端模式下读错了目标）。
                FilesRoot = () => FsRoot,
                ListFiles = dir => FsList(dir).Select(e => new PluginFileInfo(e.Name, e.IsDir, e.Size, e.Mtime)).ToList(),
                FileExists = p => FsExists(p),
                ReadText = p => FsReadText(p),
                WriteText = (p, t) => FsWriteText(p, t),
                CreateDirectory = p => FsMkdir(p),
                Rename = (from, to) => FsRename(from, to),
                Delete = p => FsDelete(p),
                Upload = (local, remote) => FsUpload(local, remote),
                Download = (remote, local) => FsDownload(remote, local),
                InvalidateFileCache = FsInvalidate,
                RunCommand = (ps, ms) => SshRun(ps, ms),
                AfterLoad = RebuildPluginMenu,
                BeforeUnload = ClearPluginViews,
            };
            pluginHost = new PluginHost(opts);
            pluginHost.Load();
            SetupPluginWatcher(pluginDir);
        }
        catch (Exception ex) { ERR("管理器插件加载失败: " + ex.Message); }
    }

    /// <summary>处理启动参数：--open-plugin &lt;名字|dll名|清单名&gt; 直接打开某个插件视图（便于联调/验证）。</summary>
    private void ApplyStartupArgs()
    {
        string[] args = Program.StartupArgs;
        for (int i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], "--open-plugin", StringComparison.OrdinalIgnoreCase) || i + 1 >= args.Length) { continue; }
            string want = args[i + 1];
            var hit = pluginHost?.Plugins.FirstOrDefault(p =>
                p.Name.Contains(want, StringComparison.OrdinalIgnoreCase)
                || p.SourceFile.Contains(want, StringComparison.OrdinalIgnoreCase)
                || p.Descriptor.Key.Contains(want, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) { ShowPluginView(hit); }
            else { ERR("--open-plugin 找不到插件: " + want); }
            return;
        }
    }

    /// <summary>卸载前把插件界面从可视树摘掉，否则可回收 ALC 无法回收。</summary>
    private void ClearPluginViews()
    {
        pluginViews.Clear();
        ExtContent.Content = null;
        ExtPanel.Children.Clear();
        ExtHeader.IsVisible = false;
    }

    /// <summary>加载完成后重建右侧「扩展」菜单 + 刷新插件管理页/指标。</summary>
    private void RebuildPluginMenu()
    {
        if (pluginHost is null || ExtPanel is null) { return; }
        ExtPanel.Children.Clear();
        var list = pluginHost.Plugins;
        ExtHeader.IsVisible = list.Count > 0;
        foreach (var p in list)
        {
            var b = new Button
            {
                Content = p.Icon + "  " + p.Name, FontFamily = new Avalonia.Media.FontFamily("Segoe MDL2 Assets, Microsoft YaHei"), FontSize = 12.5,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                Padding = new Avalonia.Thickness(12, 9), CornerRadius = new Avalonia.CornerRadius(9),
            };
            b.Classes.Add("menu");
            string tip = p.Description + "   ·   v" + p.Version + "   ·   API v" + p.ApiVersion;
            if (p.Commands.Count > 0) { tip += "   ·   命令 " + string.Join(" ", p.Commands.Keys.Select(k => "!" + k)); }
            Avalonia.Controls.ToolTip.SetTip(b, tip);
            var plugin = p;
            b.Click += (_, _) => ShowPluginView(plugin);
            ExtPanel.Children.Add(b);
        }
        RefreshManagerPlugins();
        RefreshPluginMetrics();
    }

    /// <summary>P2-15：视图缓存 —— 同一插件只建一次界面，切走再切回不重建。</summary>
    private void ShowPluginView(LoadedPlugin p)
    {
        ExtTitle.Text = p.Icon + "  " + p.Name;
        string author = string.IsNullOrWhiteSpace(p.Instance.Author) ? "" : "   ·   作者 " + p.Instance.Author;
        ExtDesc.Text = p.Description + author + "   ·   v" + p.Version + "   ·   API v" + p.ApiVersion;
        try
        {
            if (!pluginViews.TryGetValue(p, out var view) || view is null)
            {
                view = p.Instance.CreateView(p.Context!);
                pluginViews[p] = view;
            }
            ExtContent.Content = view ?? new TextBlock
            {
                Text = p.Name + " 是纯动作/服务型插件（未提供界面）。", FontSize = 12.5,
                Foreground = GetBrush("#7C7C82"), TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            };
        }
        catch (Exception ex)
        {
            ExtContent.Content = new TextBlock
            {
                Text = "插件界面创建失败: " + ex.Message, FontSize = 12,
                Foreground = GetBrush("#FF7B72"), TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            };
        }
        ShowView("ext");
    }

    private IReadOnlyList<WorldInfo> RowsSnapshot()
        => rows.Skip(1).Select(r => new WorldInfo(r.Name, r.Size, r.Running)).ToList();

    // ===== 插件管理页（P0-2 + P0-3）=====
    private void OnShowMgrPlugins(object? sender, RoutedEventArgs e)
    {
        if (CmbLang is not null && CmbLang.SelectedItem is null)
        {
            foreach (var o in CmbLang.Items)
            {
                if (o is ComboBoxItem it && it.Content?.ToString() == ManagerLanguage) { CmbLang.SelectedItem = it; break; }
            }
        }
        RefreshManagerPlugins();
        ShowView("mgrplugins");
    }

    private void OnMgrPluginsReload(object? sender, RoutedEventArgs e)
    {
        try { pluginHost?.Reload(); ERR("插件已重新加载（见下方列表）"); }
        catch (Exception ex) { ERR("重载失败: " + ex.Message); }
    }

    private void OnMgrPluginsOpenDir(object? sender, RoutedEventArgs e)
    {
        try
        {
            string dir = pluginHost?.Options.PluginDir ?? Path.Combine(AppContext.BaseDirectory, "plugins");
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex) { ERR("打开目录失败: " + ex.Message); }
    }

    private void RefreshManagerPlugins()
    {
        if (MgrPluginList is null) { return; }
        MgrPluginList.Children.Clear();
        if (pluginHost is null) { MgrPluginList.Children.Add(Plain("插件宿主未初始化")); return; }
        var report = pluginHost.Report;
        MgrPluginPath.Text = pluginHost.Options.PluginDir;
        MgrPluginInfo.Text = $"已加载 {report.Loaded.Count} · 失败 {report.Failed.Count} · 扫描 {report.Scanned.Count} 个文件 · {report.At:HH:mm:ss} · 宿主 API v{ManagerApi.Version}";

        MgrPluginList.Children.Add(GroupLabel($"已加载（{report.Loaded.Count}）"));
        if (report.Loaded.Count == 0) { MgrPluginList.Children.Add(Plain("（当前没有已加载的插件）")); }
        foreach (var p in report.Loaded) { MgrPluginList.Children.Add(LoadedRow(p)); }

        if (report.Failed.Count > 0)
        {
            MgrPluginList.Children.Add(GroupLabel($"加载失败（{report.Failed.Count}）"));
            foreach (var f in report.Failed) { MgrPluginList.Children.Add(FailedRow(f)); }
        }

        var scanned = new HashSet<string>(report.Scanned, StringComparer.OrdinalIgnoreCase);
        var extras = new List<string>();
        try
        {
            foreach (var file in Directory.GetFiles(pluginHost.Options.PluginDir).Where(IsPluginFileName))
            {
                if (file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) { extras.Add(file); }
                else if (!scanned.Contains(file)) { extras.Add(file); }
            }
        }
        catch { }
        if (extras.Count > 0)
        {
            MgrPluginList.Children.Add(GroupLabel($"已停用 / 未加载（{extras.Count}）"));
            foreach (var file in extras.OrderBy(x => x)) { MgrPluginList.Children.Add(FileRow(file)); }
        }
    }

    private static bool IsPluginFileName(string path)
    {
        string s = path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? path[..^".disabled".Length] : path;
        return s.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".csx", StringComparison.OrdinalIgnoreCase);
    }

    private Control LoadedRow(LoadedPlugin p)
    {
        var left = new StackPanel { Spacing = 2 };
        string kind = string.IsNullOrWhiteSpace(p.Type) ? "" : " [" + p.Type + "]";
        left.Children.Add(new TextBlock
        {
            Text = p.Icon + " " + p.Name + "   v" + p.Version + "   API v" + p.ApiVersion + kind,
            FontSize = 13, Foreground = GetBrush("#F2F2F5"),
        });
        left.Children.Add(new TextBlock
        {
            Text = "作者 " + p.Instance.Author + "   ·   " + p.SourceFile + "   ·   权限 " + p.PermissionsText
                   + (p.Commands.Count > 0 ? "   ·   命令 " + string.Join(" ", p.Commands.Keys.Select(k => "!" + k)) : ""),
            FontSize = 10.5, Foreground = GetBrush("#7C7C82"), TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        left.Children.Add(new TextBlock
        {
            Text = p.Description, FontSize = 11, Foreground = GetBrush("#9A9AA0"),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });

        var btn = new Button
        {
            Content = "停用", FontSize = 11, Padding = new Avalonia.Thickness(10, 4),
            CornerRadius = new Avalonia.CornerRadius(7),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        btn.Click += (_, _) =>
        {
            if (PluginHost.SetEnabled(p.Descriptor.DllPath, false))
            {
                ERR("已停用 " + p.SourceFile + "（重命名为 .disabled），重载中…");
                pluginHost?.Reload();
            }
            else { ERR("停用失败：文件不存在"); }
        };
        return RowCard(left, btn);
    }

    private static Control FailedRow(FailedPlugin f)
    {
        var sp = new StackPanel { Spacing = 2 };
        sp.Children.Add(new TextBlock { Text = "✗ " + Path.GetFileName(f.DllPath), FontSize = 12.5, Foreground = GetBrush("#FF7B72") });
        sp.Children.Add(new TextBlock { Text = f.Reason, FontSize = 11, Foreground = GetBrush("#9A9AA0"), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        return Card(sp);
    }

    private Control FileRow(string file)
    {
        bool disabled = file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
        string real = disabled ? file[..^".disabled".Length] : file;
        var sp = new StackPanel { Spacing = 2 };
        sp.Children.Add(new TextBlock { Text = (disabled ? "⏸ " : "… ") + Path.GetFileName(real), FontSize = 12.5, Foreground = GetBrush("#B9B9C0") });
        sp.Children.Add(new TextBlock
        {
            Text = disabled ? "已停用（.disabled）—— 点「启用」恢复" : "存在但未加载（不是插件，或加载被跳过）",
            FontSize = 10.5, Foreground = GetBrush("#6E7681"),
        });
        var btn = new Button
        {
            Content = disabled ? "启用" : "停用", FontSize = 11, Padding = new Avalonia.Thickness(10, 4),
            CornerRadius = new Avalonia.CornerRadius(7),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        btn.Click += (_, _) =>
        {
            if (PluginHost.SetEnabled(real, disabled))
            {
                ERR((disabled ? "已启用 " : "已停用 ") + Path.GetFileName(real) + "，重载中…");
                pluginHost?.Reload();
            }
            else { ERR("操作失败：文件不存在"); }
        };
        return RowCard(sp, btn);
    }

    private static Control RowCard(Control left, Control right)
    {
        var grid = new Grid { ColumnDefinitions = new Avalonia.Controls.ColumnDefinitions("*,Auto") };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return Card(grid);
    }

    private static Control Card(Control inner) => new Border
    {
        Background = GetBrush("#1A1A1E"),
        BorderBrush = GetBrush("#2A2A30"),
        BorderThickness = new Avalonia.Thickness(1),
        CornerRadius = new Avalonia.CornerRadius(10),
        Padding = new Avalonia.Thickness(12, 9),
        Margin = new Avalonia.Thickness(0, 3, 0, 3),
        Child = inner,
    };

    private static TextBlock GroupLabel(string t) => new()
    {
        Text = t, FontSize = 11, FontWeight = Avalonia.Media.FontWeight.SemiBold,
        Foreground = GetBrush("#7C7C82"), Margin = new Avalonia.Thickness(2, 12, 0, 4),
    };

    private static TextBlock Plain(string t) => new()
    {
        Text = t, FontSize = 12, Foreground = GetBrush("#7C7C82"),
        TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(2, 3, 0, 3),
    };

    // ===== 插件指标（P1-10）=====
    private void RefreshPluginMetrics()
    {
        if (PluginMetricsHost is null) { return; }
        PluginMetricsHost.Children.Clear();
        IReadOnlyList<PluginMetricInfo> metrics = pluginHost is null ? Array.Empty<PluginMetricInfo>() : pluginHost.Metrics;
        IReadOnlyList<PluginStatusInfo> statuses = pluginHost is null ? Array.Empty<PluginStatusInfo>() : pluginHost.Statuses;
        if (PluginMetricsHeader is not null) { PluginMetricsHeader.IsVisible = metrics.Count > 0 || statuses.Count > 0; }
        foreach (var m in metrics)
        {
            try { m.Value = m.Provider(); } catch { m.Value = null; }
            var row = new Grid { ColumnDefinitions = new Avalonia.Controls.ColumnDefinitions("*,Auto") };
            var name = new TextBlock { Text = m.Name, FontSize = 11, Foreground = GetBrush("#9A9AA0") };
            var val = new TextBlock { Text = m.Display, FontSize = 11, Foreground = GetBrush("#66CCFF") };
            Grid.SetColumn(name, 0);
            Grid.SetColumn(val, 1);
            row.Children.Add(name);
            row.Children.Add(val);
            Avalonia.Controls.ToolTip.SetTip(row, "来自插件 " + m.PluginName);
            PluginMetricsHost.Children.Add(row);
        }

        // 文本状态项（P2-15：状态胶囊）
        foreach (var s in statuses)
        {
            try { s.Value = s.Provider(); } catch { s.Value = "—"; }
            var row = new Grid { ColumnDefinitions = new Avalonia.Controls.ColumnDefinitions("*,Auto") };
            var name = new TextBlock { Text = s.Name, FontSize = 11, Foreground = GetBrush("#9A9AA0") };
            var val = new TextBlock { Text = s.Value, FontSize = 11, Foreground = GetBrush("#7EE787"), TextWrapping = Avalonia.Media.TextWrapping.NoWrap };
            Grid.SetColumn(name, 0);
            Grid.SetColumn(val, 1);
            row.Children.Add(name);
            row.Children.Add(val);
            Avalonia.Controls.ToolTip.SetTip(row, "来自插件 " + s.PluginName);
            PluginMetricsHost.Children.Add(row);
        }
    }

    private void OnLangChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CmbLang?.SelectedItem is not ComboBoxItem item) { return; }
        string lang = item.Content?.ToString() ?? "zh-CN";
        if (lang == ManagerLanguage) { return; }
        ManagerLanguage = lang;
        SaveConn();
        ERR("插件语言已切换为 " + lang + "（重新打开插件页生效）");
        RefreshManagerPlugins();
    }

    // ===== 插件命令（P1-6）：输入 "!名字 参数" 触发 =====
    private bool TryPluginCommand(string raw, string? world)
    {
        if (pluginHost is null) { return false; }
        string body = raw.TrimStart('!').Trim();
        if (body.Length == 0) { return false; }
        int sp = body.IndexOf(' ');
        string name = sp < 0 ? body : body[..sp];
        string rest = sp < 0 ? "" : body[(sp + 1)..].Trim();

        if (string.Equals(name, "help", StringComparison.OrdinalIgnoreCase))
        {
            Term("插件命令（输入 !名字 参数）：", "#79C0FF");
            var cmds = pluginHost.Commands;
            if (cmds.Count == 0) { Term("  （没有插件注册命令）", "#7C7C82"); }
            foreach (var c in cmds) { Term("  !" + c.Name + "  —— " + c.Description + "（" + c.Plugin + "）", "#7EE787"); }
            return true;
        }

        var args = rest.Length == 0 ? Array.Empty<string>() : rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (pluginHost.TryExecuteCommand(name, args, rest, world, out _))
        {
            Term("> " + raw, "#79C0FF");
            return true;
        }
        Term("没有插件命令 !" + name + "（输入 !help 查看全部）", "#E3B341");
        return true;
    }

    // ===== 插件目录监视（可选热重载；默认关闭，避免边编译边重载）=====
    private void SetupPluginWatcher(string dir)
    {
        try
        {
            pluginWatcher = new FileSystemWatcher(dir)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                Filter = "*.*",
                EnableRaisingEvents = false,
            };
            FileSystemWatcher w = pluginWatcher;
            void OnChange(object? s, FileSystemEventArgs e)
            {
                string n = e.Name ?? "";
                if (!n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !n.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase)) { return; }
                Dispatcher.UIThread.Post(SchedulePluginReload, DispatcherPriority.Background);
            }
            w.Created += OnChange;
            w.Changed += OnChange;
            w.Deleted += OnChange;
            w.Renamed += (s, e) => OnChange(s, new FileSystemEventArgs(WatcherChangeTypes.Renamed, dir, e.Name));

            if (ChkPluginWatch is not null)
            {
                ChkPluginWatch.IsCheckedChanged += (_, _) =>
                {
                    if (pluginWatcher is not null) { pluginWatcher.EnableRaisingEvents = ChkPluginWatch.IsChecked == true; }
                };
            }
        }
        catch (Exception ex) { ERR("插件目录监视未启用: " + ex.Message); }
    }

    private void SchedulePluginReload()
    {
        if (ChkPluginWatch?.IsChecked != true) { return; }
        if (pluginReloadTimer is null) { pluginReloadTimer = NewReloadTimer(); }
        pluginReloadTimer.Stop();
        pluginReloadTimer.Start();
    }

    private DispatcherTimer NewReloadTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            ERR("插件目录已变化，正在重载…");
            try { pluginHost?.Reload(); }
            catch (Exception ex) { ERR("自动重载失败: " + ex.Message); }
        };
        return t;
    }

    private void NotifyServerState(bool up)
    {
        if (lastServerUp == up) { return; }
        lastServerUp = up;
        try { pluginHost?.RaiseServerStateChanged(up); } catch { }
    }

    // ===== 维护（整链重置/备份/恢复 三合一）=====
    private void OnShowMaint(object? sender, RoutedEventArgs e) => ShowView("maint");

    // ===== 连接设置（远端管理）=====
    private void OnShowConn(object? sender, RoutedEventArgs e)
    {
        try
        {
            ConnHostPanel.Children.Clear();
            ConnHostPanel.Children.Add(new TextBlock { Text = "支持远端 UTSL：填写远端主机与 REST 端口即可（需在远端放行该端口）", FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#9A9AA0")) });
            TextBox F(string label, string val, double w)
            {
                var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Margin = new Avalonia.Thickness(0, 4, 0, 4) };
                sp.Children.Add(new TextBlock { Text = label, FontSize = 11.5, Width = 90, Foreground = new SolidColorBrush(Color.Parse("#9A9AA0")), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                var tb = new TextBox { Text = val, FontSize = 12, Width = w, CornerRadius = new Avalonia.CornerRadius(8) };
                sp.Children.Add(tb);
                ConnHostPanel.Children.Add(sp);
                return tb;
            }
            var tbHost = F("主机:", ConnHost, 220);
            var tbPort = F("REST 端口:", ConnPort.ToString(), 100);
            var tbTok = F("令牌:", Token, 220);
            ConnHostPanel.Children.Add(new TextBlock { Text = "⚠ 安全提示：REST 请勿直接暴露公网 ✗ 建议仅监听内网 / 用 SSH 隧道 / 强令牌", FontSize = 10.5, Foreground = new SolidColorBrush(Color.Parse("#E3B341")), TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 8, 0, 0) });

            ConnHostPanel.Children.Add(new TextBlock { Text = "本机模式（默认）", FontSize = 12, FontWeight = Avalonia.Media.FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#B9B9C0")), Margin = new Avalonia.Thickness(0, 12, 0, 0) });
            var tbLocal = F("本地 UTSL 目录:", LocalDir, 380);

            ConnHostPanel.Children.Add(new TextBlock { Text = "远端模式（走 SSH；填好下面几项即可远程启停 / 看日志 / 读配置，REST 端口填隧道本地端口）", FontSize = 12, FontWeight = Avalonia.Media.FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#B9B9C0")), Margin = new Avalonia.Thickness(0, 14, 0, 0), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            var tbSshHost = F("SSH 主机:", SshHost, 220);
            var tbSshPort = F("SSH 端口:", SshPort.ToString(), 100);
            var tbSshUser = F("SSH 用户:", SshUser, 160);
            var tbSshKey = F("SSH 密钥:", SshKey, 380);
            var tbSshDir = F("远端 UTSL 目录:", RemoteDir, 220);
            var tbRestPort = F("远端 REST 端口:", RemoteRestPort.ToString(), 100);
            ConnHostPanel.Children.Add(new TextBlock { Text = "提示：远端需要先放好 UTSL 运行目录，并安装「控制台启动器 C:\\utsl-launch」（否则远端启动会因没有真实控制台而崩）。详见 utsl-remote\\README.md", FontSize = 10.5, Foreground = new SolidColorBrush(Color.Parse("#9A9AA0")), TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 6, 0, 0) });
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Margin = new Avalonia.Thickness(0, 6, 0, 0) };
            var btnTest = new Button { Content = "测试连接", FontSize = 11.5, Padding = new Avalonia.Thickness(14, 5), CornerRadius = new Avalonia.CornerRadius(9) };
            var btnSave = new Button { Content = "保存并应用", FontSize = 11.5, Padding = new Avalonia.Thickness(14, 5), CornerRadius = new Avalonia.CornerRadius(9) };
            btnTest.Click += async (_, _) =>
            {
                try
                {
                    string url = "http://" + tbHost.Text + ":" + tbPort.Text + "/world/list?token=" + Uri.EscapeDataString(tbTok.Text ?? "");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    string res = await Http.GetStringAsync(url);
                    sw.Stop();
                    ConnInfo.Text = $"连接成功 ✓ 延迟 {sw.ElapsedMilliseconds} ms · {res.Length} 字节";
                }
                catch (Exception ex) { ConnInfo.Text = "连接失败 ✗ " + ex.Message; }
            };
            btnSave.Click += (_, _) =>
            {
                ConnHost = tbHost.Text ?? "127.0.0.1";
                ConnPort = int.TryParse(tbPort.Text, out int p) && p > 0 ? p : 7878;
                Token = tbTok.Text ?? "";
                LocalDir = (tbLocal.Text ?? "").Trim().Length > 0 ? (tbLocal.Text ?? "").Trim() : LocalDir;
                SshHost = (tbSshHost.Text ?? "").Trim();
                SshPort = int.TryParse(tbSshPort.Text, out int sp) && sp > 0 ? sp : 22;
                SshUser = (tbSshUser.Text ?? "").Trim();
                SshKey = (tbSshKey.Text ?? "").Trim();
                RemoteDir = (tbSshDir.Text ?? "").Trim();
                RemoteRestPort = int.TryParse(tbRestPort.Text, out int rp) && rp > 0 ? rp : 7878;
                SaveConn();
                if (HasSsh) { EnsureTunnel(); }
                ConnInfo.Text = $"已保存并应用: http://{ConnHost}:{ConnPort}" + (HasSsh ? $" · 远端模式（SSH {SshUser}@{SshHost}:{SshPort} → {RemoteDir}）" : " · 本机模式");
                _ = RefreshWorldsAsync();
                RefreshLog();
            };
            var btnTestSsh = new Button { Content = "测试 SSH", FontSize = 11.5, Padding = new Avalonia.Thickness(14, 5), CornerRadius = new Avalonia.CornerRadius(9) };
            btnTestSsh.Click += async (_, _) =>
            {
                SshHost = (tbSshHost.Text ?? "").Trim();
                SshPort = int.TryParse(tbSshPort.Text, out int sp2) && sp2 > 0 ? sp2 : 22;
                SshUser = (tbSshUser.Text ?? "").Trim();
                SshKey = (tbSshKey.Text ?? "").Trim();
                RemoteDir = (tbSshDir.Text ?? "").Trim();
                if (!HasSsh) { ConnInfo.Text = "SSH 信息不完整 ✗ 需要主机与密钥"; return; }
                try
                {
                    var sw2 = System.Diagnostics.Stopwatch.StartNew();
                    string r = await Task.Run(() => SshRun("[Console]::OutputEncoding=[Text.Encoding]::UTF8; (hostname) + ' | UTSL进程=' + ((Get-Process UnifierTSL,UnifierTSL.ConsoleClient -ErrorAction SilentlyContinue) | Measure-Object).Count + ' | 日志=' + ((Get-ChildItem '" + RemoteDir + "\\logs\\*.log' -ErrorAction SilentlyContinue) | Measure-Object).Count", 20000));
                    sw2.Stop();
                    ConnInfo.Text = "SSH 测试 ✓ " + sw2.ElapsedMilliseconds + " ms · " + r.Replace("\n", " ");
                }
                catch (Exception ex) { ConnInfo.Text = "SSH 测试失败 ✗ " + ex.Message; }
            };
            var btnTunnel = new Button { Content = "启动/修复隧道", FontSize = 11.5, Padding = new Avalonia.Thickness(14, 5), CornerRadius = new Avalonia.CornerRadius(9) };
            btnTunnel.Click += (_, _) =>
            {
                EnsureTunnel();
                ConnInfo.Text = "已尝试建立隧道：本地 " + ConnPort + " → 远端 127.0.0.1:" + RemoteRestPort;
            };
            var btnAuto = new Button { Content = "自动检测", FontSize = 11.5, Padding = new Avalonia.Thickness(14, 5), CornerRadius = new Avalonia.CornerRadius(9) };
            btnAuto.Click += async (_, _) =>
            {
                try
                {
                    int foundPort = 7878; string foundTok = Token;
                    // 从 TShock 配置读 REST 外部应用令牌（TSManager 也是这么找的 ✓）
                    string tsCfg = Path.Combine(FsRoot, "config", "TShockAPI", "config.json");
                    if (!await Task.Run(() => FsExists(tsCfg))) { tsCfg = Path.Combine(FsRoot, "tshock", "config.json"); }
                    if (await Task.Run(() => FsExists(tsCfg)) && JsonNode.Parse(await Task.Run(() => FsReadText(tsCfg))) is JsonObject ts)
                    {
                        // 实测确认的键名（TShock 6.x）：RestApiPort 在【顶层】；ApplicationRestTokens 的【键名】就是令牌
                        if (int.TryParse(ts["RestApiPort"]?.ToString(), out int p1) && p1 > 0) { foundPort = p1; }
                        var dict = ts["ApplicationRestTokens"] as JsonObject;
                        if (dict is not null)
                        {
                            foreach (var kv in dict) { foundTok = kv.Key; break; }
                        }
                        else
                        {
                            // 兼容旧版：可能在 Settings 下
                            if (int.TryParse(ts["Settings"]?["RestApiPort"]?.ToString(), out int p2) && p2 > 0) { foundPort = p2; }
                            var d2 = ts["Settings"]?["ApplicationRestTokens"] as JsonObject;
                            if (d2 is not null) { foreach (var kv in d2) { foundTok = kv.Key; break; } }
                        }
                    }
                    tbHost.Text = "127.0.0.1";
                    tbPort.Text = foundPort.ToString();
                    tbTok.Text = foundTok;
                    ConnInfo.Text = $"自动检测完成：端口 {foundPort}，令牌 {foundTok}（来自服务端配置 ✓ 请点测试连接确认）";
                }
                catch (Exception ex) { ConnInfo.Text = "自动检测失败: " + ex.Message; }
            };
            row.Children.Add(btnTest); row.Children.Add(btnSave); row.Children.Add(btnAuto);
            var row2 = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Margin = new Avalonia.Thickness(0, 6, 0, 0) };
            row2.Children.Add(btnTestSsh); row2.Children.Add(btnTunnel);
            ConnHostPanel.Children.Add(row2);
            ConnHostPanel.Children.Add(row);
            ConnInfo.Text = $"当前: http://{ConnHost}:{ConnPort} · 配置存于 {ConnFile}";
            ShowView("conn");
        }
        catch (Exception ex) { ERR("连接设置失败: " + ex.Message); }
    }

    // ===== 世界链可视化 =====
    private async void OnShowChain(object? sender, RoutedEventArgs e)
    {
        // ★ 无条件切视图（放 try 外）
        ShowView("chain");
        try
        {
            if (ChainInfo is not null) { ChainInfo.Text = "读取中…（上次结果保留）"; }
            ChainInfo.Text = "读取中…（上次结果保留）";
            // ★ 流畅性：不先 Clear，等数据到手再重建
            // 读 EdgeStitch 配置
            JsonObject? edge = null;
            string edgePath = Path.Combine(FsRoot, "config", "EdgeStitch", "EdgeStitch.json");
            if (await Task.Run(() => FsExists(edgePath))) { edge = ParseJsonObj(await Task.Run(() => FsReadText(edgePath))); }
            ChainHost.Children.Clear();   // 数据到手才重建
            var chain = edge?["世界链"]?.AsArray().Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList() ?? [];
            if (chain.Count == 0) { chain = [.. rows.Where(r => r.Name != "全部世界").Select(r => r.Name)]; }
            var le = edge?["陆地边缘"] as JsonObject;

            // 链路图（横向卡片 + 箭头）
            var line = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            for (int i = 0; i < chain.Count; i++)
            {
                var w = rows.FirstOrDefault(r => r.Name == chain[i]);
                var card = new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#1C1C21")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#2E2E35")),
                    BorderThickness = new Avalonia.Thickness(1),
                    CornerRadius = new Avalonia.CornerRadius(12),
                    Padding = new Avalonia.Thickness(18, 14),
                    MinWidth = 168,
                };
                var box = new StackPanel { Spacing = 5 };
                box.Children.Add(new TextBlock { Text = chain[i], FontSize = 15, FontWeight = Avalonia.Media.FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#F2F2F5")) });
                box.Children.Add(new TextBlock { Text = w is null ? "未加载" : w.Size, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#9A9AA0")) });
                var dot = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
                dot.Children.Add(new Border { Width = 7, Height = 7, CornerRadius = new Avalonia.CornerRadius(3.5), Background = new SolidColorBrush(Color.Parse(w?.Running == true ? "#32D74B" : "#6B6B70")), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                dot.Children.Add(new TextBlock { Text = w?.Running == true ? "运行中" : "未运行", FontSize = 10.5, Foreground = new SolidColorBrush(Color.Parse("#9A9AA0")) });
                box.Children.Add(dot);
                if (le?[chain[i]] is JsonObject eo)
                {
                    box.Children.Add(new TextBlock { Text = $"西 {eo["西"]?.ToString() ?? "—"} · 东 {eo["东"]?.ToString() ?? "—"}", FontSize = 10.5, Foreground = new SolidColorBrush(Color.Parse("#7C7C82")) });
                }
                card.Child = box;
                line.Children.Add(card);
                if (i < chain.Count - 1)
                {
                    var arrow = new StackPanel { Spacing = 2, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Avalonia.Thickness(6, 0) };
                    arrow.Children.Add(new TextBlock { Text = "⟶", FontSize = 22, Foreground = new SolidColorBrush(Color.Parse("#0A84FF")), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center });
                    var mid = w is null ? "" : (le?[chain[i]]?["东"]?.ToString() ?? "");
                    var mid2 = le?[chain[i + 1]]?["西"]?.ToString() ?? "";
                    arrow.Children.Add(new TextBlock { Text = $"{mid} / {mid2}", FontSize = 9.5, Foreground = new SolidColorBrush(Color.Parse("#6E7681")), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center });
                    line.Children.Add(arrow);
                }
            }
            ChainHost.Children.Add(new TextBlock { Text = "链路（箭头间为两侧边缘触发值：出口 / 入口）", FontSize = 10.5, Foreground = new SolidColorBrush(Color.Parse("#6E7681")) });
            ChainHost.Children.Add(line);

            // 参数条
            if (edge is not null)
            {
                ChainHost.Children.Add(new TextBlock { Text = "触发参数", FontSize = 10.5, Foreground = new SolidColorBrush(Color.Parse("#6E7681")), Margin = new Avalonia.Thickness(0, 6, 0, 0) });
                var p = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, Margin = new Avalonia.Thickness(0, 6, 0, 0) };
                void Chip(string k, string v)
                {
                    var b = new Border { Background = new SolidColorBrush(Color.Parse("#1C1C21")), BorderBrush = new SolidColorBrush(Color.Parse("#2E2E35")), BorderThickness = new Avalonia.Thickness(1), CornerRadius = new Avalonia.CornerRadius(9), Padding = new Avalonia.Thickness(12, 6) };
                    b.Child = new TextBlock { Text = k + " " + v, FontSize = 11.5, Foreground = new SolidColorBrush(Color.Parse("#C9C9CE")) };
                    p.Children.Add(b);
                }
                Chip("切片间距", edge["切片间距_格"]?.ToString() ?? "—");
                Chip("边缘带", edge["边缘带宽度_格"]?.ToString() ?? "—");
                Chip("触发余量", edge["边缘触发余量_格"]?.ToString() ?? "—");
                Chip("冷却", (edge["传送冷却_毫秒"]?.ToString() ?? "—") + " ms");
                Chip("成环", edge["首尾相连成环"]?.ToString() ?? "—");
                ChainHost.Children.Add(p);
            }
            else
            {
                ChainHost.Children.Add(new TextBlock { Text = "未找到 EdgeStitch 配置（未安装该插件）—— 上方按当前已加载世界顺序展示", FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#6E7681")), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            }
            ChainInfo.Text = $"{chain.Count} 个世界" + (edge is null ? "（EdgeStitch 未安装）" : "");
            ShowView("chain");
        }
        catch (Exception ex) { ERR("生成世界链失败: " + ex.Message); }
    }

    // ===== 配置可视化编辑（字段带标签 / 难度下拉 / 大小步进 / 总图格安全条）=====
    private string cfgLauncherPath = "", cfgEdgePath = "";
    private readonly List<JsonObject> worldData = [];
    private StackPanel? worldRowsHost;
    private JsonObject? cfgLauncher, cfgEdge;
    private readonly List<(TextBox name, TextBox world, TextBox seed, TextBox size, ComboBox diff)> launcherRows = [];
    private readonly List<(string w, TextBox west, TextBox east)> edgeRows = [];
    private TextBox? pendChain, pendDelta, pendMargin, pendCool, pendBand, pendLoop;
    private TextBlock? safeText; private TextBlock? safeTip; private ProgressBar? safeBar;
    private static string DeployRoot => LocalDir;
    private static readonly string[] DiffNames = ["普通", "专家", "大师", "旅途"];

    private TextBlock Section(string title) => new()
    {
        Text = title, FontSize = 12.5, FontWeight = Avalonia.Media.FontWeight.SemiBold,
        Foreground = new SolidColorBrush(Color.Parse("#E8E8EC")), Margin = new Avalonia.Thickness(0, 14, 0, 2),
    };

    private static TextBlock Lbl(string t, double w = 90) => new()
    {
        Text = t, FontSize = 11.5, Foreground = new SolidColorBrush(Color.Parse("#9A9AA0")),
        Width = w, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
    };

    private static TextBox Tb(string v, double w = 120) => new()
    {
        Text = v, FontSize = 12, Width = w, CornerRadius = new Avalonia.CornerRadius(8),
    };

    /// <summary>大小步进器：只能按 200 的倍数调（片宽/源宽都必须是 200 倍数）。</summary>
    private StackPanel SizeStepper(TextBox tb)
    {
        var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
        var minus = new Button { Content = "◀", FontSize = 10, Width = 26, Height = 28, Padding = new Avalonia.Thickness(0), CornerRadius = new Avalonia.CornerRadius(7) };
        var plus = new Button { Content = "▶", FontSize = 10, Width = 26, Height = 28, Padding = new Avalonia.Thickness(0), CornerRadius = new Avalonia.CornerRadius(7) };
        void Step(int d)
        {
            if (!int.TryParse(tb.Text, out int v)) { v = 4200; }
            v = Math.Clamp(v + d * 200, 200, 20000);
            v = v / 200 * 200;   // 强制 200 倍数
            tb.Text = v.ToString();
            UpdateSafety();
        }
        minus.Click += (_, _) => Step(-1);
        plus.Click += (_, _) => Step(1);
        sp.Children.Add(minus); sp.Children.Add(tb); sp.Children.Add(plus);
        return sp;
    }

    /// <summary>按 worldData 重建所有世界行（增删都走这里，避免控件与数据错位）。</summary>
    private void RebuildWorldRows()
    {
        if (worldRowsHost is null) { return; }
        worldRowsHost.Children.Clear();
        launcherRows.Clear();
        foreach (var o in worldData) { AddWorldRow(o); }
        UpdateSafety();
    }

    private void AddWorldRow(JsonObject o)
    {
        var outer = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 18, Margin = new Avalonia.Thickness(4, 5, 0, 5) };
        StackPanel Pair(string label, double lw, System.Action<StackPanel> addBox)
        {
            var p = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
            p.Children.Add(Lbl(label, lw));
            addBox(p);
            outer.Children.Add(p);
            return p;
        }
        TextBox t0 = null!, t1 = null!, t2 = null!, t3 = null!;
        ComboBox cb = null!;
        Pair("世界名称:", 62, p => { t0 = Tb(o["name"]?.ToString() ?? "", 110); p.Children.Add(t0); });
        Pair("世界文件:", 62, p => { t1 = Tb(o["worldName"]?.ToString() ?? "", 140); p.Children.Add(t1); });
        Pair("世界种子:", 62, p => { t2 = Tb(o["seed"]?.ToString() ?? "", 100); p.Children.Add(t2); });
        Pair("片宽:", 38, p =>
        {
            t3 = Tb(o["size"]?.ToString() ?? "4200", 78);
            t3.TextChanged += (_, _) => { o["size"] = t3.Text ?? "4200"; UpdateSafety(); };
            p.Children.Add(SizeStepper(t3));
        });
        Pair("难度:", 38, p =>
        {
            cb = new ComboBox { FontSize = 12, Width = 92 };
            foreach (var d in DiffNames) { cb.Items.Add(d); }
            cb.SelectedIndex = int.TryParse(o["difficulty"]?.ToString(), out int dv) ? Math.Clamp(dv, 0, 3) : 0;
            p.Children.Add(cb);
        });
        // 每行删除按钮（修复：原来只能删最后一个世界 ✗）
        var btnDelRow = new Button
        {
            Content = "✕", FontSize = 11, Width = 28, Height = 28, Padding = new Avalonia.Thickness(0),
            CornerRadius = new Avalonia.CornerRadius(7), Margin = new Avalonia.Thickness(6, 0, 0, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        btnDelRow.Click += (_, _) =>
        {
            if (worldData.Count <= 1) { ERR("至少要保留 1 个世界"); return; }
            if (worldData.Remove(o)) { RebuildWorldRows(); ERR($"已从列表中移除 {o["name"]}（点保存后写入配置）"); }
        };
        outer.Children.Add(btnDelRow);
        worldRowsHost!.Children.Add(outer);
        launcherRows.Add((t0, t1, t2, t3, cb));
    }

    /// <summary>按 UTSL 的真实几何估算总图格：源宽 = n×片宽 − 3×重叠×(n−1)，高度按宽度推导。</summary>
    private async void UpdateSafety()
    {
        if (safeText is null || safeBar is null) { return; }
        int n = Math.Max(1, worldData.Count);
        int width = 4200;
        if (worldData.Count > 0 && int.TryParse(worldData[0]["size"]?.ToString(), out int w0) && w0 > 0) { width = w0; }
        width = Math.Clamp(width, 200, 20000);   // ★ 与步进器取值范围一致
        int overlap = 100;
        try
        {
            string mw = Path.Combine(FsRoot, "config", "AutoResetPlus", "MultiWorld.json");
            // ★ 自检修复：这里原来是 2 次同步 SSH（FsExists+FsReadText）在 UI 线程上跑 → 点世界链就冻 2~4 秒
            if (await Task.Run(() => FsExists(mw)))
            {
                JsonObject? mo = null;
                try { mo = JsonNode.Parse(await Task.Run(() => FsReadText(mw))) as JsonObject; } catch { }
                if (mo is not null && int.TryParse(mo["重叠量"]?.ToString(), out int ov) && ov > 0) { overlap = ov; }
            }
        }
        catch { }
        // ★ 修 bug#7：原式在 int32 下溢出（n=3,width=999999999 → 负值）；改用 long 并钳制
        long srcW64 = (long)n * width - 3L * overlap * (n - 1);
        if (srcW64 <= 0) { srcW64 = (long)n * width; }
        int srcW = (int)Math.Clamp(srcW64, 0, 10_000_000);
        int h = srcW <= 13000 ? 2400 : srcW <= 17000 ? 1800 : 1200;
        double totalM = srcW * (double)h / 10000.0;
        const double limitM = 2900;
        double pct = Math.Min(100, totalM / limitM * 100);
        safeBar.Value = pct;
        string color = pct >= 99 ? "#FF7B72" : pct >= 90 ? "#E3B341" : "#7EE787";
        safeBar.Foreground = new SolidColorBrush(Color.Parse(color));
        string hRule = srcW <= 13000 ? "<=13000" : srcW <= 17000 ? "<=17000" : ">17000";
        safeText.Text = $"{n} 个世界：源地图宽 = {n}×{width} − 3×{overlap}×{n - 1} = {srcW}"
                      + $"　→　高度 {h}（按宽度推导：{srcW} {hRule}）"
                      + $"　→　总图格 {totalM:F0} 万 / 上限 {limitM:F0} 万（{pct:F0}%）"
                      + (srcW % 200 != 0 ? "  ⚠ 源宽不是 200 的倍数！" : "");
        safeTip.Text = "⚠ 注意：世界数增加时总图格【可能反而变小】—— 因为源地图变长后高度会被 UTSL 自动压低（2400→1800→1200），"
                     + "这是为了适应内存上限做的取舍。真正决定能否跑起来的是【总图格数】，不是世界个数。";
        safeText.Foreground = new SolidColorBrush(Color.Parse(srcW % 200 != 0 ? "#FF7B72" : color));
    }

    private async void OnShowConfig(object? sender, RoutedEventArgs e)
    {
        // ★ 修 bug（"配置编辑点不进去"）：光标切换视图放在 try 之外、函数第一行 ——
        //   数据读取再失败也一定能进页面（页面里会显示错误），绝不会"点了没反应"。
        ShowView("config");
        try
        {
            if (ConfigInfo is not null) { ConfigInfo.Text = "读取中…（上次结果保留）"; }
            cfgLauncherPath = Path.Combine(FsRoot, "config", "config.json");
            cfgEdgePath = Path.Combine(FsRoot, "config", "EdgeStitch", "EdgeStitch.json");
            launcherRows.Clear(); edgeRows.Clear();

            ConfigHost.Children.Add(Section("启动器 config.json · 世界列表" + (UseRemote ? "（远端 " + FsRoot + "）" : "（本机 " + FsRoot + "）")));
            ConfigHost.Children.Add(new TextBlock { Text = "⚠ 这些改动【在下次整链重置时生效】（重置会重新生成源地图并按此列表切分）", FontSize = 10.5, Foreground = new SolidColorBrush(Color.Parse("#E3B341")) });
            cfgLauncher = ParseJsonObj(await Task.Run(() => FsReadText(cfgLauncherPath)));   // ★ 解析失败不再抛异常（视图已切过去）
            ConfigHost.Children.Clear();   // 数据到手才重建
            worldData.Clear();
            if (cfgLauncher?["launcher"]?["autoStartServers"]?.AsArray() is JsonArray arr0)
            {
                foreach (var it in arr0) { if (it is JsonObject o) { worldData.Add(o); } }
            }
            worldRowsHost = new StackPanel { Spacing = 0 };
            ConfigHost.Children.Add(worldRowsHost);
            RebuildWorldRows();
            var btns = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Margin = new Avalonia.Thickness(0, 8, 0, 0) };
            var btnAdd = new Button { Content = "+ 添加世界", FontSize = 11.5, Padding = new Avalonia.Thickness(12, 4), CornerRadius = new Avalonia.CornerRadius(9) };
            var btnDel = new Button { Content = "− 删除最后一个", FontSize = 11.5, Padding = new Avalonia.Thickness(12, 4), CornerRadius = new Avalonia.CornerRadius(9) };
            btnAdd.Click += (_, _) =>
            {
                int n = worldData.Count + 1;
                worldData.Add(new JsonObject { ["name"] = "World" + n, ["worldName"] = "World" + n + ".wld", ["seed"] = "", ["difficulty"] = "0", ["size"] = "4200", ["evil"] = "0" });
                RebuildWorldRows();
            };
            btnDel.Click += (_, _) =>
            {
                if (worldData.Count <= 1) { ERR("至少要保留 1 个世界"); return; }
                worldData.RemoveAt(worldData.Count - 1);
                RebuildWorldRows();
            };
            btns.Children.Add(btnAdd); btns.Children.Add(btnDel);
            ConfigHost.Children.Add(btns);

            // 总图格安全条
            ConfigHost.Children.Add(Section("总图格安全估算（自动计算）"));
            safeText = new TextBlock { Text = "—", FontSize = 11.5, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            safeBar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 10, CornerRadius = new Avalonia.CornerRadius(5), Margin = new Avalonia.Thickness(0, 6, 0, 0) };
            ConfigHost.Children.Add(safeText);
            ConfigHost.Children.Add(safeBar);
            safeTip = new TextBlock { Text = "", FontSize = 10.5, Foreground = new SolidColorBrush(Color.Parse("#E3B341")), TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 6, 0, 0) };
            ConfigHost.Children.Add(safeTip);
            ConfigHost.Children.Add(new TextBlock { Text = "安全上限来自实测：12000×2400(2880万) 可用，12800×2400(3072万) OOM；建议留 10% 余量", FontSize = 10.5, Foreground = new SolidColorBrush(Color.Parse("#6E7681")), Margin = new Avalonia.Thickness(0, 4, 0, 0) });

            // EdgeStitch（缺失时优雅提示，不报错）
            ConfigHost.Children.Add(Section("EdgeStitch · 世界链与边缘触发（可选）"));
            if (!await Task.Run(() => FsExists(cfgEdgePath)))
            {
                ConfigHost.Children.Add(new TextBlock { Text = "未找到 EdgeStitch 配置（未安装该插件）—— 此区跳过，本管理器仍可正常管理多世界", FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#6E7681")), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
                cfgEdge = null;
            }
            else
            {
                cfgEdge = ParseJsonObj(await Task.Run(() => FsReadText(cfgEdgePath)));
                var row1 = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
                row1.Children.Add(Lbl("世界链(逗号分隔):"));
                var chain = string.Join(",", cfgEdge?["世界链"]?.AsArray().Select(x => x?.ToString() ?? "") ?? []);
                pendChain = Tb(chain, 300); row1.Children.Add(pendChain);
                ConfigHost.Children.Add(row1);
                void Num(string label, string key, out TextBox tb)
                {
                    var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Margin = new Avalonia.Thickness(0, 4, 0, 0) };
                    sp.Children.Add(Lbl(label, 130));
                    tb = Tb(cfgEdge?[key]?.ToString() ?? "0", 90);
                    sp.Children.Add(tb);
                    ConfigHost.Children.Add(sp);
                }
                Num("切片间距_格:", "切片间距_格", out var tD); pendDelta = tD;
                Num("边缘触发余量_格:", "边缘触发余量_格", out var tM); pendMargin = tM;
                Num("传送冷却_毫秒:", "传送冷却_毫秒", out var tC); pendCool = tC;
                Num("边缘带宽度_格:", "边缘带宽度_格", out var tB); pendBand = tB;
                Num("首尾相连成环(true/false):", "首尾相连成环", out var tL); pendLoop = tL;
                pendDelta!.TextChanged += (_, _) => UpdateSafety();
                if (cfgEdge?["陆地边缘"] is JsonObject le)
                {
                    ConfigHost.Children.Add(new TextBlock { Text = "陆地边缘（西 / 东，留空=无边缘；41 与 宽-42 是常用值）", FontSize = 10.5, Foreground = new SolidColorBrush(Color.Parse("#6E7681")), Margin = new Avalonia.Thickness(0, 10, 0, 0) });
                    foreach (var kv in le)
                    {
                        var v = kv.Value as JsonObject;
                        var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Margin = new Avalonia.Thickness(0, 3, 0, 3) };
                        sp.Children.Add(Lbl(kv.Key, 96));
                        sp.Children.Add(Lbl("西"));
                        var w = Tb(v?["西"]?.ToString() ?? "", 92); sp.Children.Add(w);
                        sp.Children.Add(Lbl("东"));
                        var ee = Tb(v?["东"]?.ToString() ?? "", 92); sp.Children.Add(ee);
                        ConfigHost.Children.Add(sp);
                        edgeRows.Add((kv.Key, w, ee));
                    }
                }
            }
            ConfigInfo.Text = "改完点右上「保存」；旧配置会自动备份";
            UpdateSafety();
        }
        catch (Exception ex) { ERR("读取配置失败（页面仍可进入，可看下方提示）: " + ex.Message); }
    }

    private async void OnSaveConfig(object? sender, RoutedEventArgs e)
    {
        try
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            File.Copy(cfgLauncherPath, cfgLauncherPath + "." + stamp + ".bak", true);
            if (FsExists(cfgEdgePath)) { await Task.Run(() => FsWriteText(cfgEdgePath + "." + stamp + ".bak", FsReadText(cfgEdgePath))); }

            if (cfgLauncher is not null)
            {
                var arr = new JsonArray();
                foreach (var r in launcherRows)
                {
                    int size = (int.TryParse(r.size.Text, out int sv) ? Math.Clamp(sv, 200, 20000) : 4200) / 200 * 200;   // ★ 修 bug#7
                    r.size.Text = size.ToString();
                    arr.Add(new JsonObject
                    {
                        ["name"] = r.name.Text ?? "",
                        ["worldName"] = r.world.Text ?? "",
                        ["seed"] = r.seed.Text ?? "",
                        ["difficulty"] = r.diff.SelectedIndex.ToString(),
                        ["size"] = size.ToString(),
                        ["evil"] = "0",
                    });
                }
                cfgLauncher["launcher"] ??= new JsonObject();
                (cfgLauncher["launcher"] as JsonObject)!["autoStartServers"] = arr;
                await Task.Run(() => FsWriteText(cfgLauncherPath, cfgLauncher.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
            }

            if (cfgEdge is not null)
            {
                var names = (pendChain?.Text ?? "").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
                cfgEdge["世界链"] = new JsonArray(names.Select(n => (JsonNode)JsonValue.Create(n.Trim())!).ToArray());
                if (int.TryParse(pendDelta?.Text, out int d)) { cfgEdge["切片间距_格"] = d; }
                if (int.TryParse(pendMargin?.Text, out int m)) { cfgEdge["边缘触发余量_格"] = m; }
                if (int.TryParse(pendCool?.Text, out int c)) { cfgEdge["传送冷却_毫秒"] = c; }
                if (int.TryParse(pendBand?.Text, out int b)) { cfgEdge["边缘带宽度_格"] = b; }
                cfgEdge["首尾相连成环"] = (pendLoop?.Text ?? "false").Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
                if (cfgEdge["陆地边缘"] is JsonObject le)
                {
                    foreach (var (w, west, east) in edgeRows)
                    {
                        var o = le[w] as JsonObject ?? new JsonObject();
                        o["西"] = string.IsNullOrWhiteSpace(west.Text) ? null : (JsonNode?)JsonValue.Create(int.TryParse(west.Text, out int wv) ? wv : 0);
                        o["东"] = string.IsNullOrWhiteSpace(east.Text) ? null : (JsonNode?)JsonValue.Create(int.TryParse(east.Text, out int ev) ? ev : 0);
                        le[w] = o;
                    }
                }
                await Task.Run(() => FsWriteText(cfgEdgePath, cfgEdge.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
            }
            ERR($"已保存（旧配置已备份为 .{stamp}.bak）—— 世界列表在下一次整链重置时生效 ✓");
        }
        catch (Exception ex) { ERR("保存失败: " + ex.Message); }
    }

    // ===== 视图切换（不弹窗，直接换中间面板）=====
    private void ShowView(string which)
    {
        ConsoleView.IsVisible = which == "console";
        ConfigView.IsVisible = which == "config";
        ChainView.IsVisible = which == "chain";
        PluginsView.IsVisible = which == "plugins";
        MgrPluginsView.IsVisible = which == "mgrplugins";
        if (which != "map" && MapImage?.Source is Avalonia.Media.Imaging.Bitmap oldMap)
        {
            // ★ 自检修复：离开地图页就释放大位图（11958px 拼接图解码后约 13MB），否则拖窗口/切页都卡
            MapImage.Source = null;
            oldMap.Dispose();
        }
        MapView.IsVisible = which == "map";
        if (which == "console") { try { lastRemoteLogMs = 0; logPos = 0; lastLogFile = ""; RefreshLog(); } catch { } }   // 切回控制台立即补一次日志
        // 右侧菜单高亮当前视图（#66CCFF ✓）
        ExtView.IsVisible = which == "ext";
        MaintView.IsVisible = which == "maint";
        BtnMaint?.Classes.Set("active", which == "maint");
        ConnView.IsVisible = which == "conn";
        BtnConn?.Classes.Set("active", which == "conn");
        BtnChain?.Classes.Set("active", which == "chain");
        BtnConfig?.Classes.Set("active", which == "config");
        BtnPlugins?.Classes.Set("active", which == "plugins");
        BtnMap?.Classes.Set("active", which == "map");
        BtnMgrPlugins?.Classes.Set("active", which == "mgrplugins");
    }

    private void OnBackToConsole(object? sender, RoutedEventArgs e) => ShowView("console");

    // ===== 插件管理（视图切换；停用=改名 .disabled 不删除）=====
    // ===== 文件管理（类 MCSManager：浏览 utsl-deploy + 插件 .dll 启用开关）=====
    private string fileCwd = "";


    private static bool CopyLocal(string src, string dst) { try { File.Copy(src, dst, true); return true; } catch { return false; } }

    private void OnFileCancelTransfer(object? sender, RoutedEventArgs e) { CancelTransfer(); ERR("已请求取消传输…"); }

    // ===== 凭据加密（DPAPI）：对标 MCSManager CVE-2025-50691 —— 别把 token 明文写在本地文件里 =====
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct DataBlob { public int cbData; public IntPtr pbData; }
    [System.Runtime.InteropServices.DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptProtectData(ref DataBlob pDataIn, string? desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob pDataOut);
    [System.Runtime.InteropServices.DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob pDataOut);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr h);

    /// <summary>用当前 Windows 用户的 DPAPI 加密（只有本机本用户能解开）。失败返回空串。</summary>
    private static string DpapiProtect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) { return ""; }
        byte[] inBytes = System.Text.Encoding.UTF8.GetBytes(plain);
        IntPtr buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(inBytes.Length);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(inBytes, 0, buf, inBytes.Length);
            var inBlob = new DataBlob { cbData = inBytes.Length, pbData = buf };
            if (!CryptProtectData(ref inBlob, "UTSLManager", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out DataBlob outBlob)) { return ""; }
            try
            {
                byte[] outBytes = new byte[outBlob.cbData];
                System.Runtime.InteropServices.Marshal.Copy(outBlob.pbData, outBytes, 0, outBlob.cbData);
                return Convert.ToBase64String(outBytes);
            }
            finally { LocalFree(outBlob.pbData); }
        }
        catch { return ""; }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buf); }
    }

    private static string DpapiUnprotect(string b64)
    {
        if (string.IsNullOrEmpty(b64)) { return ""; }
        try
        {
            byte[] inBytes = Convert.FromBase64String(b64);
            IntPtr buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(inBytes.Length);
            try
            {
                System.Runtime.InteropServices.Marshal.Copy(inBytes, 0, buf, inBytes.Length);
                var inBlob = new DataBlob { cbData = inBytes.Length, pbData = buf };
                if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out DataBlob outBlob)) { return ""; }
                try
                {
                    byte[] outBytes = new byte[outBlob.cbData];
                    System.Runtime.InteropServices.Marshal.Copy(outBlob.pbData, outBytes, 0, outBlob.cbData);
                    return System.Text.Encoding.UTF8.GetString(outBytes);
                }
                finally { LocalFree(outBlob.pbData); }
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buf); }
        }
        catch { return ""; }
    }

    // ===== 传输进度与取消（对标面板的上传进度条；我们的链路只有 1.3~2.4 MB/s，长传输没有进度体验很差）=====
    public static Action<string>? TransferStatus;      // 由管理器接到状态栏（后台线程调用，内部会 Post 到 UI）
    private static volatile bool transferCancel;
    public static void CancelTransfer() { transferCancel = true; }
    private static void Progress(string s) { try { TransferStatus?.Invoke(s); } catch { } }

    /// <summary>
    /// 安全修复：把远端文件名净化成合法的本地文件名，避免「..\..\」这类名字造成路径穿越
    /// （下载目标目录之外写入）。只取最后一段并把非法字符替换成下划线。
    /// </summary>
    private static string SafeName(string name)
    {
        string n = (name ?? "").Replace('\\', '/');
        int i = n.LastIndexOf('/');
        if (i >= 0) { n = n.Substring(i + 1); }
        foreach (char ch in System.IO.Path.GetInvalidFileNameChars()) { n = n.Replace(ch, '_'); }
        n = n.Trim().TrimEnd('.');
        if (n.Length == 0 || n == "." || n == "..") { n = "unnamed"; }
        if (n.Length > 120) { n = n.Substring(0, 120); }
        return n;
    }

    /// <summary>下载一行文件：本机直接复制；远端走 scp → 桌面\utsl-downloads</summary>
    private async Task DownloadFileAsync(string full, string name)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "utsl-downloads");
            Directory.CreateDirectory(dir);
            string local = Path.Combine(dir, SafeName(name));   // ★ 安全：净化文件名，防止 ..\ 路径穿越写到目标目录之外
            long knownSize = 0;
            // ★ 速度优化（实测链路仅约 1.3MB/s，减少重复传输最有效）：远端大小与本机一致就跳过
            if (UseRemote)
            {
                long remoteSize = FsList(Path.GetDirectoryName(full) ?? fileCwd)
                    .Where(e => !e.IsDir && e.Name == Path.GetFileName(full)).Select(e => e.Size).FirstOrDefault();
                knownSize = remoteSize;
                if (File.Exists(local) && remoteSize > 0 && remoteSize == new FileInfo(local).Length)
                {
                    ERR("远端与本机版本一致，已跳过下载: " + local);
                    return;
                }
            }
            ERR("正在下载 " + name + " …（进度见下方状态栏，可随时取消）");
            bool ok = await Task.Run(() => UseRemote ? FsDownload(full, local, knownSize) : CopyLocal(full, local));
            ERR(ok ? ("已下载到 " + local) : ("下载失败: " + name));
        }
        catch (Exception ex) { ERR("下载失败: " + ex.Message); }
    }

    private async Task DeleteEntryAsync(string full)
    {
        try
        {
            string nm = Path.GetFileName(full.TrimEnd('\\', '/'));
            ERR("正在删除 " + nm + " …");
            bool ok = await Task.Run(() => FsDelete(full));
            ERR(ok ? ("已删除 " + nm) : ("删除失败: " + nm));
            ShowFiles();
        }
        catch (Exception ex) { ERR("删除失败: " + ex.Message); }
    }

    /// <summary>上传本地文件到当前目录（远端 scp / 本机复制）</summary>
    private async void OnFileUpload(object? sender, RoutedEventArgs e)
    {
        try
        {
            var top = Avalonia.Controls.TopLevel.GetTopLevel(this);
            if (top is null) { return; }
            var picked = await top.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "选择要上传到 " + fileCwd + " 的文件",
                AllowMultiple = false,
            });
            if (picked.Count == 0) { return; }
            string? local = picked[0].Path?.LocalPath;
            if (string.IsNullOrEmpty(local)) { ERR("无法获取本地路径"); return; }
            string name = Path.GetFileName(local);
            string remote = Path.Combine(fileCwd, name);
            ERR("正在上传 " + name + " …");
            bool ok = await Task.Run(() => UseRemote ? FsUpload(local, remote) : CopyLocal(local, remote));
            ERR(ok ? ("已上传 " + name) : ("上传失败: " + name));
            ShowFiles();
        }
        catch (Exception ex) { ERR("上传失败: " + ex.Message); }
    }

    private async void OnShowPlugins(object? sender, RoutedEventArgs e)
    {
        // ★ 即时反馈 + 无条件切视图（数据再失败也进得去）
        ShowView("plugins");
        if (PluginList is not null && PluginList.Children.Count == 0) { FileInfo.Text = "读取中…"; }
        if (string.IsNullOrEmpty(fileCwd) || !await Task.Run(() => FsExists(fileCwd))) { fileCwd = FsRoot; }
        ShowFiles();
    }

    private void OnFileUp(object? sender, RoutedEventArgs e)
    {
        string root = FsRoot.TrimEnd('\\', '/');
        string cur = fileCwd.TrimEnd('\\', '/');
        if (cur.Length <= root.Length) { ERR("已在根目录"); return; }
        int sep = cur.LastIndexOfAny(new[] { '\\', '/' });
        fileCwd = sep <= root.Length ? root : cur.Substring(0, sep);
        ShowFiles();
    }

    private void OnFileRoot(object? sender, RoutedEventArgs e) { fileCwd = FsRoot; ShowFiles(); }
    private void OnFileRefresh(object? sender, RoutedEventArgs e) => ShowFiles();

    private async void OnFileMkdir(object? sender, RoutedEventArgs e)
    {
        try
        {
            string dir0 = fileCwd;
            string name = await Task.Run(() =>
            {
                string nm = "新建文件夹"; int i = 1;
                while (FsExists(Path.Combine(dir0, nm))) { nm = "新建文件夹" + (++i); }
                return nm;
            });
            await Task.Run(() => FsMkdir(Path.Combine(fileCwd, name)));
            ShowFiles();
            ERR("已新建文件夹: " + name);
        }
        catch (Exception ex) { ERR("新建失败: " + ex.Message); }
    }

    private async void ShowFiles()
    {
        try
        {
            if (PluginList is null) { return; }
            // ★ 流畅性：先不清空 —— 上次的结果继续可见，新数据到了再替换（web 面板就是这么做的，
            //   之前先 Clear 再 await，观感就是"点了没反应"）
            if (PluginList.Children.Count == 0) { FileInfo.Text = "读取中…"; } else { FileInfo.Text = "刷新中…（显示的是上次结果）"; }
            string root2 = FsRoot;
            string rel = fileCwd.StartsWith(root2, StringComparison.OrdinalIgnoreCase)
                ? fileCwd.Substring(root2.Length).TrimStart('\\', '/') : fileCwd;
            FilePathText.Text = (UseRemote ? "远端 " + root2 : root2) + "\\" + rel;
            // ★ 远端模式下 FsList 是 SSH 调用，必须离开 UI 线程，否则窗口会「未响应」
            var entries = await Task.Run(() => FsList(fileCwd));
            PluginList.Children.Clear();   // 数据到手才替换
            if (entries.Count == 0 && !await Task.Run(() => FsExists(fileCwd))) { fileCwd = root2; return; }
            var dirs = entries.Where(e => e.IsDir).OrderBy(e => e.Name).ToList();
            var files = entries.Where(e => !e.IsDir).OrderBy(e => e.Name).ToList();
            FileInfo.Text = $"{dirs.Count} 个文件夹 · {files.Count} 个文件";

            void Row(string icon, string name, string sizeText, string timeText, bool isDir, string full, bool? enabled)
            {
                // 表格行：名称(*) | 大小(90) | 修改时间(130) | 启用(64)
                var rowWrap = new Border { BorderBrush = new SolidColorBrush(Color.Parse("#1E1E23")), BorderThickness = new Avalonia.Thickness(0, 0, 0, 1), Padding = new Avalonia.Thickness(14, 0, 14, 0) };
                var sp = new Grid { ColumnDefinitions = new Avalonia.Controls.ColumnDefinitions("*,90,130,64,120"), Height = 30 };
                var nameCell = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                nameCell.Children.Add(new TextBlock { Text = icon, FontFamily = new Avalonia.Media.FontFamily("Segoe MDL2 Assets, Microsoft YaHei"), FontSize = 12, Width = 20, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                var tb = new TextBlock { Text = name, FontSize = 12, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
                if (isDir) { tb.Foreground = new SolidColorBrush(Color.Parse("#79C0FF")); tb.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand); }
                nameCell.Children.Add(tb);
                Avalonia.Controls.Grid.SetColumn(nameCell, 0);
                sp.Children.Add(nameCell);
                var sizeTb = new TextBlock { Text = sizeText, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#8A8A90")), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                Avalonia.Controls.Grid.SetColumn(sizeTb, 1);
                sp.Children.Add(sizeTb);
                var timeTb = new TextBlock { Text = timeText, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#6E7681")), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                Avalonia.Controls.Grid.SetColumn(timeTb, 2);
                sp.Children.Add(timeTb);
                if (enabled is bool en)
                {
                    var cb = new CheckBox { IsChecked = en, FontSize = 11.5, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                    // ★ 修 bug#5：只去掉结尾的 .disabled（Replace 会删掉名字里所有 .disabled），
                    //   且目标已存在时不再 overwrite（File.Move(...,true) 会静默覆盖 → 丢文件）
                    string baseName = name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? name[..^".disabled".Length] : name;
                    cb.IsCheckedChanged += async (_, _) =>
                    {
                        try
                        {
                            bool want = cb.IsChecked == true;
                            string on = System.IO.Path.Combine(fileCwd, baseName);
                            string off = on + ".disabled";
                            if (want)
                            {
                                if (!FsExists(off)) { ERR("找不到 " + System.IO.Path.GetFileName(off)); }
                                else if (FsExists(on)) { ERR($"已存在 {System.IO.Path.GetFileName(on)}，为免覆盖已中止（请先手工处理）"); }
                                else { await Task.Run(() => FsRename(off, on)); ERR($"已启用 {baseName} —— 重启服务端后生效"); }
                            }
                            else
                            {
                                if (!FsExists(on)) { ERR("找不到 " + System.IO.Path.GetFileName(on)); }
                                else if (FsExists(off)) { ERR($"已存在 {System.IO.Path.GetFileName(off)}，为免覆盖已中止（请先手工处理）"); }
                                else { await Task.Run(() => FsRename(on, off)); ERR($"已停用 {baseName} —— 重启服务端后生效"); }
                            }
                            ShowFiles();
                        }
                        catch (Exception ex) { ERR("切换失败: " + ex.Message); ShowFiles(); }
                    };
                    Avalonia.Controls.Grid.SetColumn(cb, 3);
                    cb.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
                    sp.Children.Add(cb);
                }
                // ★ 行操作：下载 / 删除（两段式确认）；远端走 scp/SSH，本机直接文件操作
                var actSp = new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                };
                if (!isDir)
                {
                    var dl = new Button { Content = "下载", FontSize = 10, Padding = new Avalonia.Thickness(7, 1), CornerRadius = new Avalonia.CornerRadius(6) };
                    dl.Click += async (_, _) => await DownloadFileAsync(full, name);
                    actSp.Children.Add(dl);
                }
                var del = new Button
                {
                    Content = "删除", FontSize = 10, Padding = new Avalonia.Thickness(7, 1), CornerRadius = new Avalonia.CornerRadius(6),
                    Foreground = new SolidColorBrush(Color.Parse("#FF7B72")),
                };
                bool armed = false;
                del.Click += async (_, _) =>
                {
                    if (!armed) { armed = true; del.Content = "确认删除?"; return; }
                    await DeleteEntryAsync(full);
                };
                actSp.Children.Add(del);
                Avalonia.Controls.Grid.SetColumn(actSp, 4);
                sp.Children.Add(actSp);
                if (isDir) { tb.PointerPressed += (_, _) => { fileCwd = full; ShowFiles(); }; }
                rowWrap.Child = sp;
                PluginList.Children.Add(rowWrap);
            }

            foreach (var d in dirs) { Row("\uE8B7", d.Name, "", d.Mtime.ToString("MM-dd HH:mm"), true, Path.Combine(fileCwd, d.Name), null); }
            foreach (var x in files)
            {
                bool isDll = x.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !x.Name.StartsWith("TShockAPI", StringComparison.OrdinalIgnoreCase);
                bool isOff = x.Name.EndsWith(".dll.disabled", StringComparison.OrdinalIgnoreCase) && !x.Name.StartsWith("TShockAPI", StringComparison.OrdinalIgnoreCase);
                bool? en = (isDll || isOff) ? isDll : (bool?)null;
                Row("\uE7C3", x.Name, $"{x.Size / 1024} KB", x.Mtime.ToString("MM-dd HH:mm") + (isOff ? " · 已停用" : ""), false, Path.Combine(fileCwd, x.Name), en);
            }
            ShowView("plugins");
        }
        catch (Exception ex) { ERR("读取目录失败: " + ex.Message); }
    }

    // ===== 地图预览（视图切换 + 单世界切换 + 缩放）=====
    private double mapZoom = 1.0;
    private string mapDirLocal = "";
    private static string MapCacheDir => Path.Combine(Path.GetTempPath(), "utsl-map-cache");
    private static string MapRemoteDir => Path.Combine(RemoteDir, "GenerateMap", "Images");
    private static string MapLocalDir => Path.Combine(LocalDir, "GenerateMap", "Images");
    private static bool mapFullWanted;          // ★ 默认只下载缩略图（9.4MB→~0.6MB，首开从 7 秒降到不到 1 秒）

    /// <summary>在远端生成/复用缩略图（宽 1280，双线性）。实测链路约 1.3MB/s，9.4MB 原图要 7 秒，
    /// 缩略图约 0.6MB 不到 1 秒 —— 这是"文件加载速度"最有效的一招（换算法/压缩/并发都无提升，已实测）。</summary>
    private static string EnsureRemoteThumb(string remoteFull)
    {
        string thumb = remoteFull + ".thumb.png";
        string script =
            "$ProgressPreference='SilentlyContinue'; " +
            "Add-Type -AssemblyName System.Drawing; " +
            "$src='" + Q(remoteFull) + "'; $dst='" + Q(thumb) + "'; " +
            "if ((-not (Test-Path $dst)) -or ((Get-Item $dst).LastWriteTimeUtc -lt (Get-Item $src).LastWriteTimeUtc)) { " +
            "  $img=[Drawing.Image]::FromFile($src); $w=1280; if ($img.Width -le $w) { $w=$img.Width }; " +
            "  $h=[int]($img.Height * $w / $img.Width); if ($h -lt 1) { $h=1 }; " +
            "  $bmp=New-Object Drawing.Bitmap($w,$h); $g=[Drawing.Graphics]::FromImage($bmp); " +
            "  $g.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBilinear; $g.DrawImage($img,0,0,$w,$h); " +
            "  $img.Dispose(); $bmp.Save($dst,[Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose(); $g.Dispose() }; " +
            "'ok ' + (Get-Item $dst).Length";
        try { return SshRun(script, 180000); } catch { return ""; }
    }

    private async void OnMapLoadFull(object? sender, RoutedEventArgs e)
    {
        mapFullWanted = true;
        if (BtnMapFull is not null) { BtnMapFull.Content = "载入中…"; }
        LoadMap();
        await Task.Delay(1500);
        if (BtnMapFull is not null) { BtnMapFull.Content = "载入原图"; }
    }

    /// <summary>地图 PNG 列表：本机直接枚举；远端用 SSH 列目录（下载交给 PickMapFile）。</summary>
    private static List<(string Name, DateTime Mtime)> ListMapPng()
    {
        if (!UseRemote)
        {
            var di = new DirectoryInfo(MapLocalDir);
            if (!di.Exists) { return new(); }
            return di.GetFiles("*.png").Where(x => !x.Name.EndsWith(".thumb.png", StringComparison.OrdinalIgnoreCase))
                       .Select(x => (x.Name, x.LastWriteTime)).ToList();
        }
        return FsList(MapRemoteDir)
            // ★ 审查发现的 bug：缩略图（*.thumb.png）原来也被当成"地图"，
            //   于是会被再生成缩略图 → .thumb.png.thumb.png 递归叠加，还污染下拉列表与「载入原图」。
            .Where(e => !e.IsDir && e.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                     && !e.Name.EndsWith(".thumb.png", StringComparison.OrdinalIgnoreCase))
            .Select(e => (e.Name, e.Mtime)).ToList();
    }

    private void OnShowMap(object? sender, RoutedEventArgs e)
    {
        RefreshMapPicker();
        LoadMap();
        ShowView("map");
        Dispatcher.UIThread.Post(() => OnMapFit(null, new RoutedEventArgs()), DispatcherPriority.Background);
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        t.Tick += (_, _) => { t.Stop(); OnMapFit(null, new RoutedEventArgs()); };
        t.Start();
    }

    private void OnRefreshMap(object? sender, RoutedEventArgs e) { RefreshMapPicker(); LoadMap(); }

    private void RefreshMapPicker()
    {
        if (MapPicker is null) { return; }
        string sel = MapPicker.SelectedItem as string ?? "全部（拼接图）";
        var items = new List<string> { "全部（拼接图）" };
        items.AddRange(rows.Where(r => r.Name != "全部世界").Select(r => r.Name));
        MapPicker.ItemsSource = items;
        MapPicker.SelectedIndex = Math.Max(0, items.IndexOf(sel));
    }

    private void OnMapPickChanged(object? sender, SelectionChangedEventArgs e)
    {
        LoadMap();
        Dispatcher.UIThread.Post(() => OnMapFit(null, new RoutedEventArgs()), DispatcherPriority.Background);
    }

    private FileInfo? PickMapFile()
    {
        var all = ListMapPng();
        if (all.Count == 0) { return null; }
        string pick = MapPicker?.SelectedItem as string ?? "全部（拼接图）";
        string? chosen = pick.StartsWith("全部")
            ? (all.Where(x => x.Name.Contains("stitched")).OrderByDescending(x => x.Mtime).Select(x => x.Name).FirstOrDefault()
               ?? all.OrderByDescending(x => x.Mtime).Select(x => x.Name).FirstOrDefault())
            : all.Where(x => x.Name.StartsWith(pick + "_", StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x.Mtime).Select(x => x.Name).FirstOrDefault();
        if (chosen is null) { return null; }
        string remoteFull = Path.Combine(MapRemoteDir, chosen);
        // ★ 默认走缩略图：远端生成一次（宽 2048，约 0.6MB），之后本地缓存复用；点「载入原图」才拉 9MB 原图
        bool full = !UseRemote || mapFullWanted;
        string cacheName = full ? chosen : (chosen + ".thumb.png");
        string localPath = Path.Combine(UseRemote ? MapCacheDir : MapLocalDir, cacheName);
        if (UseRemote)
        {
            Directory.CreateDirectory(MapCacheDir);
            if (full)
            {
                if (!File.Exists(localPath) && !FsDownload(remoteFull, localPath)) { return null; }
            }
            else
            {
                string r = EnsureRemoteThumb(remoteFull);
                if (r.Contains("ok")) { FsDownload(remoteFull + ".thumb.png", localPath); }
                else if (!FsDownload(remoteFull, localPath)) { return null; }   // 缩略图生成失败 → 回退原图
            }
        }
        var fi = new FileInfo(localPath);
        return fi.Exists ? fi : null;
    }

    private async void LoadMap()
    {
        try
        {
            mapDirLocal = UseRemote ? MapCacheDir : MapLocalDir;
            MapInfo.Text = "读取中…";
            // ★ 远端模式会按需 scp 下载地图（可能几 MB），放后台线程
            var f = await Task.Run(() => PickMapFile());
            if (f is null) { MapInfo.Text = (UseRemote ? "远端 " + MapRemoteDir : MapLocalDir) + " 里没有该地图的图片（远端模式会自动下载到本地缓存）"; return; }
            if (MapImage.Source is IDisposable old) { old.Dispose(); }
            // ★ 内存优化：按上限宽解码（11958 宽全解码 = 115MB 位图 ✗）
            //   限制到 4096 → 4096×822×4 ≈ 13MB ✓ 内存降 9 倍 ✓ 视觉上 1:1 会略糊但可接受
            const double MaxDecodeWidth = 4096;
            Avalonia.Media.Imaging.Bitmap bmp;
            using (var fs = File.OpenRead(f.FullName))
            {
                bmp = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(fs, (int)MaxDecodeWidth);
            }
            MapImage.Source = bmp;
            MapImage.Width = bmp.PixelSize.Width * mapZoom;
            MapImage.Height = bmp.PixelSize.Height * mapZoom;
            MapInfo.Text = $"{f.Name} · {bmp.PixelSize.Width}×{bmp.PixelSize.Height} · {f.Length / 1048576.0:F1} MB · 缩放 {mapZoom * 100:F0}% · {f.LastWriteTime:MM-dd HH:mm}";
        }
        catch (Exception ex) { MapInfo.Text = "加载失败: " + ex.Message; }
    }

    private void ApplyZoom(double z)
    {
        mapZoom = Math.Clamp(z, 0.02, 4.0);
        if (MapImage.Source is Avalonia.Media.Imaging.Bitmap b)
        {
            MapImage.Width = b.PixelSize.Width * mapZoom;
            MapImage.Height = b.PixelSize.Height * mapZoom;
            string head = (MapInfo.Text ?? "").Split(" · 缩放")[0];
            MapInfo.Text = head + $" · 缩放 {mapZoom * 100:F0}%";
        }
    }

    private void OnMapZoomIn(object? sender, RoutedEventArgs e) => ApplyZoom(mapZoom * 1.25);
    private void OnMapZoomOut(object? sender, RoutedEventArgs e) => ApplyZoom(mapZoom / 1.25);
    private void OnMapActual(object? sender, RoutedEventArgs e) => ApplyZoom(1.0);

    private void OnMapFit(object? sender, RoutedEventArgs e)
    {
        if (MapImage.Source is not Avalonia.Media.Imaging.Bitmap b) { return; }
        double vw = MapScroll.Bounds.Width, vh = MapScroll.Bounds.Height;
        if (vw < 50) { vw = Math.Max(400, Bounds.Width - 320); }
        if (vh < 50) { vh = Math.Max(240, Bounds.Height - 220); }
        vw -= 30; vh -= 30;
        if (vw <= 10 || vh <= 10) { return; }
        ApplyZoom(Math.Min(vw / b.PixelSize.Width, vh / b.PixelSize.Height));
    }

    private void OnMapWheel(object? sender, Avalonia.Input.PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & Avalonia.Input.KeyModifiers.Control) == 0) { return; }
        ApplyZoom(e.Delta.Y > 0 ? mapZoom * 1.15 : mapZoom / 1.15);
        e.Handled = true;
    }

    private async Task SendAsyncOld()
    {
        await Task.CompletedTask;
    }

    // ===== 从备份恢复    // ===== 从备份恢复（二次确认）=====
    private bool restoreArmed;

    private async void OnRestoreClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            string backupDir = Path.Combine(LocalDir, "backup");
            var zips = new DirectoryInfo(backupDir).GetFiles("backup_*.zip").OrderByDescending(f => f.LastWriteTime).ToList();
            if (zips.Count == 0) { ERR("backup 目录里没有备份"); return; }
            var latest = zips[0];
            if (!restoreArmed)
            {
                restoreArmed = true;
                BtnRestore.Content = "确认恢复?";
                ERR($"再点一次将从【{latest.Name}】恢复 Worlds/config（会覆盖当前文件！建议先停服）");
                await Task.Delay(8000);
                if (restoreArmed) { restoreArmed = false; BtnRestore.Content = "恢复"; }
                return;
            }
            restoreArmed = false;
            BtnRestore.Content = "恢复中…";
            string root = @"C:\Users\星梦\Desktop\utsl-deploy";
            int n = 0, skipped = 0;
            string rootFull = Path.GetFullPath(root);
            string rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
            using (var zip = ZipFile.OpenRead(latest.FullName))
            {
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) { continue; }
                    // ★ 修 bug#2（zip-slip）：先归一化，再校验目标必须落在 utsl-deploy 之内
                    string target = Path.GetFullPath(Path.Combine(rootFull, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    try { entry.ExtractToFile(target, true); n++; } catch { }
                }
            }
            ERR($"已从 {latest.Name} 恢复 {n} 个文件" + (skipped > 0 ? $"（{skipped} 个越界条目已跳过）" : "") + " —— 建议重启服务端使其生效");
        }
        catch (Exception ex) { ERR("恢复失败: " + ex.Message); }
        await Task.Delay(2000);
        BtnRestore.Content = "恢复";
    }

    // ===== 整链重置（两次点击确认，防误触）=====
    private bool resetArmed;

    private async void OnResetClicked(object? sender, RoutedEventArgs e)
    {
        if (!resetArmed)
        {
            resetArmed = true;
            BtnReset.Content = "确认重置?";
            ERR("再点一次「确认重置?」将对全部世界执行 /reset（会重新生成源地图并切分替换）");
            await Task.Delay(6000);
            if (resetArmed) { resetArmed = false; BtnReset.Content = "整链重置"; }
            return;
        }
        resetArmed = false;
        BtnReset.Content = "重置中…";
        try
        {
            OnTrimMemory(null, new RoutedEventArgs());   // 重操作前先腾物理内存 ✓
            await Task.Delay(800);
            string url = Rest + "/world/command?token=" + Uri.EscapeDataString(Token) + "&world=all&cmd=" + Uri.EscapeDataString("/reset");
            string res = await Http.GetStringAsync(url);
            ProtectWatchdog(1800, "chain-reset");   // ★ 整链重置会重新建图+切分+重启，给足 30 分钟保护窗口
            Term("→ /reset (全部世界)  " + res, "#79C0FF");
            ERR("整链重置已触发，进度见左侧「服务器日志」面板（阶段1→重启→阶段2）");
        }
        catch (Exception ex) { ERR("触发重置失败: " + ex.Message); }
        await Task.Delay(3000);
        BtnReset.Content = "整链重置";
    }

    private void ERR(string msg) => ErrText.Text = msg;

    /// <summary>读 EdgeStitch 配置里的世界链，显示在侧栏底部。</summary>
    private async void RefreshChain()
    {
        try
        {
            string cfg = Path.Combine(FsRoot, "config", "EdgeStitch", "EdgeStitch.json");
            // ★ 自检修复：同步 SSH ×2 在 UI 线程 → 每次调用冻 2~4 秒；且原来用 LocalDir，远端模式下读的是本机
            if (!await Task.Run(() => FsExists(cfg))) { return; }
            using var doc = JsonDocument.Parse(await Task.Run(() => FsReadText(cfg)));
            if (doc.RootElement.TryGetProperty("世界链", out var arr))
            {
                var names = arr.EnumerateArray().Select(x => x.GetString()).Where(x => x is not null);
                ChainText.Text = "世界链: " + string.Join(" — ", names);
            }
        }
        catch { }
    }
    private void OnTitleBarPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { BeginMoveDrag(e); }
    }

    // ===== 服务端控制 =====
    private bool serverWasUp;
    private int downTicks;
    private int watchdogFailures;
    private bool warnedRemoteWatchdog;
    // ★ 换图/启动保护窗口（移植 PGame 的 watchdogWorldBuildSuppress 思路）：
    //   世界加载期间 REST 本来就不通，若用"REST 通不通"当存活判据会误判掉线 → 重启循环。
    private static long watchdogProtectUntilMs;

    /// <summary>开启保护窗口：启动/重启/整链重置后的一段时间内，看门狗不介入。</summary>
    private static void ProtectWatchdog(int seconds, string why)
    {
        watchdogProtectUntilMs = Environment.TickCount64 + seconds * 1000L;
        _ = why;
    }

    /// <summary>★ 修 bug#15：目标是否为本机 —— 远端目标绝不在本机拉起/停止服务端进程。</summary>
    private static bool TargetIsLocal => ConnHost is "127.0.0.1" or "localhost" or "::1" or "0.0.0.0";

    private static string ServerDir => LocalDir;
    private static string ServerExe => Path.Combine(LocalDir, "UnifierTSL.exe");

    private static bool ServerRunning => System.Diagnostics.Process.GetProcessesByName("UnifierTSL").Length > 0;

    private async void OnStartServer(object? sender, RoutedEventArgs e)
    {
        if (UseRemote)
        {
            try
            {
                ProtectWatchdog(600, "remote-start");   // ★ 启动后 10 分钟内看门狗不介入（世界加载可能很久）
                Term("--- 远端启动中（SSH + WMI + 控制台启动器），请稍候 ---", "#79C0FF");
                ErrText.Text = "远端启动中…";
                string r = await Task.Run(() => SshRun(RemoteStartScript(), 60000));
                Term(r, PickColor(r));
                ErrText.Text = "远端启动指令已下发";
            }
            catch (Exception ex) { ErrText.Text = "远端启动失败: " + ex.Message; }
            return;
        }
        if (ServerRunning) { return; }
        ProtectWatchdog(600, "local-start");   // ★ 启动后 10 分钟内看门狗不介入
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ServerExe)
            {
                WorkingDirectory = ServerDir,
                Arguments = "-joinserver first -logmode txt",
            });
        }
        catch (Exception ex) { ErrText.Text = "启动失败: " + ex.Message; }
    }

    private async void OnStopServer(object? sender, RoutedEventArgs e)
    {
        if (UseRemote)
        {
            try
            {
                Term("--- 远端停止中（只杀 UTSL，不动机器人）---", "#79C0FF");
                ErrText.Text = "远端停止中…";
                string r = await Task.Run(() => SshRun(RemoteStopScript(), 40000));
                Term(r, PickColor(r));
                ErrText.Text = "远端停止指令已下发";
            }
            catch (Exception ex) { ErrText.Text = "远端停止失败: " + ex.Message; }
            NotifyServerState(false);
            return;
        }
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("UnifierTSL")) { p.Kill(); }
        }
        catch (Exception ex) { ErrText.Text = "停止失败: " + ex.Message; }
        NotifyServerState(false);
    }

    private async void OnRestartServer(object? sender, RoutedEventArgs e)
    {
        ProtectWatchdog(600, "restart");   // ★ 重启后给世界加载留出时间，避免看门狗介入造成重启循环
        OnStopServer(sender, e);
        await Task.Delay(4000);
        OnStartServer(sender, e);
    }

    private void OnClose(object? sender, Avalonia.Input.PointerPressedEventArgs e) { Close(); }

    private void OnMin(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        WindowState = WindowState.Minimized;
        e.Handled = true;
    }

    private void OnMax(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        e.Handled = true;
    }
}