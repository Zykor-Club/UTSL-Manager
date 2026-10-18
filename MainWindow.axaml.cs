using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using UTSLManager.Backends;
using UTSLManager.Servers;
using SkiaSharp;
using UTSLManager.Maps;
using UTSLManager.Plugins;

namespace UTSLManager;

public sealed class WorldRow
{
    public string Name { get; set; } = "";
    public string Size { get; set; } = "";
    public bool Running { get; set; }
    public IBrush DotBrush => Running ? new SolidColorBrush(Color.Parse("#32D74B")) : new SolidColorBrush(Color.Parse("#6B6B70"));
}

/// <summary>左侧「服务器」卡片的一行（一台服务器一张卡）。属性变更会通知界面就地更新，不重建集合。</summary>
public sealed class ServerCardRow : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));

    public string Id { get; set; } = "";

    private string name = "";
    public string Name { get => name; set { if (name == value) { return; } name = value; Raise(nameof(Name)); } }

    private string address = "";
    public string Address { get => address; set { if (address == value) { return; } address = value; Raise(nameof(Address)); } }

    private string worlds = "—";
    public string Worlds { get => worlds; set { if (worlds == value) { return; } worlds = value; Raise(nameof(Worlds)); } }

    private bool isActive;
    public bool IsActive { get => isActive; set { if (isActive == value) { return; } isActive = value; Raise(nameof(IsActive)); } }

    private IBrush statusBrush = new SolidColorBrush(Color.Parse("#6B6B70"));
    public IBrush StatusBrush { get => statusBrush; set { if (ReferenceEquals(statusBrush, value)) { return; } statusBrush = value; Raise(nameof(StatusBrush)); } }

    private string tip = "";
    public string Tip { get => tip; set { if (tip == value) { return; } tip = value; Raise(nameof(Tip)); } }

    // ===== 卡片上的实时数据（后台常驻轮询提供，非激活档也有） =====
    private string state = "";
    public string State { get => state; set { if (state == value) { return; } state = value; Raise(nameof(State)); } }

    private string cpuText = "CPU —";
    public string CpuText { get => cpuText; set { if (cpuText == value) { return; } cpuText = value; Raise(nameof(CpuText)); } }

    private string memText = "内存 —";
    public string MemText { get => memText; set { if (memText == value) { return; } memText = value; Raise(nameof(MemText)); } }

    private string netText = "↑— ↓—";
    public string NetText { get => netText; set { if (netText == value) { return; } netText = value; Raise(nameof(NetText)); } }
}

/// <summary>终端里的一行（文字 + 颜色）。
/// ★ 三列显示（时间 / 级别 / 消息）：服务端日志行会被拆成三列，只给"级别"上色；
///   管理层自己写的消息（"已切换到…"）不拆列，原样落在消息列。
///   过滤 / 暂停计数 / 复制 仍然只看 Text（原始整行），所以这些功能的语义完全没变。</summary>
public sealed class TermLine
{
    private static readonly System.Text.RegularExpressions.Regex LogRx = new(
        @"^(?<t>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d+)\s+[+-]\d\d:\d\d\s+\[(?<lv>[A-Za-z]+)\](?<rest>.*)$",   // 时间列不含时区偏移（省 7 个字符，够宽不贴边）
        System.Text.RegularExpressions.RegexOptions.Compiled);

    // Immutable 画刷：不可变、无 UI 线程亲和性（TermLine 可能在后台线程构造）
    private static readonly Avalonia.Media.Immutable.ImmutableSolidColorBrush NeutralText =
        new(Avalonia.Media.Color.Parse("#D9FFFFFF"));
    private static readonly Avalonia.Media.Immutable.ImmutableSolidColorBrush LvInfo =
        new(Avalonia.Media.Color.Parse("#30D158"));
    private static readonly Avalonia.Media.Immutable.ImmutableSolidColorBrush LvWarn =
        new(Avalonia.Media.Color.Parse("#FFD600"));
    private static readonly Avalonia.Media.Immutable.ImmutableSolidColorBrush LvErr =
        new(Avalonia.Media.Color.Parse("#FF4245"));
    private static readonly Avalonia.Media.Immutable.ImmutableSolidColorBrush LvDebug =
        new(Avalonia.Media.Color.Parse("#8CFFFFFF"));

    private string _text = "";
    public string Text
    {
        get => _text;
        set { _text = value ?? ""; Parse(); }
    }

    public IBrush Brush { get; set; } = new SolidColorBrush(Color.Parse("#B9B9C0"));

    /// <summary>是否拆成了三列（服务端日志行才有时间/级别）。</summary>
    public bool HasColumns { get; private set; }
    public string ColTime { get; private set; } = "";
    public string ColLevel { get; private set; } = "";
    public string ColText { get; private set; } = "";
    /// <summary>级别色：只给级别上色；消息一律中性色（整行染色会把日志看得更吵）。</summary>
    public IBrush ColLevelBrush { get; private set; } = LvInfo;
    public IBrush ColTextBrush => HasColumns ? NeutralText : Brush;

    private void Parse()
    {
        var m = LogRx.Match(_text);
        if (!m.Success)
        {
            HasColumns = false; ColTime = ""; ColLevel = ""; ColText = _text;
            return;
        }
        HasColumns = true;
        ColTime = m.Groups["t"].Value;
        ColLevel = m.Groups["lv"].Value.ToUpperInvariant();
        ColText = m.Groups["rest"].Value.TrimStart();
        ColLevelBrush = ColLevel switch
        {
            "WARN" or "WARNING" => LvWarn,
            "ERROR" or "ERR" or "FATAL" => LvErr,
            "DEBUG" or "TRACE" => LvDebug,
            _ => LvInfo,           // Info / OK / Success 都是绿
        };
    }
}

public partial class MainWindow : Window
{
    // ===== 连接配置：多服务器（servers.json；一档 = 一台服务器）=====
    //   ★ 关键设计：下面这一整组"这台服务器"的字段全部是【转发到当前激活档】的属性。
    //     全仓约 212 处引用这些名字（Token/UseRemote/LocalDir/FsRoot/SshHost…），改成属性后
    //     读点一行都不用动，写点只剩 LoadConn / SaveConn / 连接设置页三处。
    private static ServersConfig ServersCfg = new();
    private static ServerProfile ActiveSrv = ServersConfig.NewLocal();

    private static string ConnHost { get => ActiveSrv.Host; set => ActiveSrv.Host = value; }
    private static int ConnPort { get => ActiveSrv.Port; set => ActiveSrv.Port = value; }
    private static string Token { get => ActiveSrv.Token; set => ActiveSrv.Token = value; }   // ★ 明文只在内存，落盘一律 DPAPI 加密
    private static string ManagerLanguage = "zh-CN";     // 界面语言是全局偏好，不属于任何一台服务器
    private static string Rest => "http://" + ConnHost + ":" + ConnPort;
    private static string ConnFile => Path.Combine(AppContext.BaseDirectory, "connection.json");   // 旧单档配置：迁移来源，原样保留（回滚用）
    private static string ServersFile => Path.Combine(AppContext.BaseDirectory, "servers.json");

    private static void LoadConn()
    {
        try
        {
            // servers.json 不存在 → 把旧 connection.json 迁移成第一条档并立刻落盘（迁移只发生一次；旧文件原样保留）
            ServersCfg = ServersConfig.LoadOrMigrate(ServersFile, ConnFile);
            // ★ 联调/验证用：--server <档id 或档名> 启动即激活那一台（与 --open-plugin 同一套 StartupArgs）
            string want = StartupArg("--server");
            if (want.Length > 0)
            {
                var hit = ServersCfg.Find(want)
                          ?? ServersCfg.Servers.FirstOrDefault(s => string.Equals(s.Name, want, StringComparison.OrdinalIgnoreCase));
                if (hit is not null) { ServersCfg.ActiveId = hit.Id; }
            }
            ActiveSrv = ServersCfg.ActiveOrFirst();
            ManagerLanguage = ServersCfg.Language.Length > 0 ? ServersCfg.Language : ManagerLanguage;
            // ★ 明文令牌（旧 connection.json 或手改过的 servers.json）→ 立刻换成 DPAPI 密文落盘
            if (ServersCfg.HadPlainToken) { SaveConn(); }
            ApplyConsoleMode();
        }
        catch { }
    }

    /// <summary>取启动参数 <paramref name="name"/> 后面那个值（没有则空串）。</summary>
    private static string StartupArg(string name)
    {
        string[] a = Program.StartupArgs;
        for (int i = 0; i < a.Length - 1; i++)
        {
            if (string.Equals(a[i], name, StringComparison.OrdinalIgnoreCase)) { return a[i + 1]; }
        }
        return "";
    }

    /// <summary>
    /// 落盘 = 写 servers.json（所有档 + 当前激活档）。连接设置页/模式开关/切档都走这里。
    /// ★ 令牌一律 DPAPI 加密（同机其它用户读到也解不开）；加密不可用才退回明文并在终端提示。
    /// </summary>
    /// <summary>最近一次 servers.json 落盘失败的原因（成功为空）。</summary>
    public static string LastConfigSaveError = "";

    private static void SaveConn()
    {
        try
        {
            ServersCfg.Language = ManagerLanguage;
            ServersCfg.ActiveId = ActiveSrv.Id;
            if (!ServersCfg.Save(ServersFile))
            {
                // ★ 不能静默吞：这一份文件里是全部档 + DPAPI 令牌，没落盘等于这次修改白做。
                LastConfigSaveError = "✗ 保存 servers.json 失败（配置未落盘）: " + ServersConfig.LastSaveError;
                Progress(LastConfigSaveError);
                // ★ 状态行会被"切档/插件重载"这类自己的文案秒盖掉（实测），所以再写两处留痕：
                //   服务器终端（当场可见）+ switchperf.log（事后可查、可断言）。
                try { TerminalSink?.Invoke(LastConfigSaveError, "#FF7B72"); } catch { }
                try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "switchperf.log"),
                        $"{DateTime.Now:HH:mm:ss.fff}	SAVE-FAIL	{LastConfigSaveError}" + Environment.NewLine); } catch { }
                return;
            }
            LastConfigSaveError = "";
        }
        catch (Exception ex) { LastConfigSaveError = ex.Message; Progress("✗ 保存 servers.json 异常: " + ex.Message); }
    }
    // ===== 路径配置 =====
    // 每档自带 paths.localDir（默认「当前用户桌面\utsl-deploy」）；这里是转发到激活档的属性。
    private static string LocalDir { get => ActiveSrv.LocalDir; set => ActiveSrv.LocalDir = value; }
    /// <summary>把"是否显示世界控制台窗口"同步给两条启动路径（本机直启 / agent 拉起）。</summary>
    private static void ApplyConsoleMode()
    {
        bool headless = !ShowWorldConsoles;
        LocalProcessBackend.HeadlessConsole = headless;
        AgentClient.HeadlessConsole = headless;
    }

    /// <summary>
    /// 是否显示"世界控制台窗口"（UTSL 的 PerWorld 模式）。默认【不显示】（headless）——
    /// 那个窗口每个世界一个、且关掉会被会话驱动重新拉起，而管理器本身已能看日志与发指令。
    /// 注意：模式在服务端【进程启动时】决定，改这个开关要重启服务端才生效。
    /// </summary>
    private static bool ShowWorldConsoles { get => ActiveSrv.WorldConsoles; set => ActiveSrv.WorldConsoles = value; }

    /// <summary>掉线自启：服务端进程消失时自动拉起。默认开，可关并记住。</summary>
    private static bool WatchdogEnabled { get => ActiveSrv.Watchdog; set => ActiveSrv.Watchdog = value; }
    private static string Logs => Path.Combine(LocalDir, "logs");
    private static int RemoteRestPort { get => ActiveSrv.RemoteRestPort; set => ActiveSrv.RemoteRestPort = value; }   // 远端 TShock REST 端口（隧道另一端）
    private static int TunnelPid { get => ActiveSrv.TunnelPid; set => ActiveSrv.TunnelPid = value; }                  // 该档的隧道 ssh 进程（多档各一条，互不复用）
    private static bool TunnelAlive;              // ★ 状态灯：隧道可用
    private static bool RestAlive;                // ★ 状态灯：REST 接口可用
    private static bool lastDotTunnel, lastDotShell, lastDotRest, dotsInit;

    // ===== 远端（SSH）配置：connection.json 里的 "ssh" 段；填了就走远端启停/读日志 =====
    private static string SshHost { get => ActiveSrv.SshHost; set => ActiveSrv.SshHost = value; }
    private static int SshPort { get => ActiveSrv.SshPort; set => ActiveSrv.SshPort = value; }
    private static string SshUser { get => ActiveSrv.SshUser; set => ActiveSrv.SshUser = value; }
    private static string SshKey { get => ActiveSrv.SshKey; set => ActiveSrv.SshKey = value; }
    private static string RemoteDir { get => ActiveSrv.RemoteDir; set => ActiveSrv.RemoteDir = value; }
    private static string RemoteDotnet { get => ActiveSrv.RemoteDotnet; set => ActiveSrv.RemoteDotnet = value; }
    private static string RemoteLauncher { get => ActiveSrv.RemoteLauncher; set => ActiveSrv.RemoteLauncher = value; }
    // ===== 直连（面板）模式：agent 直接暴露在网络上、用令牌认证，不经 SSH 隧道 =====
    //   适合"我就想 MCSM 面板那样直连"的用户；安全由 agent 侧的令牌 + 绑定红线保证。
    private static bool DirectMode { get => ActiveSrv.Direct; set => ActiveSrv.Direct = value; }             // 用户勾选
    private static string DirectHost { get => ActiveSrv.DirectHost; set => ActiveSrv.DirectHost = value; }   // 例：1.2.3.4 或域名
    private static int DirectPort { get => ActiveSrv.DirectPort; set => ActiveSrv.DirectPort = value; }
    private static string DirectToken { get => ActiveSrv.DirectToken; set => ActiveSrv.DirectToken = value; }
    /// <summary>直连是否真的可用（勾了 + 地址与令牌都齐）。</summary>
    private static bool DirectReady => ActiveSrv.DirectReady;

    private static bool HasSsh => ActiveSrv.HasSsh;
    /// <summary>连接设置页的「本机管理 / 远端管理」开关：勾到本机时即便填了 SSH 也按本机跑（不丢配置）。</summary>
    private static bool ForceLocal { get => ActiveSrv.ForceLocal; set => ActiveSrv.ForceLocal = value; }
    // ★ 注意：管理器是经 SSH 隧道连远端的，ConnHost 就是 127.0.0.1 —— 所以不能用 TargetIsLocal 判定，
    //   只认「connection.json 里配了 ssh 段 + 没被开关强制成本机」。
    //   直连模式同样算"远端"（进程/文件都走 agent，而不是本机）
    private static bool UseRemote => ActiveSrv.IsRemote;

    /// <summary>
    /// 管理器自己的 HTTP 客户端：对 127.0.0.1（本机 REST / SSH 隧道 / agent）【必须禁用代理】。
    /// ★ 实测踩过：这台机器开着系统代理（WinINET ProxyEnable=1），HttpClient 默认会走代理，
    ///   于是发往 127.0.0.1:7879 的 agent 请求被交给代理 → 失败/404（curl 同样表现，实测确认）。
    ///   管理工具的本地/隧道调用一律不该经过代理。
    /// </summary>
    private static HttpClient NewLocalHttp(TimeSpan timeout)
        => new(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(5) }) { Timeout = timeout };

    private static readonly HttpClient Http = NewLocalHttp(TimeSpan.FromSeconds(15));
    // ★ 每台服务器各自一份 UI 集合，常驻内存，切换只换引用（O(1)）—— 这是"切档不像在重建"的关键。
    //   参考 PGame-TSManager：每个 ServerContainer 自带 FlowDocument/Paragraph 日志缓冲，
    //   切换服务器 = ComboBox 换 SelectedItem，日志/状态全部现成，没有重建与空窗。
    private ObservableCollection<WorldRow> rows = [];
    private readonly Dictionary<string, ObservableCollection<WorldRow>> rowsByServer = [];
    private readonly ObservableCollection<ServerCardRow> srvCards = [];   // 左侧服务器卡片（一台一张）
    private readonly List<ServerCardRow> srvRows = [];
    private ObservableCollection<TermLine> term = [];
    private readonly Dictionary<string, ObservableCollection<TermLine>> termByServer = [];

    // ★ 过滤层：Raw（term/termByServer）永远是全量，显示用 termView（过滤后的子集）。
    //   这样"改关键字"随时可以重新解释整段历史；若直接在 Raw 上过滤，暂停/缓存期间的行就再也找不回来了。
    private ObservableCollection<TermLine> termView = [];
    private readonly Dictionary<string, ObservableCollection<TermLine>> termViewByServer = [];
    private readonly Dictionary<string, string> termFilterByServer = [];
    private bool suppressFilterEvent;      // 代码改过滤框文本时不要触发重建
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
        // ★ 传输进度显示在底部状态栏（后台线程调用 → Post 回 UI）
        TerminalSink = (text, color) => Term(text, color);
        TransferStatus = (msg) =>
        {
            try { Dispatcher.UIThread.Post(() => { if (ErrText is not null) { ErrText.Text = msg; } }); } catch { }
        };
        // ★ L5 窗口装饰：窗口失焦时三个"红绿灯"变淡（macOS 的做法），重新聚焦时恢复
        try
        {
            Activated += (_, __) => SetTrafficLightsActive(true);
            Deactivated += (_, __) => SetTrafficLightsActive(false);
            SetTrafficLightsActive(IsActive);
            ToolTip.SetTip(CpuNow, "占单核 % / 整机满槽 %（满槽 = 核数 × 100）");
        }
        catch { }
        Program.Log("ctor:begin");
        LoadConn();
        Program.Log("ctor:LoadConn done");
        WarmupRemote();
        Program.Log("ctor:WarmupRemote queued");   // ★ 启动即后台预热常驻通道与常用配置，第一次点击就秒开   // ★ 必须在任何 REST 调用之前（TSManager 也是先读配置再启动 ✓）
        WorldList.ItemsSource = rows;
        ServerCards.ItemsSource = srvCards;
        TermLines.ItemsSource = termView;
        Term("UTSLManager 终端已就绪 —— 上方是服务端日志流，下方可直接输入指令", "#8E8E93");
        // 启动打点：诊断"点了图标到底起没起来 / 什么时候起来的"（与 switchperf.log 同文件，写满自动清）
        try
        {
            string pf = Path.Combine(AppContext.BaseDirectory, "switchperf.log");
            var fi0 = new FileInfo(pf);
            if (fi0.Exists && fi0.Length > 256 * 1024) { File.WriteAllText(pf, ""); }
            File.AppendAllText(pf, $"{DateTime.Now:HH:mm:ss.fff}	start pid={Environment.ProcessId}	active={ActiveSrv.Id}	args={string.Join(" ", Program.StartupArgs)}" + Environment.NewLine);
        }
        catch { }
        if (ServersCfg.Migrated)
        {
            Term($"已把旧的 connection.json 迁移成 servers.json（{ServersCfg.Servers.Count} 台服务器；旧文件原样保留，回滚时拷回即可）", "#30D158");
        }
        UpdateServerCards();
        Term($"当前服务器：{ActiveSrv.Name}（{ActiveSrv.AddressText}）· {(UseRemote ? "远端" : "本机")} · 左侧卡片可切换", "#8E8E93");
        rows.Add(new WorldRow { Name = "全部世界", Size = "", Running = true });
        WorldList.SelectedIndex = 0;
        WorldList.SelectionChanged += (_, _) =>
        {
            UpdateTarget();
            try { pluginHost?.RaiseWorldChanged((WorldList.SelectedItem as WorldRow)?.Name ?? "全部世界"); } catch { }
        };

        SendBtn.Click += async (_, _) => await SendAsync();
        if (ChkWatchdog is not null)
        {
            ChkWatchdog.IsChecked = WatchdogEnabled;
            ChkWatchdog.IsCheckedChanged += (_, _) =>
            {
                WatchdogEnabled = ChkWatchdog.IsChecked == true;
                try { SaveConn(); } catch { }
                ERR(WatchdogEnabled ? "掉线自启：已开启（并记住）" : "掉线自启：已关闭（并记住）");
            };
        }
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

        // ★ 服务端 /metrics 轮询（2 秒）—— 远端模式的实时指标来源；插件不存在时会失败并回退快照
        var metricsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        // ★ 不要用"失败闩锁"：服务端重启/插件临时不可用之后再也不会重试，指标会永久退化。
        //   改成失败退避（失败后 10 秒再试），这样服务端恢复后自动回到实时指标。
        metricsTimer.Tick += (_, _) =>
        {
            if (switching) { return; }                // ★ 切档期间一律不采样（否则旧档的数值会写进新档的图）
            if (worldDlBusy) { return; }              // 下载世界文件时让路（同一根隧道）
            if (!UseRemote) { return; }
            if (Environment.TickCount64 < MetricsNextTryMs) { return; }
            RefreshMetricsAsync();
        };
        metricsTimer.Start();

        var listTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        listTimer.Tick += async (_, _) =>
        {
            if (switching) { return; }
            UpdateHealthDots();                       // 状态灯永远刷新（本地判断，零成本）
            if (!IsActive) { return; }                // ★ 自适应节流：窗口不在前台就跳过轮询与采样
            await RefreshWorldsAsync();
            SampleMetrics();
        };
        listTimer.Start();

        var logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        logTimer.Tick += (_, _) => { if (switching || !IsActive) { return; } RefreshLog(); };   // ★ 失焦不刷日志；切档期间不碰日志游标
        logTimer.Start();
            // 远端模式：每 20 秒确保 SSH 隧道还活着（断了自动重建）
            var tunnelTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            tunnelTimer.Tick += (_, _) => { if (switching) { return; } if (HasSsh) { _ = Task.Run(() => EnsureTunnel()); } _ = Task.Run(() => TickAgentAsync()); };   // ★ agent 探活与本机/远端无关（本机 7879 / 远端走隧道 17879）   // 自检修复：隧道探测也放后台
            tunnelTimer.Start();
        _ = Task.Run(() => TickAgentAsync());     // 启动即探一次 agent（否则要等 20 秒）
        // ★ 后台档常驻监控：每 5 秒扫一遍"没在看的"服务器（内部按档分频：世界 20s / 指标 5s）。
        //   效果：切到任何一台都是现成的（世界列表 + 三张图曲线 + 状态），不用等 REST。
        {
            var bgTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            bgTimer.Tick += (_, _) =>
            {
                if (switching || worldDlBusy) { return; }   // 下载世界文件时让路，别抢隧道
                try { SyncBackgroundLogs(); } catch { }
                _ = Task.Run(PollBackgroundAsync);
            };
            bgTimer.Start();
            _ = Task.Run(async () => { await Task.Delay(4000); _ = PollBackgroundAsync(); });
        }
        // ★ 非激活档轻量探活（60 秒一次，只探端口，不开隧道）→ 卡片状态点不再是"上次已知"
        {
            var probeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            probeTimer.Tick += async (_, _) => { if (switching) { return; } await ProbeInactiveServersAsync(); };
            probeTimer.Start();
            _ = Task.Run(async () => { await Task.Delay(8000); Dispatcher.UIThread.Post(() => _ = ProbeInactiveServersAsync()); });
        }
        // ★ 联调钩子：--switch-test <档id|档名> → 启动 6 秒后自动切过去并写 switchcheck.txt（真机验证用）
        {
            string st = StartupArg("--switch-test");
            if (st.Length > 0)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(6000);
                    Dispatcher.UIThread.Post(() => _ = RunSwitchTestAsync(st));
                });
            }
        }

        // ★ 150% DPI 下窗口初始位置/尺寸会算错，导致右侧跑到屏幕外（控制区看不见）
        //   这里按屏幕工作区强制钳制并居中。
        Opened += (_, _) =>
        {
            Program.Log("window:Opened");
            ClampToScreen();
            Program.Log("window:Clamped");
        };

        var chainTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };   // ★ 10s→30s：侧栏世界链摘要不必那么勤（且已走 90s 缓存）
        chainTimer.Tick += (_, _) =>
        {
            // 快照始终刷新（只发一次 ssh 覆盖多个页面），窗口失焦时也保持数据新鲜
            if (Environment.TickCount64 - SnapshotAtMs > 45000) { _ = Task.Run(() => RefreshSnapshot()); }
            if (!IsActive) { return; }
            RefreshChain();
        };
        chainTimer.Start();
        Program.Log("ctor:timers done");
        RefreshChain();
        Program.Log("ctor:RefreshChain queued");
        InitPluginHost();
        Program.Log("ctor:InitPluginHost done");
        pluginLoadedProfileId = ActiveSrv.Id;      // 启动时插件就是按这张档加载的
        ApplyStartupArgs();
        Program.Log("ctor:ApplyStartupArgs done");
        _ = RefreshWorldsAsync();
        RefreshLog();
        Program.Log("ctor:end");
        // 激活档令牌为空（典型：刚用「＋」建的档）→ 启动时也自动从服务端配置补一次，否则世界列表一直是空的
        if (ActiveSrv.Token.Length == 0)
        {
            var target = ActiveSrv;
            _ = Task.Run(async () =>
            {
                bool got = await TryFillTokenAsync(target);
                Dispatcher.UIThread.Post(() =>
                {
                    if (got)
                    {
                        SaveConn();
                        ERR($"「{target.Name}」令牌为空 → 已从服务端配置自动读到（REST 端口 {target.Port}），已保存");
                        _ = RefreshWorldsAsync();
                    }
                    else { ERR($"「{target.Name}」令牌为空 → 世界列表为空属预期，请到「连接设置 → 自动检测」填一次"); }
                });
            });
        }

        // 关闭时通知插件（P0-4：保存状态 / 停后台任务 / 卸载可回收 ALC）
        Closing += (_, _) =>
        {
            try { pluginHost?.Shutdown(); } catch { }
            // ★ 退出时收隧道：实测强杀/正常关都【不会】带走 ssh 子进程 → 端口被占 + 任务管理器堆 ssh，
            //   下一次 ssh -L 还会因 ExitOnForwardFailure=yes 直接起不来。
            try { foreach (var p in ServersCfg.Servers) { KillTunnel(p); } } catch { }
        };
    }

    private void UpdateTarget()
    {
        var row = WorldList.SelectedItem as WorldRow;
        string name = row?.Name ?? "全部世界";
        TargetText.Text = "目标: " + (name == "全部世界" ? "全部世界" : name);
        if (PromptText is not null) { PromptText.Text = name == "全部世界" ? "@ 全部世界" : "@ " + name; }
    }

    private static bool WorldsFetching;                // 防重入（REST 未就绪时单次最长 15 秒，2 秒定时器会叠出多个请求）

    private async Task RefreshWorldsAsync()
    {
        // ★ 原来没有防重入：REST 未就绪（换图/加载期）时每 2 秒再发一次 GET，最多 7 个并发在飞；
        //   而每个 catch 都会 downTicks++，于是"连续 2 次失败"被同一个真实故障快速凑满 → 掉线自启误判/重复触发。
        if (WorldsFetching) { return; }
        WorldsFetching = true;
        try { await RefreshWorldsCoreAsync(); }
        finally { WorldsFetching = false; }
    }

    private async Task RefreshWorldsCoreAsync()
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
            ActiveSrv.Status = 1; ActiveSrv.WorldCount = on;      // ★ 卡片上的状态点/世界数就来自这里
            UpdateServerCards();
            if (SrvDot is not null) { SrvDot.Background = GetBrush("#32D74B"); }   // ★ 用画刷缓存：这条每 2 秒跑一次，原来每轮 new 一个
            serverWasUp = true;
            downTicks = 0;
            watchdogFailures = 0;
            // ★ 保存失败要"顶着"显示，直到下一次保存成功 —— 否则会被插件重载/切档这些状态文案秒盖（实测）。
            ErrText.Text = LastConfigSaveError;
            if (changed) { try { pluginHost?.RaiseWorldListRefreshed(RowsSnapshot()); } catch { } }
            NotifyServerState(true);
        }
        catch (Exception ex)
        {
            // ★ 远端模式必须用【远端】存活判据，否则会被本机进程误导成"未运行"
            // 直连模式没有 ssh 快照，存活判据用 agent 的 /metrics；否则远端看快照、本机看进程
            bool aliveNow = DirectReady ? AgentServerRunning : UseRemote ? SnapshotProcs > 0 : ServerRunning;
            ConnState.Text = aliveNow ? "REST 无响应" : "未运行";
            ActiveSrv.Status = aliveNow ? 2 : 3;
            UpdateServerCards();
            if (SrvDot is not null) { SrvDot.Background = new SolidColorBrush(Color.Parse(aliveNow ? "#FEBC2E" : "#6B6B70")); }
            // 掉线自启：本来在跑 → 现在没了 → 自动拉起
            downTicks++;
            // ★ 存活判据改成「进程是否还在」，不再用「REST 通不通」——
            //   世界加载/换图期间 REST 本来就没就绪，用 REST 判会把"正在加载"误判成"掉线"→ 重启循环（调研 D-6 的坑）。
            bool procAlive = aliveNow;
            if (procAlive)
            {
                downTicks = 0;      // 进程还在 → 只是 REST 未就绪（加载中），掉线自启不介入
            }
            else if (WatchdogEnabled && !ActiveSrv.UserStopped && serverWasUp && downTicks >= 2)
            {
                downTicks = 0;
                // ★ 换图保护窗口：刚启动/重启/整链重置后，世界加载需要时间（大图可达数分钟），期间不自动重启
                if (Environment.TickCount64 < watchdogProtectUntilMs)
                {
                    int left = (int)((watchdogProtectUntilMs - Environment.TickCount64) / 1000);
                    Wd($"掉线自启：进程不在，但仍在启动/换图保护窗口内（剩余约 {left} 秒），暂不自动重启");
                }
                else if (!LivenessKnown())
                {
                    // ★ 看不到服务端状态时不能"猜"它挂了：agent 掉线/快照过期时也报"未运行"，
                    //   原来的逻辑会因此把好好的服务端重启一遍（本地 agent 升级时真实发生过）。
                    Wd("掉线自启：暂时看不到服务端状态（agent 或快照不可用），本轮不自动重启");
                }
                else
                {
                    // 掉线自启只决定"要不要重启"（保护窗口与熔断）；重启动作交给进程后端
                    watchdogFailures++;
                    if (watchdogFailures >= 3)
                    {
                        ChkWatchdog!.IsChecked = false;
                        watchdogFailures = 0;
                        Wd("⚠ 掉线自启已熔断：连续 3 次自动重启后服务端仍未存活，已停止重试（请手动检查）");
                    }
                    else
                    {
                        Wd(ProcessBackend.IsRemote
                            ? $"掉线自启：远端服务端进程已不在，正在自动重启…（第 {watchdogFailures}/3 次）"
                            : $"掉线自启：服务端已退出，正在自动重启…（第 {watchdogFailures}/3 次）");
                        ProtectWatchdog(600, "watchdog");
                        _ = Task.Run(async () =>
                        {
                            ProcessResult r = await ProcessBackend.StartAsync();
                            if (!r.Ok) { Dispatcher.UIThread.Post(() => Wd("掉线自启：" + r.Message)); }
                        });
                    }
                }
            }
            if (serverWasUp) { NotifyServerState(false); }
        }
    }

    // ===== 终端：攒批 / 溢出留痕 / 暂停滚动 =====
    private readonly List<TermLine> termPending = [];
    private DispatcherTimer? termFlushTimer;
    private int termDropped;        // 因上限被丢掉的旧行总数（给标题显示，避免"以为日志断流"）
    private int termPausedNew;      // 暂停滚动期间新增的行数

    /// <summary>往终端追加一行（带颜色）。★ 不直接进集合：先入待办，由 50ms 定时器一次性刷进去。
    /// 原来每行一次集合 Add + 一次 ScrollIntoView 的 Dispatcher.Post —— 日志高峰（服务端刷屏）时
    /// 每秒几十次布局，UI 会顿；攒批后同样的行数只需一次布局。</summary>
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
        // 事件总线（P1-5）：订阅者要实时，逐行触发；再入保护防止插件在处理里又写日志造成递归
        if (!raisingTerminal && pluginHost is not null && pluginHost.Events.HasTerminalLineSubscribers)
        {
            raisingTerminal = true;
            try { pluginHost.RaiseTerminalLine(text, LevelOf(color)); }
            catch { }
            finally { raisingTerminal = false; }
        }
        termPending.Add(new TermLine { Text = text, Brush = GetBrush(color) });
        if (ChkAutoScroll?.IsChecked != true) { termPausedNew++; }   // 暂停滚动：只计数，不打断用户看的内容
        if (termFlushTimer is null)
        {
            termFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            termFlushTimer.Tick += (_, _) => FlushTermPending();
            termFlushTimer.Start();
        }
    }

    /// <summary>把攒下的行一次性写进终端（50ms 一次）。</summary>
    private void FlushTermPending()
    {
        if (termPending.Count == 0) { return; }
        // ★ 写"当前激活档"的缓冲：字段 term 是"当前显示的那一份"，但它可能被切档过程短暂置换成
        //   别的档（历史 bug：切到新档后终端出现上一台的日志）。按 ActiveSrv.Id 路由可以从根上避免。
        foreach (var line in termPending) { AppendTermLine(ActiveSrv.Id, line); }
        termPending.Clear();
        if (ChkAutoScroll?.IsChecked == true)
        {
            try { if (termView.Count > 0) { TermLines.ScrollIntoView(termView[^1]); } } catch { }
        }
        UpdateTermTitle();
    }

    /// <summary>一行的唯一写入口：Raw 全量 → 命中过滤才进显示视图 → 顺便维护丢弃/暂停/标题。
    /// ★ 服务端日志（TermTo）和管理层消息（Term→Flush）都走这里，计数才不会只统计一半。</summary>
    private void AppendTermLine(string profileId, TermLine line)
    {
        var raw = TermFor(profileId);
        raw.Add(line);
        int dropped = 0;
        while (raw.Count > MaxTermLines) { raw.RemoveAt(0); dropped++; }
        bool active = string.Equals(profileId, ActiveSrv.Id, StringComparison.Ordinal);
        if (!active) { return; }                       // 非激活档的显示视图等切过去时再重建
        bool matches = MatchesFilter(profileId, line);
        if (matches)
        {
            var view = TermViewFor(profileId);
            view.Add(line);
            while (view.Count > MaxTermLines) { view.RemoveAt(0); }
        }
        if (dropped > 0) { termDropped += dropped; }
        if (ChkAutoScroll?.IsChecked != true && matches) { termPausedNew++; }
        UpdateTermTitle();
    }

    private string FilterFor(string profileId)
        => termFilterByServer.TryGetValue(profileId, out var q) ? q : "";

    /// <summary>这一行是否命中过滤词（空过滤 = 全通过）。大小写不敏感；用 IndexOf 而不是正则：
    /// 日志里的正则元字符太多，用户随手输入一个 "[" 就让整条过滤失效，得不偿失。</summary>
    private bool MatchesFilter(string profileId, TermLine line)
    {
        string q = FilterFor(profileId);
        return q.Length == 0 || (line.Text ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>取某档的"显示视图"。
    /// ★ 激活档必须返回【字段 termView 本体】—— ListBox 绑的是这个字段；
    ///   如果这里另建一个字典集合并往里写，界面就永远看不到过滤结果（实测：view=175 但界面仍是全量）。</summary>
    private ObservableCollection<TermLine> TermViewFor(string id)
    {
        if (string.Equals(id, ActiveSrv.Id, StringComparison.Ordinal)) { return termView; }
        if (!termViewByServer.TryGetValue(id, out var v))
        {
            v = []; termViewByServer[id] = v;
            foreach (var line in TermFor(id)) { if (MatchesFilter(id, line)) { v.Add(line); } }
            while (v.Count > MaxTermLines) { v.RemoveAt(0); }
        }
        return v;
    }

    /// <summary>过滤词变了 → 按 Raw 重建显示视图（Raw 不动，所以随时能改回来）。</summary>
    private void RebuildTermView(string profileId)
    {
        var raw = TermFor(profileId);
        var view = TermViewFor(profileId);
        view.Clear();
        foreach (var line in raw) { if (MatchesFilter(profileId, line)) { view.Add(line); } }
        while (view.Count > MaxTermLines) { view.RemoveAt(0); }

        if (string.Equals(profileId, ActiveSrv.Id, StringComparison.Ordinal))
        {
            if (ChkAutoScroll?.IsChecked == true) { try { if (view.Count > 0) { TermLines.ScrollIntoView(view[^1]); } } catch { } }
            UpdateTermTitle();
        }
    }

    /// <summary>过滤框文本变化：写入该档的过滤词并重建视图。</summary>
    private void OnTermFilterChanged(object? sender, TextChangedEventArgs e)
    {
        if (suppressFilterEvent || TbTermFilter is null) { return; }
        termFilterByServer[ActiveSrv.Id] = TbTermFilter.Text ?? "";
        RebuildTermView(ActiveSrv.Id);
    }

    /// <summary>标题上写清"丢了多少行 / 暂停期间新增多少行"——静默丢弃会让人以为日志断流。</summary>
    private void UpdateTermTitle()
    {
        if (TermTitle is null) { return; }
        string t = "服务器终端";
        string q = FilterFor(ActiveSrv.Id);
        if (q.Length > 0) { t += $"（过滤「{q}」：显示 {termView.Count}/{term.Count} 行）"; }
        if (termDropped > 0) { t += $"（更早的 {termDropped} 行已丢弃）"; }
        if (ChkAutoScroll?.IsChecked != true && termPausedNew > 0) { t += $"（已暂停，期间新增 {termPausedNew} 行）"; }
        if (TermTitle.Text != t) { TermTitle.Text = t; }
    }

    /// <summary>自动滚动勾掉 = 暂停（继续写缓冲，只是不跟到底）；勾回来 = 一次滚到底。</summary>
    private void OnAutoScrollToggle(object? sender, RoutedEventArgs e)
    {
        if (ChkAutoScroll?.IsChecked == true)
        {
            termPausedNew = 0;
            try { if (term.Count > 0) { TermLines.ScrollIntoView(term[^1]); } } catch { }
        }
        UpdateTermTitle();
    }

    /// <summary>Ctrl+C：有选中行才复制（否则不拦，留给默认行为）。</summary>
    private void OnTermKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.C && e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control))
        {
            if (CopySelectedTerm()) { e.Handled = true; }
        }
    }

    private void OnCopyTermClick(object? sender, RoutedEventArgs e) => CopySelectedTerm();

    private bool CopySelectedTerm()
    {
        try
        {
            var picked = TermLines.SelectedItems is null
                ? new List<string>()
                : TermLines.SelectedItems.OfType<TermLine>().Select(l => l.Text).ToList();
            if (picked.Count == 0) { return false; }
            string text = string.Join(Environment.NewLine, picked);
            var clip = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clip is null) { ERR("复制失败：拿不到剪贴板"); return false; }
            _ = clip.SetTextAsync(text);
            ERR($"已复制 {picked.Count} 行到剪贴板");
            return true;
        }
        catch (Exception ex) { ERR("复制失败: " + ex.Message); return false; }
    }

    private static string LevelOf(string color) => color switch
    {
        "#FF7B72" => "error",
        "#E3B341" => "warning",
        "#7EE787" => "success",
        "#9AA0A6" or "#8E8E93" => "info",
        "#848D9A" => "debug",
        _ => "normal",
    };

    private static string PickColor(string line)
    {
        if (line.Contains("[Error]")) { return "#FF7B72"; }
        if (line.Contains("[Warning]") || line.Contains("[Warn]")) { return "#E3B341"; }
        if (line.Contains("[Success]")) { return "#7EE787"; }
        if (line.Contains("[Info]")) { return "#9AA0A6"; }
        if (line.Contains("[Debug]")) { return "#848D9A"; }
        return "#B9B9C0";
    }

    // ===== 统一文件访问：本机直连 / 远端 SSH，统一由 IFileBackend 提供（本文件不再分叉）=====
    private static IFileBackend? _fileBackend;
    /// <summary>后端懒加载的互斥锁。★ UI 线程（构造流程）与后台线程（快照预热/页面加载）会同时首次触达，
    /// 原来无锁 → 可能各 new 一个装饰器，后写者覆盖字段，而先返回的调用方继续用「落选」实例；
    /// 落选实例的 90 秒缓存再也清不掉（FsInvalidate 只清字段上那一个）→ 上传/保存后目录列表仍是旧的。</summary>
    private static readonly object BackendGate = new();

    /// <summary>连接配置重载（本机/远端切换或根目录变化）时自动重建后端。</summary>
    private static IFileBackend FileBackend
    {
        get
        {
            // ★ 先算好 root 再进锁：AgentClient.RemoteRoot 首次会发一次 HTTP（60 秒超时），
            //   放在锁内会让任何文件访问都可能持锁阻塞（我自己刚引入的问题，自查发现）。
            // ★ 远端模式下也优先用 agent（前提：它可达 + 它自报的根与远端根一致）。
            //   实测原因：远端 SSH 的每一次文件操作都要 5~8 秒（TCP + SSH 握手多轮往返，在 1.67 秒 RTT 的链路上尤其明显），
            //   而 agent 是同一个隧道上的一次 HTTP（本机 50~200ms）—— 对编辑器保存 / 配置保存这类高频小写入是数量级差别。
            //   两个前提缺一不可：不可达、或根不一致（例如本该读远端却连上了本机 agent）时，一律退回原来的 SSH 后端。
            bool agentOk = DirectReady
                || (UseRemote && AgentClient.Alive && AgentClient.RemoteRoot.Length > 0
                    && AgentClient.RemoteRoot.Equals(RemoteDir, StringComparison.OrdinalIgnoreCase));
            string root = DirectReady ? AgentClient.RemoteRoot : UseRemote ? RemoteDir : LocalDir;
            lock (BackendGate)
            {
                var b = _fileBackend;
                if (b is null || b.IsRemote != (UseRemote || agentOk) || b.Root != root || _lastDirect != agentOk)
                {
                    b = new CachedFileBackend(agentOk ? new AgentFileBackend(root)
                        : UseRemote ? new RemoteFileBackend(new UtslSshTransport(), root) : new LocalFileBackend(root));
                    _fileBackend = b; _lastDirect = agentOk;
                }
                return b;
            }
        }
    }

    private static bool _lastDirect;        // 上次 FileBackend 是否按直连模式构建（模式变了要重建）

    private static IProcessBackend? _proc;

    /// <summary>建 _proc 时用的指纹（本机/远端 + 部署目录 + 启动参数 + 环境变量）。任一项变了就重建。</summary>
    private static string _procKey = "";

    /// <summary>服务端进程操作；本机/远端切换时自动重建。</summary>
    private static IProcessBackend ProcessBackend
    {
        get
        {
            var p = _proc;
            // ★ 后端里烧进了「部署目录 + 启动参数 + 环境变量」，这三样任一变就必须重建：
            //   原来只在 IsRemote 变化时重建 → 切到另一份本机部署后，停/启动还对着【上一个目录】下手
            //   （上轮是"两档参数恰好不同"才碰巧触发重建，属于幸存偏差）。
            string want = (UseRemote ? "R" : "L") + "|" + ServerDir + "|"
                        + (ActiveSrv.StartArgs ?? "").Trim() + "|" + (ActiveSrv.StartEnv ?? "").Trim();
            if (p is null || p.IsRemote != UseRemote || _procKey != want)
            {
                p = UseRemote
                    ? new RemoteProcessBackend(new UtslSshTransport(), RemoteStartScript, RemoteStopScript, ActiveSrv.StartArgs ?? "", ActiveSrv.StartEnv ?? "")
                    : new LocalProcessBackend(ServerExe, ServerDir, ActiveSrv.StartArgs ?? "", ActiveSrv.StartEnv ?? "");
                _proc = p;
                _procKey = want;
            }
            return p;
        }
    }

    private static IEnvironmentInfo? _env;

    /// <summary>环境路径与文案；连接配置重载（本机/远端切换或目录变化）时自动重建。</summary>
    private static IEnvironmentInfo Env
    {
        get
        {
            string root = UseRemote ? RemoteDir : LocalDir;
            var e = _env;
            if (e is null || e.IsRemote != UseRemote || e.FileRoot != root)
            {
                e = UseRemote
                    ? new RemoteEnvironmentInfo(root, MapRemoteDir, MapCacheDir)
                    : new LocalEnvironmentInfo(root, MapLocalDir);
                _env = e;
            }
            return e;
        }
    }

    /// <summary>把既有 SshRun/scp 暴露给远端文件后端；传输的进度与取消仍沿用既有实现。</summary>
    private sealed class UtslSshTransport : ISshTransport
    {
        public string Host => SshHost;
        public int Port => SshPort;
        public string User => SshUser;
        public string KeyPath => SshKey;
        public string Run(string psScript, int timeoutMs = 30000) => SshRun(psScript, timeoutMs);
        public bool CopyToRemote(string localPath, string remotePath, Action<string>? progress = null, Func<bool>? cancelled = null)
            => FsUpload(localPath, remotePath);
        public bool CopyFromRemote(string remotePath, string localPath, long knownTotal = 0, Action<string>? progress = null, Func<bool>? cancelled = null)
            => FsDownload(remotePath, localPath, knownTotal);
    }

    // 短 TTL 缓存：文件后端有自己的缓存（CachedFileBackend）；这一份仍供其它读取路径使用
    /// <summary>
    /// 把快照已经取到的内容灌进【真正被读的】缓存（Backends/CachedFileBackend）。
    /// ★ 原来这里写的是一个静态死字典 FsMemo（全仓没有读取点），快照"一次 ssh 覆盖所有页面"的
    ///   设计因此从未生效 —— 页面读文件仍各发一趟 ssh。现在直接预热被读的缓存。
    /// </summary>
    private static void WarmCache(string path, string? text = null, List<FsEntry>? list = null, bool? exists = null)
    {
        if (_fileBackend is not CachedFileBackend c) { return; }
        if (text is not null) { c.WarmText(path, text); }
        if (list is not null) { c.WarmList(path, list); }
        if (exists is bool b) { c.WarmExists(path, b); }
    }

    private static string Q(string s) => s.Replace("'", "''");
    /// <summary>C:\a\b → /C:/a/b（Windows OpenSSH 的 scp 远端路径必须带前导斜杠）</summary>
    private static string ScpPath(string winPath)
    {
        string p = winPath.Replace('\\', '/');
        return (p.Length > 1 && p[1] == ':') ? "/" + p : p;
    }

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
    // ★ 服务端 /metrics 插件（MetricsChannel）—— 远端模式的实时指标来源（实测单次 98~147ms，可 2 秒级）
    private static double MetricsCpuPct, MetricsUpKbps, MetricsDownKbps, MetricsPrivateMB;
    // 网卡累计字节的 3 秒缓存 + 上一次采样时间（用于算真实速率分母）—— 见 SampleMetrics 里的说明
    private static long netCacheSent = 0, netCacheRecv = 0, netCacheAtMs = 0, lastBytesAtMs = 0;
    // （原来还声明了 MetricsGcMB / MetricsLogicalCpu / MetricsThreads / MetricsPlayers / MetricsWorlds，
    //   全仓只有赋值、没有读取 —— 已删，报表里那几行的解析也一并去掉）
    private static long MetricsAtMs;                  // 最近一次成功取值的时间
    private static bool MetricsFetching;              // 防重入
    private static long MetricsNextTryMs;             // 失败后的下次重试时间（退避，避免每 2 秒猛打）
    private static int SnapshotProcs;                 // 快照里的服务端进程数
    private static string SnapshotListen = "";        // 快照里的监听端口

    /// <summary>
    /// 一次 ssh 拿回所有常用数据（进程数/监听端口/config.json/EdgeStitch.json/根目录列表），
    /// 直接预热【真正被读的】文件缓存（CachedFileBackend）—— 之后三个页面读缓存即刻返回，
    /// 不需要各自再发一次 ssh（每次 3~8 秒）。
    /// </summary>
    private static int snapshotBusy;      // 在飞闩锁：单次快照最长 60 秒，而定时器每 30 秒就会再触发

    private static void RefreshSnapshot()
    {
        if (worldDlBusy) { return; }      // 正在下载世界文件：别抢隧道
        // ★ 没有闩锁时：一次快照超过 45 秒，下一个 tick 会再起一个完整快照 —— 两者并发抢 SshGate，
        //   还会重复写同一批 Snap* 静态字段（速率差分基准被搅乱 → 指标忽高忽低）。
        if (Interlocked.CompareExchange(ref snapshotBusy, 1, 0) != 0) { return; }
        try
        {
            string root = FsRoot;
            string cfg = Path.Combine(root, "config", "config.json");
            string edge = Path.Combine(root, "config", "EdgeStitch", "EdgeStitch.json");
            if (!UseRemote)
            {
                // 本机模式：直接读，无需 ssh
                WarmCache(cfg, File.Exists(cfg) ? File.ReadAllText(cfg) : "");
                WarmCache(edge, File.Exists(edge) ? File.ReadAllText(edge) : "");
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
                // ★ 预热目录必须写【管理器真正读的】相对路径：原来写的是根上的 'Worlds'/'ServerPlugins'，
                //   而真实世界在 lib\Saves\Worlds（根上的 Worlds 是另一回事）、ServerPlugins 这个目录根本不存在
                //   → 快照等于白做，文件管理页/世界相关缓存仍然各自走一趟 ssh。
                "$o.d=[ordered]@{}; foreach($n in @('config','lib\\Saves\\Worlds','lib\\Saves\\Worlds\\_utpl_templates','logs','plugins')){ $p=Join-Path $b $n; if(Test-Path -LiteralPath $p){ $o.d[$n]=@(Get-ChildItem -LiteralPath $p -ErrorAction SilentlyContinue|ForEach-Object{ $(if($_.PSIsContainer){'D'}else{'F'})+'|'+$_.Length+'|'+$_.LastWriteTime.ToString('o')+'|'+$_.Name }) } }; " +
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
            // ★ 只有【真读到内容】才写缓存。原来无条件 WarmCache(cfg, cfgText)：一旦这次 ssh 读取失败
            //   （超时/链路抖动 → cfgText 为空串）就会把"空文本"写进缓存，而快照每 45 秒重来一次 → 持续投毒。
            //   实机症状：世界链页面把"读不到"当成"没装 EdgeStitch"（用户反馈"我明明是装了的"）。
            if (cfgText.Length > 0) { WarmCache(cfg, cfgText); WarmCache(cfg, exists: true); }
            if (edgeText.Length > 0) { WarmCache(edge, edgeText); WarmCache(edge, exists: true); }
            if (entries.Count > 0)
            {
                WarmCache(root, list: entries);
                WarmCache(root, exists: true);
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
            if (mapEntries.Count > 0) { WarmCache(MapRemoteDir, list: mapEntries); }
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
                        WarmCache(subPath, list: sub);
                        WarmCache(subPath, exists: true);
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
        finally { Volatile.Write(ref snapshotBusy, 0); }   // ★ 必须释放：否则一次异常/提前返回就让快照永久停摆
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
                // （原来这里会为旧地图页预热 GenerateMap 的拼接图缩略图，要跑一次 ssh 生成缩略图。
                //   地图改成管理器本进程解析 .wld 渲染后，这步已无意义，去掉也省掉一次 ssh 往返。）
            }
            catch { }
        });
    }

    private static string FsRoot => FileBackend.Root;

    private static void FsInvalidate()
    {
        if (_fileBackend is CachedFileBackend c) { c.InvalidateCache(); }
    }

    /// <summary>只失效某个路径（+ 它所在目录的列表）。★ 上传/写文件后用这个：
    /// 原来用 FsInvalidate()（整张清空）会把 45 秒合并快照刚预热好的缓存全部丢掉，之后每个页面又各走一趟 ssh。</summary>
    private static void FsInvalidatePath(string path)
    {
        if (_fileBackend is CachedFileBackend c) { c.InvalidatePath(path); }
    }

    private static List<FsEntry> FsList(string dir) => FileBackend.List(dir).ToList();
    private static bool FsExists(string path) => FileBackend.Exists(path);
    private static string FsReadText(string path) => FileBackend.ReadText(path);
    private static bool FsWriteText(string path, string text) => FileBackend.WriteText(path, text);
    private static bool FsMkdir(string dir) => FileBackend.CreateDirectory(dir);
    private static bool FsRename(string from, string to) => FileBackend.Rename(from, to);
    private static bool FsDelete(string path) => FileBackend.Delete(path);

    /// <summary>本地 → 远端（scp；远端路径用 /C:/ 形式）</summary>
    private static bool FsUpload(string localPath, string remotePath)
    {
        FsInvalidatePath(remotePath);
        try
        {
            // ★ 不重定向 stdout/stderr：scp 的进度是往 stderr 刷的，管道写满就会阻塞子进程，
            //   而我们这里的循环只 WaitForExit、从不读管道 → 上传会一直卡到 600 秒超时（A 子代理实测指出）。
            //   这里只关心退出码，不重定向即可（上传进度本来就是按"本地文件大小 + 已用时间"估的）。
            var psi = new System.Diagnostics.ProcessStartInfo("scp") { UseShellExecute = false, CreateNoWindow = true };
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
            // ★ 重下前先删掉旧文件：否则 scp 覆盖写入期间"文件大小"可能比上一轮还小，
            //   进度条就会从 30% 掉回 20%（用户实测遇到的就是这个）。
            try { if (File.Exists(localPath)) { File.Delete(localPath); } } catch { }
            // ★ 同上：不重定向（否则 stderr 写满会卡死下载循环，只能等 180 秒超时）
            var psi = new System.Diagnostics.ProcessStartInfo("scp") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-C");     // ★ 压缩：实测 6.2MB 世界 1.11 → 1.54 MB/s（.wld 可压）
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(SshKey);
            psi.ArgumentList.Add("-P"); psi.ArgumentList.Add(SshPort.ToString());
            psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("BatchMode=yes");
            psi.ArgumentList.Add(SshUser + "@" + SshHost + ":" + ScpPath(remotePath));
            psi.ArgumentList.Add(localPath);
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) { return false; }
            // ★ 进度：scp 边传边写目标文件，轮询本地文件大小即可得到真实进度 + 预计剩余时间；并支持取消
            transferCancel = false;
            worldDlBusy = true;          // 下载期间后台轮询让路
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long total = knownTotal;
            long lastReported = 0;
            while (!p.WaitForExit(400))
            {
                if (transferCancel) { try { p.Kill(true); } catch { } Progress("传输已取消"); return false; }
                if (sw.ElapsedMilliseconds > 180000) { try { p.Kill(true); } catch { } Progress("传输超时（180 秒）"); return false; }
                long done = 0;
                try { var fi = new FileInfo(localPath); if (fi.Exists) { done = fi.Length; } } catch { }
                if (done < lastReported) { done = lastReported; } else { lastReported = done; }   // 单调不倒退
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
            worldDlBusy = false;
            // ★ 必须看 scp 退出码：原来只要目标文件存在就返回 true —— scp 失败（远端文件被删/权限不足）时
            //   会把上一次留下的旧文件或这次只写了一半的文件当成"下载成功"，调用方静默用错数据。
            return p.ExitCode == 0 && File.Exists(localPath);
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

        // 1.5) 多服务器配置（servers.json）：档数 / 激活档 / 每档端口 / 令牌是不是密文
        Run("多服务器配置", () =>
        {
            LoadConn();
            int n = ServersCfg.Servers.Count;
            bool activeOk = ServersCfg.Find(ActiveSrv.Id) is not null;
            string txt = File.Exists(ServersFile) ? File.ReadAllText(ServersFile) : "";
            bool noPlain = txt.Length > 0 && !txt.Contains("\"token\"");
            bool encOk = txt.Contains("tokenEnc");
            bool portsOk = ServersCfg.Servers.Select(s => s.Port).Distinct().Count() == n
                        && ServersCfg.Servers.Select(s => s.AgentPort).Distinct().Count() == n;
            return (activeOk && portsOk && noPlain,
                $"{n} 档 激活「{ActiveSrv.Name}」({ActiveSrv.AddressText}) 端口 REST {ActiveSrv.Port}/agent {ActiveSrv.AgentPort} · 档间端口唯一={portsOk} · 无明文令牌={noPlain} · 有 tokenEnc={encOk}");
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
            string mapDir = Env.MapDir;
            var pngs = FsList(mapDir).Where(e => !e.IsDir && e.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).ToList();
            if (pngs.Count == 0) { return (false, mapDir + " 下没有 png（可用 GenerateMap 插件生成）"); }
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
            string up = Path.Combine(ScratchDir, "sc_up.bin");
            var payload = new byte[64 * 1024];
            new Random(20251011).NextBytes(payload);
            File.WriteAllBytes(up, payload);
            string remote = Path.Combine(RemoteDir, "selfcheck_probe.bin");
            try
            {
                if (!FileBackend.Upload(up, remote)) { return (false, "上传失败"); }
                long rsize = FsList(RemoteDir).Where(e => e.Name == "selfcheck_probe.bin").Select(e => e.Size).FirstOrDefault();
                string back = Path.Combine(ScratchDir, "sc_down.bin");
                if (File.Exists(back)) { File.Delete(back); }
                bool down = FileBackend.Download(remote, back);
                bool same = down && File.Exists(back) && File.ReadAllBytes(back).SequenceEqual(payload);
                FsDelete(remote);
                bool gone = !FsList(RemoteDir).Any(e => e.Name == "selfcheck_probe.bin");
                return (same && gone, $"上传 {payload.Length} B（远端 {rsize} B）→ 下载逐字节比对 {(same ? "一致" : "不一致")} → 删除 {(gone ? "已清除" : "仍残留")}");
            }
            finally
            {
                try { File.Delete(up); } catch { }
                try { File.Delete(Path.Combine(ScratchDir, "sc_down.bin")); } catch { }   // ★ 审查发现：下载临时文件原先没清理
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
            bool ok = FileBackend.Download(Path.Combine(dir, png.Name), local);
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

    /// <summary>
    /// 轮询服务端 /metrics（MetricsChannel 插件）。实测单次 98~147ms，走同一条 SSH 隧道，不需要新端口。
    /// 失败（服务器没装该插件）时不清空旧值：SampleMetrics 会按新鲜度自动回退到 SSH 合并快照。
    /// </summary>
    private async void RefreshMetricsAsync()
    {
        if (!UseRemote || MetricsFetching) { return; }
        MetricsFetching = true;
        try
        {
            string txt = await Http.GetStringAsync(Rest + "/metrics?token=" + Uri.EscapeDataString(Token));
            if (JsonNode.Parse(txt) is JsonObject o)
            {
                MetricsCpuPct = Num(o["cpuPercent"]);
                MetricsUpKbps = Num(o["upKbps"]);
                MetricsDownKbps = Num(o["downKbps"]);
                MetricsPrivateMB = Num(o["privateMB"]);
                MetricsAtMs = Environment.TickCount64;
                // （原来这里还解析 gcHeapMB/logicalCpu/threads/players/worlds 并赋值给仅写不读的静态字段 —— 已删）
                MetricsNextTryMs = 0;
            }
        }
        catch { MetricsNextTryMs = Environment.TickCount64 + 10000; }   // 失败 10 秒后重试（服务端重启期间就是这条）
        finally { MetricsFetching = false; }
    }

    private static double Num(JsonNode? n)
    {
        try { return n is null ? 0 : double.Parse(n.ToString(), System.Globalization.CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    /// <summary>刷新连接健康三灯（只在状态变化时改画刷，避免每 2 秒产生垃圾）。</summary>
    private void UpdateHealthDots()
    {
        try
        {
            bool tunnel = TunnelAlive;
            // ★ 原来这里读的是 RemoteShell.Status（那个常驻通道早已停用、Status 恒为"未启用"）→
            //   远端模式下这盏灯永远橙色，让人以为通道坏了。改用真实的 ssh 健康度（最近一次调用是否失败）。
            bool sshOk = HasSsh && LastSshError.Length == 0;
            bool rest = RestAlive;
            if (dotsInit && tunnel == lastDotTunnel && sshOk == lastDotShell && rest == lastDotRest) { return; }
            dotsInit = true; lastDotTunnel = tunnel; lastDotShell = sshOk; lastDotRest = rest;
            SetDot(DotTunnel, !HasSsh ? "#6B6B70" : tunnel ? "#30D158" : "#FF9F0A");
            SetDot(DotShell, !HasSsh ? "#6B6B70" : sshOk ? "#30D158" : "#FF9F0A");
            SetDot(DotRest, rest ? "#30D158" : "#FF9F0A");
        }
        catch { }
    }

    private static void SetDot(Avalonia.Controls.Border? dot, string hex)
    {
        if (dot is null) { return; }
        dot.Background = new SolidColorBrush(Color.Parse(hex));
    }

    // ===== 云端 agent（UTSLDaemon，云管 P0）=====
    //   远端没有 .NET 10，所以 agent 以【自包含单文件】部署在 C:\utsl-agent\UTSLDaemon.exe，
    //   由计划任务 UTSLAgent 常驻，只监听 127.0.0.1:7879。传输走【已有的那条常驻 ssh 隧道】
    //   多转发一个端口 —— 能力像 MCSManager 的 daemon，但公网暴露面仍然是零。
    //   有 agent：日志改推送（实测 154 ms）、指标可 2 秒级；没有：自动回落到原有 ssh 路径。
    private static int AgentLocalPort => ActiveSrv.AgentPort;    // 该档的 agent 隧道本地入口（原来硬编码 17879；多档不分开就会连到同一台 agent → 串台）
    private const int AgentRemotePort = 7879;    // 远端 agent 端口（只绑回环）
    private static bool AgentAlive;
    // agent 指标（远端没装 MetricsChannel 插件时的 2 秒级来源；网络是累计字节，速率由管理器差分）
    private static double AgentCpuPct, AgentPrivateMB;
    private static int AgentCores;          // 远端机器核数（agent /metrics 根节点 cores）→ 把"单核 %"换算成"满槽 %"
    private static long AgentSentBytes = -1, AgentRecvBytes = -1, AgentMetricsAtMs;
    private static long lastAgentSent = -1, lastAgentRecv = -1;
    // ★ 复用，别每轮 new；且禁用代理（本地/隧道地址走代理必失败）
    private static readonly HttpClient AgentHttp = NewLocalHttp(TimeSpan.FromSeconds(4));
    private static TextBlock? AgentStatusText;
    private static string lastAgentScpErr = "";
    private static string AgentBaseUsed = "";
    private static string AgentProbeErr = "";   // 最近一次探测失败原因（排查用，显示在连接设置页）   // ★ 当前流连的是哪个 base（本机 7879 / 远端 17879）；切换时必须重连
    private static bool AgentServerRunning;     // 直连模式下用 /metrics 的 server.running 当存活判据（没有 ssh 快照）
    private static CancellationTokenSource? agentLogCts;
    //   三种情况：直连（面板，公网地址+令牌）> 远端（SSH 隧道本地端）> 本机（agent 只绑回环 7879）
    private static string AgentBase => DirectReady
        ? "http://" + DirectHost + ":" + DirectPort
        : UseRemote ? "http://127.0.0.1:" + AgentLocalPort : "http://127.0.0.1:" + AgentRemotePort;

    /// <summary>探测 agent 是否可用（2 秒超时；不可用不抛异常）。</summary>
    private static async Task<bool> ProbeAgentAsync(string baseUrl)
    {
        try
        {
            using var http = NewLocalHttp(TimeSpan.FromSeconds(2));
            string s = await http.GetStringAsync(baseUrl + "/ping");
            bool ok = s.Contains("\"ok\":true");
            return ok;
        }
        catch (Exception ex) { AgentProbeErr = ex.GetType().Name + ": " + ex.Message; return false; }
    }

    /// <summary>刷新连接设置页的 agent 状态灯（UI 线程调用）。</summary>
    private static void RefreshAgentStatus()
    {
        if (AgentStatusText is null) { return; }
        if (!HasSsh)
        {
            AgentStatusText.Text = "云端 agent：本机模式不需要（本机直接读日志/进程）";
            AgentStatusText.Foreground = GetBrush("#9BA3AE");
            return;
        }
        if (AgentAlive)
        {
            long fresh = AgentMetricsAtMs > 0 ? Environment.TickCount64 - AgentMetricsAtMs : -1;
            AgentStatusText.Text = "云端 agent：已连接 ✓" + (fresh >= 0 ? $"（指标 {fresh / 1000.0:F1} 秒前更新）" : "") + " · 日志走推送、指标 2 秒级";
            AgentStatusText.Foreground = GetBrush("#32D74B");
        }
        else
        {
            AgentStatusText.Text = "云端 agent：未安装 / 未运行 ✗ " + AgentProbeErr + " —— 点右边按钮推送安装（日志与指标自动回落到 SSH 路径）";
            AgentStatusText.Foreground = GetBrush("#FEBC2E");
        }
    }

    /// <summary>
    /// 把随管理器分发的 agent（agent/UTSLDaemon.exe，自包含单文件）推到远端并建计划任务常驻。
    /// 推的是【自包含】版：实测远端只有 .NET 9、没有 .NET 10，框架依赖版起不来。
    /// </summary>
    private async Task InstallAgentAsync(TextBlock status)
    {
        // 安装必须经 SSH 推一次（把 exe 送上去）；装好之后可以切到「直连模式」不用 SSH。
        if (!HasSsh) { status.Text = "云端 agent：安装需要先填好 SSH 主机与密钥（推一次即可）；装好后可切到直连模式"; return; }
        string exe = Path.Combine(AppContext.BaseDirectory, "agent", "UTSLDaemon.exe");
        if (!File.Exists(exe)) { status.Text = "云端 agent：找不到安装包 " + exe + " —— 先执行 scripts/publish-agent.ps1"; return; }
        // ★ 顺序很重要：运行中的 UTSLDaemon.exe 在 Windows 上被锁，直接覆盖会 scp: dest open ... Failure。
        //   先停旧进程（并结束计划任务），再推，最后重建任务 —— 实测验证过的顺序。
        status.Text = "云端 agent：正在停止旧进程（否则 exe 被占用，覆盖会失败）…";
        string stopped = await Task.Run(() => SshRun(
            "$ErrorActionPreference='SilentlyContinue'; " +
            "schtasks /End /TN UTSLAgent 2>&1 | Out-Null; " +
            "Get-Process UTSLDaemon -ErrorAction SilentlyContinue | Stop-Process -Force; " +
            "Start-Sleep -Seconds 2; " +
            "'alive=' + ((Get-Process UTSLDaemon -ErrorAction SilentlyContinue) | Measure-Object).Count", 60000));
        status.Text = "云端 agent：旧进程已停（" + stopped.Trim() + "），正在上传…";
        double mb = new FileInfo(exe).Length / 1048576.0;
        status.Text = $"云端 agent：正在上传 {mb:F1} MB …";
        bool ok = await Task.Run(() =>
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("scp") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(SshKey);
                psi.ArgumentList.Add("-P"); psi.ArgumentList.Add(SshPort.ToString());
                psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("BatchMode=yes");
                psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("StrictHostKeyChecking=accept-new");
                psi.ArgumentList.Add(exe);
                psi.ArgumentList.Add(SshUser + "@" + SshHost + ":C:/utsl-agent/UTSLDaemon.exe");
                using var proc = System.Diagnostics.Process.Start(psi)!;
                string err = proc.StandardError.ReadToEnd();
                proc.WaitForExit(240000);
                if (proc.ExitCode != 0) { lastAgentScpErr = err.Trim(); return false; }
                return true;
            }
            catch (Exception ex) { lastAgentScpErr = ex.Message; return false; }
        });
        if (!ok) { status.Text = "云端 agent：上传失败 ✗ " + lastAgentScpErr + "（若提示 dest open … Failure，说明 exe 仍被占用）"; return; }

        status.Text = "云端 agent：正在建计划任务（先停旧的）…";
        // ★ 直连模式：把绑定与令牌一起写进计划任务参数（否则对外的 agent 起不来 —— 无令牌绑外网会被拒绝）。
        //   注意：令牌会出现在该机器的计划任务命令行里（同机管理员可见），这是直连模式的固有代价。
        string directArgs = DirectReady ? " --bind 0.0.0.0 --token " + DirectToken : "";
        string res = await Task.Run(() => SshRun(
            "$ErrorActionPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
            "New-Item -ItemType Directory -Force -Path C:\\utsl-agent | Out-Null; " +
            "schtasks /End /TN UTSLAgent 2>&1 | Out-Null; " +
            "Get-Process UTSLDaemon -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Seconds 2; " +
            "$tr='\"C:\\utsl-agent\\UTSLDaemon.exe\" --root " + RemoteDir + " --port 7879" + directArgs + "'; " +
            "schtasks /Create /TN UTSLAgent /TR $tr /SC ONSTART /RU SYSTEM /RL HIGHEST /F | Out-Null; " +
            "schtasks /Run /TN UTSLAgent | Out-Null; Start-Sleep -Seconds 3; " +
            "'proc=' + ((Get-Process UTSLDaemon -ErrorAction SilentlyContinue) | Measure-Object).Count", 120000));
        await TickAgentAsync();                       // 立刻重探，别等 20 秒定时器
        status.Text = "云端 agent：安装完成 · " + res.Trim().Replace("\n", " ") + (AgentAlive ? " · 已连接 ✓" : " · 但探测未通过 ✗");
    }

    /// <summary>每 2 秒拉一次 agent /metrics（远端没装 MetricsChannel 插件时，这是唯一的实时来源）。</summary>
    private async Task AgentMetricsLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                string s = await AgentHttp.GetStringAsync(AgentBaseUsed + "/metrics", ct);
                using var doc = JsonDocument.Parse(s);
                var srv = doc.RootElement.GetProperty("server");
                // ★ 直连（无 ssh）模式下没有快照可用，服务端存活改看 agent 的 /metrics（server.running）
                AgentServerRunning = srv.TryGetProperty("running", out var rn) && rn.GetBoolean();
                AgentCpuPct = srv.TryGetProperty("cpuSingleCorePct", out var c) ? c.GetDouble() : 0;
                if (doc.RootElement.TryGetProperty("cores", out var cr) && cr.ValueKind == JsonValueKind.Number) { AgentCores = cr.GetInt32(); }
                AgentPrivateMB = srv.TryGetProperty("privateMB", out var m) ? m.GetDouble() : 0;
                var net = doc.RootElement.GetProperty("net");
                AgentSentBytes = net.TryGetProperty("sentBytes", out var sb) ? sb.GetInt64() : -1;
                AgentRecvBytes = net.TryGetProperty("recvBytes", out var rb) ? rb.GetInt64() : -1;
                AgentMetricsAtMs = Environment.TickCount64;
            }
            catch (OperationCanceledException) { break; }
            catch { }
            try { await Task.Delay(2000, ct); } catch { break; }
        }
    }

    /// <summary>
    /// 订阅 agent 的 /stream/stdout：服务端**进程自己的标准输出**（agent 托管进程时才有）。
    /// 与日志流互补 —— /help 这类只写控制台、不写日志的输出只有这条路能看到。
    /// </summary>
    private async Task AgentStdoutLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var http = NewLocalHttp(Timeout.InfiniteTimeSpan);
                using var resp = await http.GetAsync(AgentBaseUsed + "/stream/stdout", HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                while (!ct.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(ct);
                    if (line is null) { break; }
                    if (line.StartsWith("data: "))
                    {
                        string text = line.Substring(6);
                        bool err = text.StartsWith("[err]");
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (!ConsoleView.IsVisible) { return; }
                            Term(text, err ? "#FF7B72" : "#8B949E");
                        });
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch { }
            try { await Task.Delay(2000, ct); } catch { break; }
        }
    }

    /// <summary>订阅 agent 的 SSE 日志流，把行投到控制台；断开则退避重连。AgentAlive=false 时不再重连。</summary>
    private async Task AgentLogLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var http = NewLocalHttp(Timeout.InfiniteTimeSpan);
                using var resp = await http.GetAsync(AgentBaseUsed + "/stream/log", HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                while (!ct.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(ct);
                    if (line is null) { break; }                      // 服务端关流
                    if (line.StartsWith("data: "))
                    {
                        string text = line.Substring(6);
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (!ConsoleView.IsVisible) { return; }   // 不在控制台页就不刷新 UI
                            Term(text, PickColor(text));
                        });
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch { }
            if (!AgentAlive) { break; }
            try { await Task.Delay(2000, ct); } catch { break; }
        }
    }

    /// <summary>探活 + 起停日志流；由隧道定时器调用。状态变化时在控制台留一行日志（不刷屏）。</summary>
    private async Task TickAgentAsync()
    {
        if (worldDlBusy) { return; }      // 下载世界文件时不要重新建流/探活，别抢隧道
        string want = AgentBase;
        bool ok = await ProbeAgentAsync(want);
        // ★ 不仅要看"活不活"，还要看"连的是不是同一个 base"：
        //   启动时配置可能还没加载完（UseRemote=false → 探到本机 agent），之后切到远端 →
        //   若只看 AgentAlive 就会一直 true == true 直接 return，流永远留在本机（实测踩到）。
        if (ok == AgentAlive && want == AgentBaseUsed) { Dispatcher.UIThread.Post(RefreshAgentStatus); return; }
        AgentAlive = ok;
        AgentBaseUsed = want;
        AgentClient.Alive = ok; AgentClient.Base = want;   // ★ 同步给文件通道（P1：上传/下载走 agent，带真实进度）
        AgentClient.Token = DirectReady ? DirectToken : ""; // 直连模式带上令牌
        AgentClient.ResetRoot();                            // base 变了 → agent 根缓存失效
        if (ok) { AgentClient.RefreshRoot(); }               // 在【后台】抓 /ping 的 root（RemoteRoot 现在是纯字段读，不再阻塞调用线程）
        AgentHttp.DefaultRequestHeaders.Authorization = AgentClient.Token.Length > 0
            ? new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AgentClient.Token) : null;
        if (ok)
        {
            Dispatcher.UIThread.Post(() => { ERR("✓ 已连接 UTSLDaemon（云端 agent）—— 控制台日志改为推送，实测延迟约 150 ms；不再轮询日志文件"); RefreshAgentStatus(); });
            agentLogCts?.Cancel();
            agentLogCts = new CancellationTokenSource();
            lastAgentSent = -1; lastAgentRecv = -1; AgentMetricsAtMs = 0;    // ★ 新连接：速率基准重置
            _ = Task.Run(() => AgentLogLoopAsync(agentLogCts.Token));
            _ = Task.Run(() => AgentStdoutLoopAsync(agentLogCts.Token));   // P2：服务端真正的 stdout（含 /help 这类只写控制台的输出）
            _ = Task.Run(() => AgentMetricsLoopAsync(agentLogCts.Token));
        }
        else
        {
            Dispatcher.UIThread.Post(() => { ERR("agent 不可用，日志回落到 ssh 轮询路径"); RefreshAgentStatus(); });
            agentLogCts?.Cancel();
            agentLogCts = null;
        }
    }
    /// <summary>同时保留的隧道上限（保活让"来回切换"变快；超了按 LRU 淘汰最久没用的那条）。</summary>
    private const int MaxLiveTunnels = 4;

    /// <summary>该档建隧道用到的 ssh 参数指纹（变了就必须重建）。</summary>
    private static string TunnelSig(ServerProfile p)
        => string.Join("|", p.SshHost, p.SshPort, p.SshUser, p.SshKey, p.RemoteDir, p.RemoteRestPort, p.Port, p.AgentPort);

    private static bool ProcAlive(int pid)
    {
        if (pid <= 0) { return false; }
        try { using var pr = System.Diagnostics.Process.GetProcessById(pid); return !pr.HasExited; } catch { return false; }
    }

    /// <summary>隧道保活上限：按"最近使用"淘汰，正在用的那条永不淘汰。只杀本机 ssh 客户端。</summary>
    private static void TrimTunnels(ServerProfile keep)
    {
        try
        {
            var live = ServersCfg.Servers
                .Where(p => p.TunnelPid > 0 && ProcAlive(p.TunnelPid))
                .OrderBy(p => p.LastUsedMs)
                .ToList();
            int over = live.Count - MaxLiveTunnels;
            foreach (var p in live)
            {
                if (over <= 0) { break; }
                if (ReferenceEquals(p, keep)) { continue; }
                KillTunnel(p);
                over--;
            }
        }
        catch { }
    }

    /// <summary>
    /// 确保某档的 SSH 隧道在跑（默认=当前激活档）。
    /// ★ 多档后这里必须显式带档、并且串行：切档时隧道定时器、合并快照(RefreshSnapshot) 都会调进来，
    ///   而原来只有一个静态 TunnelPid + 隐式读 ConnPort —— 实测出现"切换中有人按旧档端口起了另一条隧道"，
    ///   结果新档 REST 端口没人监听、世界列表空。现在：锁 + 冷却 + 切档期间不插队 + 端口取自传入的那档。
    /// </summary>
    private static void EnsureTunnel(ServerProfile? only = null)
    {
        ServerProfile p = only ?? ActiveSrv;
        if (only is null && switching) { return; }      // 切档进行中：只允许切档自己（显式传档）来建隧道
        if (!p.HasSsh) { return; }
        lock (TunnelGate)
        {
            try
            {
                var t = new System.Net.Sockets.TcpClient();
                bool alive = false;
                try { alive = t.ConnectAsync("127.0.0.1", p.Port).Wait(800); } catch { alive = false; }
                t.Close();
                if (ReferenceEquals(p, ActiveSrv)) { TunnelAlive = alive; }
                string sig = TunnelSig(p);
                // ★ 参数被改过（换主机/密钥/端口）→ 旧隧道连的是老目标，必须重建，不能复用
                if (alive && p.TunnelKey.Length > 0 && p.TunnelKey != sig)
                {
                    KillTunnel(p);
                    alive = false;
                }
                // ★ 端口在听但不知道是谁起的（管理器重启后旧 ssh 仍在）→ 认领它，否则切档时杀不掉、会一直占着端口
                if (alive) { if (p.TunnelPid <= 0) { p.TunnelPid = FindTunnelPidByPort(p.Port); } p.TunnelKey = sig; return; }
                if (p.TunnelPid > 0)
                {
                    try { System.Diagnostics.Process.GetProcessById(p.TunnelPid); return; } catch { p.TunnelPid = 0; }
                }
                // 冷却：ssh 从"进程起来"到"端口在听"有几百毫秒，这期间每 20 秒的定时器 + 快照刷新会各起一条 → 堆积
                long now = Environment.TickCount64;
                if (p.LastSpawnMs > 0 && now - p.LastSpawnMs < 10000) { return; }
                p.LastSpawnMs = now;
                var psi = new System.Diagnostics.ProcessStartInfo("ssh")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    Arguments = "-i \"" + p.SshKey + "\" -p " + p.SshPort + " -o BatchMode=yes -o ExitOnForwardFailure=yes -o ServerAliveInterval=30 -N " +
                                // ★ 一个 ssh 承载两条转发：REST + agent
                                "-L " + p.Port + ":127.0.0.1:" + p.RemoteRestPort + " -L " + p.AgentPort + ":127.0.0.1:" + AgentRemotePort + " " + p.SshUser + "@" + p.SshHost
                };
                var proc = System.Diagnostics.Process.Start(psi);
                p.TunnelPid = proc?.Id ?? 0;
                p.TunnelKey = sig;
            }
            catch { }
        }
    }

    /// <summary>等本地端口进入 listening（切档后建隧道用）。超时返回 false。</summary>
    private static async Task<bool> WaitPortAsync(int port, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                using var c = new System.Net.Sockets.TcpClient();
                using var cts = new CancellationTokenSource(400);
                await c.ConnectAsync("127.0.0.1", port, cts.Token);
                return true;
            }
            catch { }
            await Task.Delay(120);
        }
        return false;
    }

    private static readonly object TunnelGate = new();   // 隧道建/杀串行化（多线程都会调进来）

    /// <summary>找出"正在监听某个本地端口"的 ssh 进程（隧道认领 / 切档清理用）。找不到返回 0。</summary>
    private static int FindTunnelPidByPort(int port)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("netstat", "-ano -p tcp")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) { return 0; }
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            foreach (string line in outp.Split('\n'))
            {
                string[] t = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (t.Length < 5 || t[3] != "LISTENING") { continue; }
                if (!t[1].EndsWith(":" + port, StringComparison.Ordinal)) { continue; }
                if (!int.TryParse(t[4], out int pid)) { continue; }
                try
                {
                    using var pr = System.Diagnostics.Process.GetProcessById(pid);
                    if (pr.ProcessName.StartsWith("ssh", StringComparison.OrdinalIgnoreCase)) { return pid; }
                }
                catch { }
            }
        }
        catch { }
        return 0;
    }

    /// <summary>把 PowerShell 脚本编码后经 ssh 执行（远端命令一律走 EncodedCommand，避免引号/转义问题）。</summary>
    /// <summary>
    /// 常驻远端命令通道：**一条** ssh 上跑 PowerShell 循环，stdin 收 base64 命令、按 ##B/##E 帧回包。
    /// 省掉"每次请求重新握手 + 远端重启 PowerShell"（实测单次 3~5 秒 → 通道内 ~0.1 秒）。
    /// 三重保险：① StandardInput 显式不带 BOM（否则远端取 id 会带上 BOM → 永远收不到回包，这就是上一版失败的原因）；
    /// ② 建连后自检，6 秒内必须收到 ##E 帧；③ 任何异常/超时立即标记不可用并永久回退一次性 ssh。
    /// 生命周期：管理器退出时 stdin 关闭 → 远端 ReadLine 返回 null → 循环退出 → 本机 ssh 退出（自清理，不留孤儿）。
    // ★ 实测：单次 ssh 握手 ~1.5 秒；但并发堆到 40 个时单次会变成 14 秒（互相拖慢）。
    //   用信号量限制并发（最多 2 个）。
    //   ★ 改动：原来「最多等 15 秒就放行」——等不到许可照样起 ssh，等于并发上限名存实亡
    //     （注释里自己描述的劣化场景就是这么触发的）。现在改为：等不到就【放弃本次调用】，
    //     返回空串（与"失败返回空串"的既有约定一致），绝不无界地起 ssh。
    private static readonly System.Threading.SemaphoreSlim SshGate = new(2, 2);

    /// <summary>最近一次 ssh 失败/超时的原因（排查用；空表示正常）。</summary>
    internal static string LastSshError = "";

    private static string SshRun(string psScript, int timeoutMs = 30000)
    {
        LastSshError = "";        // 每次调用重新计健康度（状态灯据此显示）
        if (!SshGate.Wait(60000))
        {
            LastSshError = "等待 ssh 通道超时（60 秒）—— 远端可能异常繁忙或卡住";
            return "";
        }
        try
        {
            return SshRunInner(psScript, timeoutMs);
        }
        finally
        {
            SshGate.Release();
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
        var p = System.Diagnostics.Process.Start(psi) ?? throw new Exception("无法启动 ssh");
        try
        {
            // ★★ 这里原来有两处致命问题（实测复核确认）：
            //   ① 先 ReadToEnd() 再 WaitForExit(timeoutMs)：ReadToEnd 会一直阻塞到 ssh 退出才返回，
            //      远端挂住时那一行永不返回，【超时保护根本没机会执行】，而后台线程与 SshGate 许可被永久占用
            //      （只有 2 个许可，两个卡住 = 远端模式整片停摆，且没有任何报错）。
            //   ② stdout/stderr 串行读：管道缓冲约 4KB，远端往 stderr 写超一屏（CLIXML 进度流）就会
            //      ssh 阻塞在写、我们阻塞在读 → 经典互等死锁。
            //   现在：两个流【并发】读，超时用 Task.WhenAny 判，超时即 Kill 并返回空串（失败约定）。
            Task<string> so = p.StandardOutput.ReadToEndAsync();
            Task<string> se = p.StandardError.ReadToEndAsync();
            Task both = Task.WhenAll(so, se);
            if (Task.WaitAny(both, Task.Delay(timeoutMs)) != 0)
            {
                try { p.Kill(true); } catch { }
                try { both.Wait(3000); } catch { }        // 杀掉后等流收尾，避免句柄/线程悬空
                LastSshError = $"ssh 超时（{timeoutMs} ms）—— 已终止该 ssh 进程";
                return "";
            }
            string o = so.Result;
            string err = se.Result;
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
            if (clean.Length == 0) { LastSshError = "ssh 返回空（远端脚本失败或连接被拒）"; }
            return clean;
        }
        finally { p.Dispose(); }
    }

    /// <summary>远端启动脚本：先清旧进程，再用 WMI 创建「控制台启动器」（脱离 SSH 会话，断开不会被回收）。</summary>
    private static string RemoteStartScript()
    {
        // ★ 启动参数按档（空 = 不带参数）。PS 单引号里要把 ' 转义成 ''。
        string args = (ActiveSrv.StartArgs ?? "").Trim().Replace("'", "''");
        string cmd = RemoteDotnet + " " + RemoteLauncher + " " + RemoteDir + " " + RemoteDir + "\\UnifierTSL.exe"
                   + (args.Length > 0 ? " " + args : "");
        // ★ 只停【本档部署目录下】的进程：原来按进程名一刀切，远端同机两份部署会互相残杀（和本机侧同一个坑）。
        string mine = RemoteMineProcesses();
        return "$ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
               mine + " | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }; " +
               "Start-Sleep -Seconds 2; " +
               "$c='" + cmd + "'; " +
               "$r=Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{CommandLine=$c}; " +
               "'wmi rc=' + $r.ReturnValue + ' pid=' + $r.ProcessId";
    }

    /// <summary>远端「属于本档部署目录」的服务端进程（CIM 查询片段；ConsoleClient 也算）。</summary>
    private static string RemoteMineProcesses()
    {
        string d = RemoteDir.Replace("'", "''");
        return "Get-CimInstance Win32_Process -Filter \"Name='UnifierTSL.exe' OR Name='UnifierTSL.ConsoleClient.exe'\" | " +
               "Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith('" + d + "\\', [StringComparison]::OrdinalIgnoreCase) }";
    }

    private static string RemoteStopScript()
    {
        string mine = RemoteMineProcesses();
        return "$ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
               mine + " | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }; " +
               "Start-Sleep -Seconds 1; " +
               "'remaining=' + (@(" + mine + ")).Count";
    }

    private long lastRemoteLogMs;
    private long lastLogErrMs;
    private string lastLogErr = "";
    private string lastRemoteLine = "";

    /// <summary>远端日志：每 12 秒（见 RefreshRemoteLog 的节流）取最新日志尾部，按上一行去重后只追加新行。
    /// ★ agent 在线时这条路径根本不跑（日志走 SSE 推送）；没有 agent 时才回落到这里。</summary>
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
            // 自检修复：读日志失败时别每次轮询都刷一条（SSH 配错会刷屏），同一错误最多每分钟提示一次
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
        // ★ 原来是 if (lines.Count == 0)："" .Split 永远返回 1 个元素 → 这个护栏【永不成立】。
        //   真正该拦的是 SshRun 失败返回空串（远端脚本失败/超时），那种情况要能看出来。
        if (outp.Trim().Length == 0) { return; }
        string header = lines[0].StartsWith("###") ? lines[0][3..] : "";
        if (header.Length > 0 && header != lastLogFile)
        {
            lastLogFile = "remote:" + header;
            lastRemoteLine = "";
            Term("——— 远端日志 " + header + " ———", "#848D9A");
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

    private void OnClearTerm(object? sender, RoutedEventArgs e)
    {
        TermFor(ActiveSrv.Id).Clear();
        TermViewFor(ActiveSrv.Id).Clear();
        termDropped = 0;
        termPausedNew = 0;
        UpdateTermTitle();
    }

    private void RefreshLog()
    {
        if (AgentAlive) { return; }                     // ★ agent 在线：日志由 SSE 推送，不再轮询文件
        // ★ 刚切完档的 5 秒里不要用 SSH 去捞远端日志尾巴：agent 通常 ~200ms 就接上 SSE 了，
        //   那一次 ssh 要 1.5~8 秒（链路 RTT 1.7 秒），唯一效果是把终端刷 40 行 + 滚动跳一下。
        if (UseRemote && Environment.TickCount64 - switchDoneMs < 5000) { return; }
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
                Term("——— 日志文件 " + f.Name + " ———", "#848D9A");
            }
            using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < logPos) { logPos = 0; }
            bool firstFill = logPos == 0;      // ★ 首屏（刚换台/刚启动）：历史行只补最后 120 行
            // ★ 首次（logPos==0）不要从文件头整读：几十 MB 的日志会逐行读 + 每行一次 Term
            //   （集合增删 + ScrollIntoView 的 Dispatcher.Post 排队）→ 启动瞬间几万次 UI 操作 = "一打开就卡"。
            //   只取尾部 64KB 作为首屏上下文，并把 logPos 对齐到读起点。
            if (logPos == 0 && fs.Length > 64 * 1024)
            {
                fs.Seek(-64 * 1024, SeekOrigin.End);
                using (var skip = new StreamReader(fs, System.Text.Encoding.UTF8, true, 4096, leaveOpen: true)) { skip.ReadLine(); }   // 丢掉被截断的半行
                logPos = fs.Position;
            }
            fs.Seek(logPos, SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            string? line;
            List<string>? tail = firstFill ? new List<string>(256) : null;
            while ((line = sr.ReadLine()) is not null)
            {
                if (tail is not null) { tail.Add(line); continue; }   // 首屏先攒着，别逐行刷 UI
                Term(line, PickColor(line));
            }
            if (tail is not null && tail.Count > 0)
            {
                // ★ 实测：换台后第一次读日志会带来上千行历史，逐行 Term（集合改动 + 滚动 Post）最长冻 UI 363ms
                int from = Math.Max(0, tail.Count - 60);
                for (int i = from; i < tail.Count; i++) { Term(tail[i], PickColor(tail[i])); }
            }
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
        // ★ 指定了 @世界 时若正文还以 > 开头，把 > 剥掉：> 是"控制台口径"标记，
        //   与 @世界 同时出现会拼出 "/>/help" 这种垃圾（@世界 优先，语义按世界走）
        if (m.Success && text.StartsWith('>')) { text = text[1..].Trim(); }
        if (text.Length == 0) { CmdBox.Text = ""; return; }
        // ★ 输入以 ">" 开头 = 走【服务端控制台口径】（scope=console）：以服务端控制台身份执行，
        //   输出会写进服务端日志 → 显现在上面的日志流里。这是看到 /help、老式指令输出的唯一办法。
        //   不加 ">" 时语义与从前完全一致（@世界 / 不写前缀=全部世界）。
        bool consoleScope = false;
        if (!m.Success && raw.StartsWith('>'))
        {
            consoleScope = true;
            text = raw[1..].Trim();
            if (text.Length == 0) { return; }
        }

        // 插件命令（P1-6）：以 ! 开头 → 交给插件宿主，不发给服务端
        if (text.StartsWith('!'))
        {
            TryPluginCommand(text, world.Length == 0 ? null : world);
            CmdBox.Text = "";
            return;
        }

        // ★ 回显在发请求【之前】打：否则指令输出（日志流）可能先到、回显排到下面；请求失败时也会没回显。
        Term("> " + (consoleScope ? "> " : "") + (world.Length == 0 ? "" : "@" + world + " ") + text, "#79C0FF");
        // ★ 在 await 之前就清空输入框：原来清空在最后，快速双击/长按 Enter 会在清空前再次进入 → 同一条指令发两次
        CmdBox.Text = "";
        try
        {
            string url = Rest + "/world/command?token=" + Uri.EscapeDataString(Token) + "&world=" + Uri.EscapeDataString(world) + "&cmd=" + Uri.EscapeDataString(text) + "&scope=" + (consoleScope ? "console" : "world");
            string res = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(res);
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
        // （输入框已在发送前清空，见上）
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
    // ★ 指标历史按服务器分开存：切档时把当前这组存回字典、把目标档那组换上来（图表会立刻显示那台自己的历史）
    private sealed class MetricHistory
    {
        public List<double> Cpu = [], Mem = [], Up = [], Down = [];
        public List<DateTime> Times = [];
    }
    private readonly Dictionary<string, MetricHistory> histByServer = [];
    private List<double> cpuHist = [], memHist = [], upHist = [], downHist = [];
    private List<DateTime> timeHist = [];
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

    /// <summary>四路序列必须**每拍各补一次**样本：值未知时沿用上一次（首次为 0）。
    /// 否则某一路（如进程消失时的内存、没装 metrics 插件时的内存）会漏拍 → 长度不一致 →
    /// 三张图的填充宽度就不齐（这是之前"看起来没对齐"的另一半原因）。</summary>
    /// <summary>CPU 显示格式：`43.3% / 200%`（分子=占单核，分母=这台机器满槽）。
    /// 只写 43.3% 会让人以为"才 43%"，只写 100% 又会让人以为"整机吃满"——两个都是误读。</summary>
    private static string FmtCpu(double pct)
    {
        int cores = UseRemote && AgentCores > 0 ? AgentCores : Environment.ProcessorCount;
        return cores > 0 ? $"{pct:F1}% / {cores * 100.0:F0}%" : $"{pct:F1}%";
    }

    /// <summary>窗口失焦时把三个"红绿灯"调淡（macOS 行为）；不做隐藏，只降透明度，位置不跳。</summary>
    private void SetTrafficLightsActive(bool active)
    {
        double op = active ? 1.0 : 0.45;
        try { Program.Log($"trafficlights active={active} opacity={op}"); } catch { }
        foreach (var b in new Avalonia.Controls.Border?[] { BtnClose, BtnMin, BtnMax })
        {
            if (b is not null) { b.Opacity = op; }
        }
    }
    private static void PushCarry(List<double> list, double? v)
        => Push(list, v ?? (list.Count > 0 ? list[^1] : 0));
    private void SampleMetrics()
    {
        var now = DateTime.Now;
        double secs = Math.Max(0.05, (now - lastSample).TotalSeconds);

        // 口径标注（只在第一帧写一次）
        if (samplesSeen == 0)
        {
            try
            {
                bool live = UseRemote && (AgentAlive || MetricsAtMs > 0);
                // ★ 远端是 2 核机器：cpuSingleCorePct 是"单核 %"，标题必须写清楚，否则会被误读成整机占用
                if (CpuTitle is not null)
            {
                // ★ 写清"几个核"：单核 % 满格是 100%，整机满槽是 N×100%——不写核数就会把 100% 误读成"整机吃满"
                int cores = UseRemote && AgentCores > 0 ? AgentCores : Environment.ProcessorCount;
                CpuTitle.Text = !UseRemote ? $"CPU 使用量（本机 · {cores} 核）" : live ? $"CPU（远端 · {cores} 核 · 实时 2s）" : $"CPU（远端 · {cores} 核）";
            }
                if (MemTitle is not null) { MemTitle.Text = !UseRemote ? "内存使用量（本机 · 私有）" : live ? "内存（远端 · 实时 2s）" : "内存（远端 · 私有）"; }
                if (NetTitle is not null) { NetTitle.Text = !UseRemote ? "网络流量（本机全部网卡）" : live ? "网络（远端网卡 · 实时 2s）" : "网络（远端网卡）"; }
            }
            catch { }
        }

        double cpuVal = 0, memVal = 0, up = 0, down = 0;

        if (UseRemote)
        {
            // ★ 远端模式：数据来自「合并快照」（每 45 秒一次，不额外发 ssh）—— 监控的是被管的那台服务器。
            //   旧实现用 Process.GetProcessesByName 读的是【本机】进程、网络是本机全部网卡合计 → 测错对象。
            bool pluginFresh = MetricsAtMs > 0 && Environment.TickCount64 - MetricsAtMs < 8000;
            bool agentFresh = AgentAlive && AgentMetricsAtMs > 0 && Environment.TickCount64 - AgentMetricsAtMs < 8000;
            if (agentFresh)
            {
                // ★ 首选：agent（2 秒级；不要求服务端装 MetricsChannel 插件。
                //   原来这里只有"插件"和"45 秒快照"两条路，而这台远端没装插件 → 三个图表几乎不动，就是这个原因）
                cpuVal = AgentCpuPct;
                memVal = AgentPrivateMB / 1024.0;
                if (lastAgentSent >= 0 && AgentSentBytes >= 0 && secs > 0)
                {
                    up = Math.Max(0, (AgentSentBytes - lastAgentSent) / 1024.0 / secs);
                    down = Math.Max(0, (AgentRecvBytes - lastAgentRecv) / 1024.0 / secs);
                }
                lastAgentSent = AgentSentBytes; lastAgentRecv = AgentRecvBytes;
                PushCarry(cpuHist, cpuVal); PushCarry(memHist, memVal); PushCarry(upHist, up); PushCarry(downHist, down);
                CpuNow.Text = FmtCpu(cpuVal);
                MemNow.Text = $"{memVal:F2} GB";
                NetNow.Text = up >= 1024 || down >= 1024
                    ? $"↑ {up / 1024.0:F2}  ↓ {down / 1024.0:F2} MB/s"
                    : $"↑ {up:F2}  ↓ {down:F2} KB/s";
            }
            else if (pluginFresh)
            {
                // ★ 首选：服务端 /metrics 插件（2 秒级、实测单次 ~0.1 秒），监控的就是服务器本进程
                cpuVal = MetricsCpuPct;
                memVal = MetricsPrivateMB / 1024.0;
                up = MetricsUpKbps;
                down = MetricsDownKbps;
                PushCarry(cpuHist, cpuVal);
                PushCarry(memHist, memVal);
                PushCarry(upHist, up); PushCarry(downHist, down);
                CpuNow.Text = FmtCpu(cpuVal);
                MemNow.Text = $"{memVal:F2} GB";
                NetNow.Text = up >= 1024 || down >= 1024
                    ? $"↑ {up / 1024.0:F2}  ↓ {down / 1024.0:F2} MB/s"
                    : $"↑ {up:F2}  ↓ {down:F2} KB/s";
            }
            else if (SnapHasRate)
            {
                // 回退：SSH 合并快照（没装 MetricsChannel 插件的服务器走这条，45 秒粒度）
                if (SnapPrevAtMs == snapPushedAt) { return; }
                snapPushedAt = SnapPrevAtMs;
                cpuVal = SnapCpuPct;
                memVal = SnapMemBytes / 1073741824.0;
                up = SnapUpKbps;
                down = SnapDownKbps;
                PushCarry(cpuHist, cpuVal);
                CpuNow.Text = FmtCpu(cpuVal);
                PushCarry(memHist, SnapMemBytes > 0 ? memVal : null);
                MemNow.Text = SnapMemBytes > 0 ? $"{memVal:F2} GB" : "—";
                PushCarry(upHist, up); PushCarry(downHist, down);
                NetNow.Text = up >= 1024 || down >= 1024
                    ? $"↑ {up / 1024.0:F2}  ↓ {down / 1024.0:F2} MB/s"
                    : $"↑ {up:F2}  ↓ {down:F2} KB/s";
            }
            else
            {
                if (CpuNow is not null) { CpuNow.Text = "—"; }
                if (MemNow is not null) { MemNow.Text = "—"; }
                if (NetNow is not null) { NetNow.Text = "—"; }
                lastSample = now;
                return;
            }
        }
        else
        {
            // ★ 按"当前激活档的部署目录"找进程（本机挂两份部署时，不能再取第一个同名进程）
            var srv = LocalServerProc(ActiveSrv);   // ★ 排除僵尸条目，否则监控读到 0
            if (srv is not null)
            {
                try
                {
                    srv.Refresh();
                    // ★ 首帧只记基线：否则第一帧 = 该进程开机至今的总 CPU 时间 ÷ 秒数 → 假尖峰（曾把 Y 轴顶到 400%）
                    if (samplesSeen > 0)
                    {
                        double cpu = (srv.TotalProcessorTime - lastCpuTime).TotalMilliseconds / (secs * 1000) * 100.0;
                        cpuVal = Math.Max(0, Math.Min(Environment.ProcessorCount * 100.0, cpu));   // 上限=核数×100（本机核数）
                        PushCarry(cpuHist, cpuVal);
                    }
                    lastCpuTime = srv.TotalProcessorTime;
                    CpuNow.Text = FmtCpu(cpuVal);
                    memVal = srv.PrivateMemorySize64 / 1073741824.0;
                    PushCarry(memHist, memVal);
                    MemNow.Text = $"{memVal:F2} GB";
                }
                catch { }
            }
            else
            {
                // 进程没了就显示 —（旧实现会一直留着上一次的读数，看着像还在跑）
                CpuNow.Text = "—"; MemNow.Text = "—";
                if (samplesSeen > 0) { PushCarry(cpuHist, 0); PushCarry(memHist, null); PushCarry(upHist, null); PushCarry(downHist, null); }
            }

            // ★ 网卡枚举实测 75~174ms/次（A 子代理数据），是 UI 线程每 2 秒那 100~225ms 的大头。
            //   累计字节本来就要靠相邻采样差分，所以给它几秒缓存；速率按【真实经过时间】算（见下面 netSecs），
            //   因此缓存不会让速率被高估。
            long sent = 0, recv = 0;
            long nowMs2 = Environment.TickCount64;
            // TTL 取 5 秒：网卡枚举 75~174ms/次，5 秒一次即每 5 拍只做 1 次（省约 60%）；
            // 速率用"两次采样的真实间隔"做分母（见 netSecs），所以 TTL 不会让速率失真，
            // 代价只是"网络曲线的时间分辨率变粗"（本机模式才走这条；远端模式用的是 agent 的 /metrics）。
            if (netCacheAtMs == 0 || nowMs2 - netCacheAtMs > 5000)
            {
                long s0 = 0, r0 = 0;
                try
                {
                    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (ni.OperationalStatus != OperationalStatus.Up) { continue; }
                        if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) { continue; }   // 回环不计（避免隧道流量重复计）
                        var st = ni.GetIPStatistics();
                        s0 += st.BytesSent; r0 += st.BytesReceived;
                    }
                }
                catch { }
                netCacheSent = s0; netCacheRecv = r0; netCacheAtMs = nowMs2;
            }
            sent = netCacheSent; recv = netCacheRecv;
            // ★ 首帧只记基线：否则第一帧 = 网卡开机至今的总字节 → 假尖峰（曾出现 18823250 这种天文数字）
            // 速率用【这一次网卡采样与上一次之间的真实时间】做分母（配合上面的 3 秒缓存，避免高估）
            double netSecs = lastBytesAtMs > 0 ? Math.Max(0.5, (nowMs2 - lastBytesAtMs) / 1000.0) : secs;
            lastBytesAtMs = nowMs2;
            if (lastBytesSeen && samplesSeen > 0)
            {
                up = Math.Max(0, (sent - lastBytesSent) / 1024.0 / netSecs);
                down = Math.Max(0, (recv - lastBytesRecv) / 1024.0 / netSecs);
                PushCarry(upHist, up); PushCarry(downHist, down);
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
        if (ConsoleView.IsVisible && IsActive)
        {
            DrawChart(CpuChart, CpuYLabels, CpuXLabels, [cpuHist], ["#0A84FF"], "cpu", timeHist);
            DrawChart(MemChart, MemYLabels, MemXLabels, [memHist], ["#00D2E0"], "mem", timeHist);
            DrawChart(NetChart, NetYLabels, NetXLabels, [upHist, downHist], ["#DB34F2", "#FF9230"], "net", timeHist);
        }
    }

    // ===== 图表几何：**只在这里定义一套映射**，网格线 / 数据 / Y 标签 / X 标签全用它 =====
    //   之前是两套：网格线按 4+gi*(h-8)/4 画，Y 标签却靠 XAML 的 5 行等分网格摆 —— 行模型 vs 连续像素，
    //   永远对不齐；X 标签又是 4 等分列而取样按 1/3 切 → 文字与它所在位置的时间对不上。用户实测看出"没对齐"。
    private const double PlotPadY = 8;        // 绘图区上下留白（像素）：要容得下首/末 Y 标签的一半高度，否则会被裁
    private const int PlotDivisions = 4;      // 纵向分 4 段 → 5 条参考线

    private static double PlotTop(double h) => PlotPadY;
    private static double PlotBottom(double h) => h - PlotPadY;

    /// <summary>值 → 纵向像素（上下翻转：值越大越靠上，并夹进绘图区）。</summary>
    private static double YFor(double v, double scale, double h)
    {
        double top = PlotTop(h), bottom = PlotBottom(h);
        double r = scale <= 0 ? 0 : Math.Clamp(v / scale, 0, 1);
        return bottom - r * (bottom - top);
    }

    /// <summary>样本序号 → 横向像素。**左对齐**：最老的贴左边、新的往右长；缓冲满了以后最新样本正好在右边缘。
    /// （曾经为"三张图填充宽度一致"改成右对齐 —— 结果变成从右往左长，方向反了；
    ///   正确做法是让四路序列每拍都各补一次样本、长度始终一致，见 PushCarry。）</summary>
    private static double XFor(int i, double w)
    {
        double left = 1, right = w - 1;
        double step = (right - left) / (MaxSamples - 1);
        return left + i * step;
    }

    private static double CanvasW(Canvas c) => !double.IsNaN(c.Width) && c.Width > 0 ? c.Width : c.Bounds.Width;
    private static double CanvasH(Canvas c) => !double.IsNaN(c.Height) && c.Height > 0 ? c.Height : c.Bounds.Height;

    /// <summary>Y 轴标签：带单位、短数字（避免 18823250 这种把轴挤爆的值）。</summary>
    private static string FmtAxis(double v, string kind, double scale = 0)
    {
        // ★ 轴刻度小时必须留一位小数：scale=2、5 等分时 F0 会把 1.6% 和 2.0% 都舍成 "2%"，轴上出现两个 2%
        if (kind == "cpu") { return v.ToString(scale > 0 && scale < 10 ? "F1" : "F0") + "%"; }
        if (kind == "mem") { return v.ToString(v >= 10 ? "F1" : "F2") + " GB"; }
        // net：≥1 MB/s 用 MB/s，否则 KB/s
        if (v >= 1024) { double mb = v / 1024.0; return mb.ToString(mb >= 10 ? "F0" : "F1") + " MB/s"; }
        return v.ToString(v >= 100 ? "F0" : v >= 10 ? "F1" : "F2") + " KB/s";
    }

    // ★ 图表控件复用：网格线与 Y 轴标签只在【尺寸/刻度/单位】变化时才重建。
    //   原来每 2 秒对 3 张图全部 Clear + 新建（每张 11 条 Line + 11 个 TextBlock + 曲线/区域）→
    //   持续的分配与布局开销（监控面板"看着静止却一直吃 CPU"的来源之一）。
    private sealed class ChartCache { public double W, H, Scale; public string Kind = ""; }
    private static readonly Dictionary<Canvas, ChartCache> ChartCacheMap = [];

    private static void DrawChart(Canvas canvas, Canvas yLabels, Canvas xLabels, List<double>[] series, string[] colors, string kind, IReadOnlyList<DateTime> times)
    {
        double w = CanvasW(canvas), h = CanvasH(canvas);
        if (w < 12 || h < 12) { return; }

        // ★ 先摘掉"采样中…"占位：gridSame 分支只按数量删尾部子元素，摘不掉夹在中间的 TextBlock，
        //   否则真实曲线画上去后占位文字还压在上面（实测内存图出现过"有柱子 + 采样中…"并存）。
        for (int i = canvas.Children.Count - 1; i >= 0; i--)
        {
            if (canvas.Children[i] is Control pc && (pc.Tag as string) == "ph") { canvas.Children.RemoveAt(i); }
        }

        double max = 0.0001;
        foreach (var s in series) { foreach (var v in s) { if (v > max) { max = v; } } }
        double scale = NiceMax(max);   // 轴取"顺眼"刻度（1/2/5 × 10^n）

        var gridBrush = new SolidColorBrush(Color.Parse("#14FFFFFF"));   // L2：网格线也用 alpha 白 hairline
        var axisBrush = new SolidColorBrush(Color.Parse("#73FFFFFF"));   // L2：轴标签 35% 白（文档允许的唯一破例）

        if (!ChartCacheMap.TryGetValue(canvas, out ChartCache? cc)) { cc = new ChartCache(); ChartCacheMap[canvas] = cc; }
        bool gridSame = Math.Abs(cc.W - w) < 0.5 && Math.Abs(cc.H - h) < 0.5 && Math.Abs(cc.Scale - scale) < 1e-9 && cc.Kind == kind;
        if (gridSame)
        {
            // 网格线与 Y 标签原样保留，只把曲线/区域（排在网格线之后的子元素）删掉
            while (canvas.Children.Count > PlotDivisions + 1) { canvas.Children.RemoveAt(canvas.Children.Count - 1); }
        }
        else
        {
            canvas.Children.Clear();
            yLabels.Children.Clear();
            cc.W = w; cc.H = h; cc.Scale = scale; cc.Kind = kind;
        }
        xLabels.Children.Clear();   // X 轴时间标签数量随样本数变化，数量很少，每次重建

        // 参考线 + Y 轴标签：位置都来自 YFor，标签在参考线上垂直居中（gridSame 时保留上一轮的，不重建）
        if (!gridSame)
        {
            for (int gi = 0; gi <= PlotDivisions; gi++)
            {
                double v = scale * gi / PlotDivisions;
                double y = YFor(v, scale, h);
                canvas.Children.Add(new Avalonia.Controls.Shapes.Line
                {
                    StartPoint = new Avalonia.Point(0, y),
                    EndPoint = new Avalonia.Point(w, y),
                    Stroke = gridBrush,
                    StrokeThickness = 1,
                });
                var lab = new TextBlock { Text = FmtAxis(v, kind, scale), FontSize = 9, Foreground = axisBrush };
                lab.Measure(Avalonia.Size.Infinity);
                Canvas.SetLeft(lab, 52 - 7 - lab.DesiredSize.Width);      // 右对齐，与图表留 7px 间隙
                Canvas.SetTop(lab, y - lab.DesiredSize.Height / 2);       // ★ 垂直居中在参考线上
                yLabels.Children.Add(lab);
            }
        }

        for (int si = 0; si < series.Length; si++)
        {
            var s = series[si];
            if (s.Count == 0) { continue; }
            var pts = new List<Avalonia.Point>();
            for (int i = 0; i < s.Count; i++) { pts.Add(new Avalonia.Point(XFor(i, w), YFor(s[i], scale, h))); }
            var brush = new SolidColorBrush(Color.Parse(colors[si]));
            // ★ 三张图统一"折线 + 竖向渐变面积 + 末端圆点"。
            //   纯裸线太干；纯色块在平直序列上又是一块实心（原来的毛病）。
            //   渐变面积（顶部一线颜色 → 底部透明）两者都不是：有份量、又不糊住网格。
            // 面积填充：**实心 12%**（画在折线之前，线压在上面）。
            //   试过两种都更差：纯裸线太干；竖向渐变面积在"平直序列"上会变成一个半透明方框（内存那张）。
            //   实心低透明度是 Activity Monitor 的做法：起伏序列 = 带峰的填充面积，平直序列 = 一条"填充量规"，
            //   两者是同一种语言，不会互相打架。想调浓淡只改下面这个常数。
            // ★ 只给第一条序列填充：网络的"上行/下行"两条线各填 12% 会叠成浑浊的棕色，
            //   主序列填充 + 次序列只画线，是仪表盘里标准的层级做法。
            var c0 = Color.Parse(colors[si]);
            if (si == 0)
            {
                var areaPts = new List<Avalonia.Point>(pts) { new(pts[^1].X, PlotBottom(h)), new(pts[0].X, PlotBottom(h)) };
                canvas.Children.Add(new Avalonia.Controls.Shapes.Polygon
                {
                    Points = areaPts,
                    Fill = new SolidColorBrush(Color.FromArgb(0x1F, c0.R, c0.G, c0.B)),   // 12% 不透明
                });
            }
            canvas.Children.Add(new Avalonia.Controls.Shapes.Polyline
            {
                Points = pts,
                Stroke = brush,
                StrokeThickness = 2,
                StrokeJoin = Avalonia.Media.PenLineJoin.Round,
                StrokeLineCap = Avalonia.Media.PenLineCap.Round,
            });
            // 末端圆点：标出"当前值"，也让最后一段线不至于看起来断在半空
            var tail = pts[^1];
            var dot = new Avalonia.Controls.Shapes.Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = brush,
                Stroke = new SolidColorBrush(Color.Parse("#1A1A1C")),
                StrokeThickness = 1.5,
            };
            Canvas.SetLeft(dot, tail.X - 3.5);
            Canvas.SetTop(dot, tail.Y - 3.5);
            canvas.Children.Add(dot);
        }

        // X 轴时间标签：★ 必须与**实际画出来的样本**同一套索引 ——
        //   曾经按"60 格固定窗口"取样：刚启动只有 20 个样本时，曲线占左边 1/3，
        //   而唯一的时间标签却贴在最右边（看着像 X 轴反了）。
        //   现在在【已有时段】内等分：times[j] 与第 j 个数据点同索引、同 XFor。
        int n = times.Count;
        int shown = Math.Min(n, MaxSamples);
        if (shown >= 2)
        {
            double lastRight = double.NegativeInfinity;
            for (int k = 0; k < 4; k++)
            {
                int j = Math.Clamp((int)Math.Round(k / 3.0 * (shown - 1)), 0, n - 1);
                var lab = new TextBlock { Text = times[j].ToString("HH:mm:ss"), FontSize = 8.5, Foreground = axisBrush };
                lab.Measure(Avalonia.Size.Infinity);
                double x = XFor(j, w);
                double lx = Math.Clamp(x - lab.DesiredSize.Width / 2, 0, Math.Max(0, w - lab.DesiredSize.Width));
                if (lx < lastRight + 6) { continue; }        // 与上一个标签挤在一起就跳过（曲线短时最容易发生）
                lastRight = lx + lab.DesiredSize.Width;
                Canvas.SetLeft(lab, lx);
                xLabels.Children.Add(lab);
            }
        }
    }    /// <summary>把轴最大值抬到 1/2/5 × 10^n 的"顺眼"刻度。</summary>
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

    private async void OnTrimMemory(object? sender, RoutedEventArgs e)
    {
        try
        {
            ERR("正在压缩服务端工作集…");
            ERR(await Task.Run(() => ProcessBackend.TrimMemory()));
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
                bool ok = await Task.Run(() => FileBackend.Download(remoteZip, localZip));
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
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            ERR("本机备份中（压缩 Worlds + config），大存档请稍候…");
            // ★ K4（审查报告）：整段压缩原来直接做在 async void 的 UI 事件处理器里 ——
            //   十几 MB 存档 + 全部配置会冻界面 1~3 秒。现在放后台线程，UI 更新在 await 之后回主线程。
            (int n, int skip, string zipName) = await Task.Run(() =>
            {
                Directory.CreateDirectory(outDir);
                string zipPath = Path.Combine(outDir, "backup_" + stamp + ".zip");
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
                return (n, skip, Path.GetFileName(zipPath));
            });
            ERR($"备份完成: {zipName}（{n} 个文件" + (skip > 0 ? $"，{skip} 个被占用跳过" : "") + "）");
        }
        catch (Exception ex) { ERR("备份失败: " + ex.Message); }
    }

    // ===== 管理器插件系统 v2（宿主：加载/重载/命令/指标/事件/配置/权限/i18n）=====
    /// <summary>切档重载插件期间，压掉"[插件] 已加载/[API 探针]"这类每档都一样的刷屏（约 40 行/次）。</summary>
    private static volatile bool suppressPluginLog;

    /// <summary>正在重载的插件属于哪一档。重载是后台任务，切档后它还在跑；</summary>
    /// <summary>它的日志必须回"那一档"的终端缓冲，否则会串到刚切过去的档里（历史 bug）。</summary>
    private volatile string pluginReloadProfileId = "";

    private void PluginLog(string m)
    {
        if (suppressPluginLog && (m.StartsWith("[插件] 已加载") || m.StartsWith("[API 探针]")
            || m.StartsWith("[定时任务] 已加载") || m.StartsWith("[Lua] 定时任务 已加载")))
        {
            return;
        }
        string owner = pluginReloadProfileId;
        if (owner.Length > 0 && owner != ActiveSrv.Id) { TermTo(owner, m, PickColor(m)); return; }
        Term(m, PickColor(m));
    }

    /// <summary>往"指定档"的终端缓冲写一行（用于后台重载这类异步来源）。</summary>
    /// <summary>往【指定档】的终端追加一行（后台档日志流走这里）。
    /// ★ 服务端日志行是终端里的绝大部分内容，所以"溢出留痕 / 暂停计数"也必须在这里跟着算，
    ///   否则标题只统计管理层自己那几行，用户看到的仍是"日志莫名少了"。</summary>
    private void TermTo(string profileId, string text, string color)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => TermTo(profileId, text, color)); return; }
        try { AppendTermLine(profileId, new TermLine { Text = text, Brush = GetBrush(color) }); }
        catch { }
    }

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
                // ★ 插件数据按档隔离：新档用 plugin-data\<档id>\，迁移来的第一档继续用旧的全局 plugin-data
                //   （否则 LuaTaskScheduler 的"上次执行时间"会跨服务器混；迁移档搬目录又会让老配置丢失）
                DataRoot = ActiveSrv.PluginDataRoot(AppContext.BaseDirectory),
                DeployRoot = () => DeployRoot,
                RestBase = () => Rest,
                Token = () => Token,
                Worlds = RowsSnapshot,
                Log = PluginLog,
                // ★ v6：服务端进程控制。在线状态用管理器的**心跳探测结果**（本机/远端同一语义），
                //   起停复用与「启动/停止服务端」按钮完全相同的路径（含远端 SSH/控制台启动器与掉线自启保护）。
                ServerOnline = () => lastServerUp == true,
                StartServer = reason => PluginStartServerAsync(reason),
                StopServer = reason => PluginStopServerAsync(reason),
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
                Upload = (local, remote) => FileBackend.Upload(local, remote),
                Download = (remote, local) => FileBackend.Download(remote, local),
                InvalidateFileCache = FsInvalidate,
                RunCommand = (ps, ms) => SshRun(ps, ms),
                AfterLoad = RebuildPluginMenu,
                BeforeUnload = ClearPluginViews,
            };
            pluginHost = new PluginHost(opts);
            pluginHost.Load();   // 加载后宿主会回调 AfterLoad(=RebuildPluginMenu)，那里统一做地图页注入
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
        ClearMapPluginBar();       // 插件卸载/重载前清掉它们往地图页加的按钮
        pluginViews.Clear();
        ExtContent.Content = null;
        ExtPanel.Children.Clear();
        ExtHeader.IsVisible = false;
    }

    /// <summary>加载完成后重建右侧「扩展」菜单 + 刷新插件管理页/指标。</summary>
    private void RebuildPluginMenu()
    {
        // ★ 这里就是宿主 AfterLoad 的落点：**每次 Load/Reload 都会走到**。
        //   地图页的插件注入必须挂在这里 —— 否则"停用→启用"或"重载"之后控件就再也回不来了
        //   （卸载时 BeforeUnload 会把它们清掉）。
        ContributeMapToPlugins();
        ContributeFileActionsToPlugins();   // ★ v5：文件页插件动作（同样必须在这里重挂）

        if (pluginHost is null || ExtPanel is null) { return; }
        ExtPanel.Children.Clear();
        var list = pluginHost.Plugins;
        ExtHeader.IsVisible = list.Count > 0;
        foreach (var p in list)
        {
            var b = new Button
            {
                Content = p.Icon + "  " + p.Name, FontFamily = new Avalonia.Media.FontFamily("Segoe MDL2 Assets, Microsoft YaHei"), FontSize = 13,
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
                Text = p.Name + " 是纯动作/服务型插件（未提供界面）。", FontSize = 13,
                Foreground = GetBrush("#9BA3AE"), TextWrapping = Avalonia.Media.TextWrapping.Wrap,
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
        // ★ 重载要编译 .csx / 跑插件 OnLoad，放后台做，别冻界面（界面回调由 PluginHost 切回 UI 线程）
        _ = Task.Run(() =>
        {
            try { pluginHost?.Reload(); ERR("插件已重新加载（见下方列表）"); }
            catch (Exception ex) { ERR("重载失败: " + ex.Message); }
        });
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
            FontSize = 11, Foreground = GetBrush("#9BA3AE"), TextWrapping = Avalonia.Media.TextWrapping.Wrap,
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
                _ = Task.Run(() => { try { pluginHost?.Reload(); } catch (Exception ex) { ERR("重载失败: " + ex.Message); } });
            }
            else { ERR("停用失败：文件不存在"); }
        };
        return RowCard(left, btn);
    }

    private static Control FailedRow(FailedPlugin f)
    {
        var sp = new StackPanel { Spacing = 2 };
        sp.Children.Add(new TextBlock { Text = "✗ " + Path.GetFileName(f.DllPath), FontSize = 13, Foreground = GetBrush("#FF7B72") });
        sp.Children.Add(new TextBlock { Text = f.Reason, FontSize = 11, Foreground = GetBrush("#9A9AA0"), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        return Card(sp);
    }

    private Control FileRow(string file)
    {
        bool disabled = file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
        string real = disabled ? file[..^".disabled".Length] : file;
        var sp = new StackPanel { Spacing = 2 };
        sp.Children.Add(new TextBlock { Text = (disabled ? "⏸ " : "… ") + Path.GetFileName(real), FontSize = 13, Foreground = GetBrush("#B9B9C0") });
        sp.Children.Add(new TextBlock
        {
            Text = disabled ? "已停用（.disabled）—— 点「启用」恢复" : "存在但未加载（不是插件，或加载被跳过）",
            FontSize = 11, Foreground = GetBrush("#848D9A"),
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
                _ = Task.Run(() => { try { pluginHost?.Reload(); } catch (Exception ex) { ERR("重载失败: " + ex.Message); } });
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
        Foreground = GetBrush("#9BA3AE"), Margin = new Avalonia.Thickness(2, 12, 0, 4),
    };

    private static TextBlock Plain(string t) => new()
    {
        Text = t, FontSize = 12, Foreground = GetBrush("#9BA3AE"),
        TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(2, 3, 0, 3),
    };

    // ===== 插件指标（P1-10）=====
    // ★ 这个方法由 2 秒指标定时器调用（见 SampleMetrics）。原来每次 Children.Clear() + 重建所有
    //   Grid/TextBlock，即使名称集合与数值都没变 → 持续 GC 压力与可视树失效，插件多时拖窗口发顿。
    //   现在：名称集合同样就只改 Text，变了才重建。
    private readonly List<(TextBlock Val, string Name)> pluginRowCache = [];

    // ★ K2：插件指标的后台采样缓存与在飞闩锁（键 = 类型|插件|名称）。
    //   指标值是 double?（PluginMetricInfo.Provider），状态值是 string（PluginStatusInfo.Provider）。
    private readonly Dictionary<string, double?> pluginMetricCache = [];
    private readonly Dictionary<string, string?> pluginStatusCache = [];
    // ★ 这两个缓存是"后台线程写、UI 线程读"，Dictionary 本身不是线程安全的 ——
    //   必须加锁：写入端每项加锁，UI 端一次性快照后离线使用（缓存很小，快照成本可忽略）。
    private readonly object pluginCacheLock = new();
    private int pluginSampling;

    private static string PluginKey(string kind, string plugin, string name) => kind + "|" + plugin + "|" + name;

    /// <summary>在后台线程调用插件的 Provider 并写进缓存（UI 线程只读缓存）。</summary>
    private void KickPluginSample()
    {
        if (Interlocked.CompareExchange(ref pluginSampling, 1, 0) != 0) { return; }
        IReadOnlyList<PluginMetricInfo> metrics = pluginHost is null ? Array.Empty<PluginMetricInfo>() : pluginHost.Metrics;
        IReadOnlyList<PluginStatusInfo> statuses = pluginHost is null ? Array.Empty<PluginStatusInfo>() : pluginHost.Statuses;
        _ = Task.Run(() =>
        {
            try
            {
                foreach (var m in metrics)
                {
                    string k = PluginKey("m", m.PluginName, m.Name);
                    double? v = null;
                    try { v = m.Provider(); } catch { v = null; }
                    lock (pluginCacheLock) { pluginMetricCache[k] = v; }
                }
                foreach (var s in statuses)
                {
                    string k = PluginKey("s", s.PluginName, s.Name);
                    string sv = "—";
                    try { sv = s.Provider() ?? "—"; } catch { sv = "—"; }
                    lock (pluginCacheLock) { pluginStatusCache[k] = sv; }
                }
            }
            finally { Volatile.Write(ref pluginSampling, 0); }
        });
    }

    private void RefreshPluginMetrics()
    {
        if (PluginMetricsHost is null) { return; }
        // ★ K2（审查报告）：Provider 可能做 ssh/REST（单个实测 98~147ms，且没有超时），原来在 2 秒的 UI tick 上同步调用。
        //   现在：后台线程调 Provider 写缓存，UI 线程只读缓存、只做界面更新。
        //   契约变化：Provider 可能从【后台线程】被调用（见 PluginHost 里的说明），插件不要在 Provider 里碰 UI。
        KickPluginSample();
        // ★ 快照（加锁）后再离线使用，避免与后台写入并发访问 Dictionary
        Dictionary<string, double?> metricSnap;
        Dictionary<string, string?> statusSnap;
        lock (pluginCacheLock) { metricSnap = new(pluginMetricCache); statusSnap = new(pluginStatusCache); }
        IReadOnlyList<PluginMetricInfo> metrics = pluginHost is null ? Array.Empty<PluginMetricInfo>() : pluginHost.Metrics;
        IReadOnlyList<PluginStatusInfo> statuses = pluginHost is null ? Array.Empty<PluginStatusInfo>() : pluginHost.Statuses;
        if (PluginMetricsHeader is not null) { PluginMetricsHeader.IsVisible = metrics.Count > 0 || statuses.Count > 0; }

        bool same = pluginRowCache.Count == metrics.Count + statuses.Count;
        for (int i = 0; i < metrics.Count && same; i++) { if (pluginRowCache[i].Name != metrics[i].Name) { same = false; } }
        for (int i = 0; i < statuses.Count && same; i++) { if (pluginRowCache[metrics.Count + i].Name != statuses[i].Name) { same = false; } }
        if (same && pluginRowCache.Count > 0)
        {
            for (int i = 0; i < metrics.Count; i++)
            {
                var m = metrics[i];
                m.Value = metricSnap.TryGetValue(PluginKey("m", m.PluginName, m.Name), out double? cv) ? cv : null;
                pluginRowCache[i].Val.Text = m.Display;
            }
            for (int i = 0; i < statuses.Count; i++)
            {
                var s = statuses[i];
                s.Value = statusSnap.TryGetValue(PluginKey("s", s.PluginName, s.Name), out string? sv) ? (sv ?? "—") : "—";
                pluginRowCache[metrics.Count + i].Val.Text = s.Value;
            }
            return;
        }

        pluginRowCache.Clear();
        PluginMetricsHost.Children.Clear();
        foreach (var m in metrics)
        {
            m.Value = metricSnap.TryGetValue(PluginKey("m", m.PluginName, m.Name), out double? cv) ? cv : null;
            var row = new Grid { ColumnDefinitions = new Avalonia.Controls.ColumnDefinitions("*,Auto") };
            var name = new TextBlock { Text = m.Name, FontSize = 11, Foreground = GetBrush("#9A9AA0") };
            var val = new TextBlock { Text = m.Display, FontSize = 11, Foreground = GetBrush("#66CCFF") };
            Grid.SetColumn(name, 0);
            Grid.SetColumn(val, 1);
            row.Children.Add(name);
            row.Children.Add(val);
            Avalonia.Controls.ToolTip.SetTip(row, "来自插件 " + m.PluginName);
            PluginMetricsHost.Children.Add(row);
            pluginRowCache.Add((val, m.Name));
        }

        // 文本状态项（P2-15：状态胶囊）
        foreach (var s in statuses)
        {
            s.Value = statusSnap.TryGetValue(PluginKey("s", s.PluginName, s.Name), out string? sv) ? (sv ?? "—") : "—";
            var row = new Grid { ColumnDefinitions = new Avalonia.Controls.ColumnDefinitions("*,Auto") };
            var name = new TextBlock { Text = s.Name, FontSize = 11, Foreground = GetBrush("#9A9AA0") };
            var val = new TextBlock { Text = s.Value, FontSize = 11, Foreground = GetBrush("#7EE787"), TextWrapping = Avalonia.Media.TextWrapping.NoWrap };
            Grid.SetColumn(name, 0);
            Grid.SetColumn(val, 1);
            row.Children.Add(name);
            row.Children.Add(val);
            Avalonia.Controls.ToolTip.SetTip(row, "来自插件 " + s.PluginName);
            PluginMetricsHost.Children.Add(row);
            pluginRowCache.Add((val, s.Name));
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
            if (cmds.Count == 0) { Term("  （没有插件注册命令）", "#9BA3AE"); }
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
            _ = Task.Run(() => { try { pluginHost?.Reload(); } catch (Exception ex) { ERR("自动重载失败: " + ex.Message); } });
        };
        return t;
    }

    private void NotifyServerState(bool up)
    {
        if (lastServerUp == up) { return; }
        lastServerUp = up;
        try { pluginHost?.RaiseServerStateChanged(up); } catch { }
    }


    // ===== 多服务器：左侧卡片 =====
    /// <summary>把服务器卡片刷新成当前真实状态（档数变了才重建，其余就地改属性）。</summary>
    private void UpdateServerCards()
    {
        try
        {
            if (ServerCards is null) { return; }
            var list = ServersCfg.Servers;
            if (srvRows.Count != list.Count)
            {
                srvRows.Clear();
                srvCards.Clear();
                foreach (var p in list) { var r = new ServerCardRow { Id = p.Id }; srvRows.Add(r); srvCards.Add(r); }
            }
            for (int i = 0; i < list.Count && i < srvRows.Count; i++)
            {
                ServerProfile p = list[i];
                ServerCardRow r = srvRows[i];
                // ★ 乐观高亮：切档要 3~4 秒，点下去就得立刻有反馈（否则像没点上）
                string showActiveId = pendingActiveId.Length > 0 ? pendingActiveId : ActiveSrv.Id;
                bool active = p.Id == showActiveId;
                r.Id = p.Id;
                r.Name = p.Name.Length > 0 ? p.Name : p.Id;
                // 卡片：第 1 行 名称+状态+世界数 / 第 2 行 IP:端口+方式 / 第 3 行 CPU·内存 / 第 4 行 上下行
                string mode = p.DirectReady ? "直连" : p.IsRemote ? "SSH" : "本机";
                bool isPending = p.Id == pendingActiveId;
                string st = isPending ? "切换中"
                    : p.Status == 1 ? "在线"
                    : p.Status == 2 ? "REST 不通"
                    : active ? "未运行 / 未知"
                    : p.ProbeAtMs == 0 ? "未探活" : p.ProbeOk ? "SSH 可达" : "不可达";
                string lat = p.RestMs >= 0 ? " · REST " + p.RestMs + "ms" : (p.LastSwitchMs >= 0 ? " · 切档 " + p.LastSwitchMs + "ms" : "");
                r.Address = p.AddressText + " · " + mode + lat;
                r.State = st;
                bool haveData = p.Status == 1;
                var (mCpu, mMem, mUp, mDown) = MetricsFor(p);
                r.CpuText = haveData ? "CPU " + mCpu.ToString("F1") + "%" : "CPU —";
                r.MemText = haveData ? "内存 " + mMem.ToString("F2") + "G" : "内存 —";
                r.NetText = haveData ? "↑ " + SpeedText(mUp) + "   ↓ " + SpeedText(mDown) : "↑ —   ↓ —";
                r.Worlds = isPending ? "切换中…" : p.WorldCount >= 0 ? p.WorldCount + " 世界" : "—";
                // 激活档：绿=在线 / 黄=REST 不通 / 灰=未运行/未知；非激活档：绿=那台机器/端口可达（没开隧道）
                string hex = isPending ? "#FEBC2E"
                    : active ? p.Status switch { 1 => "#32D74B", 2 => "#FEBC2E", _ => "#6B6B70" }
                    : p.ProbeAtMs == 0 ? "#6B6B70" : p.ProbeOk ? "#32D74B" : "#6B6B70";
                r.StatusBrush = GetBrush(hex);
                r.IsActive = active;
                string where = p.DirectReady ? "直连 agent " + p.DirectHost + ":" + p.DirectPort
                    : p.IsRemote ? "SSH " + p.SshUser + "@" + p.SshHost + ":" + p.SshPort
                    : "本机目录 " + p.LocalDir;
                string state = active
                    ? p.Status == 1 ? "在线" : p.Status == 2 ? "REST 不通（进程在）" : "未运行 / 未知"
                    : p.ProbeText.Length > 0 ? p.ProbeText : "尚未探活";
                string worldsTip = p.WorldCount >= 0 ? " · 世界数 " + p.WorldCount + "（上次连接时）" : "";
                r.Tip = (active ? "当前激活 · " : "点击切换 · ") + where + " · " + state + worldsTip;
            }
        }
        catch { }
    }

    /// <summary>
    /// 非激活档的轻量探活（60 秒一次）：只探"那台机器的端口通不通"，**不开隧道、不连 REST**。
    /// 目的：卡片状态点不再显示"上次已知"的陈旧值（原来的灰色是什么都不知道）。
    /// 代价：每档一次 TCP connect（1.5 秒超时），对远端机器极轻。
    /// </summary>
    private async Task ProbeInactiveServersAsync()
    {
        if (switching) { return; }
        foreach (var p in ServersCfg.Servers.ToArray())
        {
            if (p.Id == ActiveSrv.Id) { continue; }        // 激活档的状态由 REST 心跳给，别重复探
            bool ok;
            string text;
            if (p.DirectReady)
            {
                ok = await TcpOkAsync(p.DirectHost, p.DirectPort, 4000);
                text = (ok ? "直连 agent 可达 " : "直连 agent 不可达 ") + p.DirectHost + ":" + p.DirectPort;
            }
            else if (p.HasSsh)
            {
                ok = await TcpOkAsync(p.SshHost, p.SshPort, 4000);   // ★ 这条链路 RTT ~1.7 秒，1.5 秒会误判成不可达
                text = (ok ? "SSH 可达（未连接）" : "SSH 不可达") + " " + p.SshHost + ":" + p.SshPort;
            }
            else
            {
                ok = PortListening(p.Port);
                text = (ok ? "本机 REST 在听 " : "本机 REST 没在听 ") + p.Port;
            }
            p.ProbeOk = ok;
            p.ProbeAtMs = Environment.TickCount64;
            p.ProbeText = text;
        }
        UpdateServerCards();
    }

    private static async Task<bool> TcpOkAsync(string host, int port, int timeoutMs)
    {
        try
        {
            using var c = new System.Net.Sockets.TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);
            await c.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch { return false; }
    }

    /// <summary>点卡片 = 切到那台服务器；右键 = 重命名/复制/删除。</summary>
    private void OnServerCardPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is not Border card || card.Tag is not string id) { return; }
        var pt = e.GetCurrentPoint(card);
        if (pt.Properties.IsRightButtonPressed)
        {
            var cm = new ContextMenu();
            var miRename = new MenuItem { Header = "重命名 / 改连接…" };
            miRename.Click += (_, _) => { ShowView("conn"); };
            var miCopy = new MenuItem { Header = "复制为新服务器" };
            miCopy.Click += (_, _) => AddServerCopy(id);
            var miDel = new MenuItem { Header = "删除此服务器" };
            miDel.Click += (_, _) => DeleteServer(id);
            cm.Items.Add(miRename);
            cm.Items.Add(miCopy);
            cm.Items.Add(new Separator());
            cm.Items.Add(miDel);
            cm.Open(card);
            e.Handled = true;
            return;
        }
        e.Handled = true;
        _ = SwitchServerAsync(id);
    }

    /// <summary>「＋」= 打开新增服务器对话框（右键菜单里的"复制为新服务器"仍是快捷路径）。</summary>
    private async void OnAddServer(object? sender, RoutedEventArgs e)
    {
        try
        {
            var (rest, agent) = AllocTunnelPorts();
            var p = await NewServerDialog.ShowAsync(this, ActiveSrv, rest, agent, "服务器 " + (ServersCfg.Servers.Count + 1));
            if (p is null) { return; }
            ServersCfg.Servers.Add(p);
            SaveConn();
            UpdateServerCards();
            ERR($"已新增服务器「{p.Name}」· {p.AddressText} · 隧道端口 REST {rest} / agent {agent} —— 正在切过去");
            await SwitchServerAsync(p.Id);
        }
        catch (Exception ex) { ERR("新增服务器失败: " + ex.Message); }
    }

    /// <summary>新增一台：复制当前档的连接设置（ssh/路径/令牌全带走），端口自动分一对空闲的。</summary>
    private void AddServerCopy(string fromId)
    {
        try
        {
            var src = ServersCfg.Find(fromId) ?? ActiveSrv;
            var (rest, agent) = AllocTunnelPorts();
            var p = ServersConfig.Clone(src, "服务器 " + (ServersCfg.Servers.Count + 1), rest, agent);
            ServersCfg.Servers.Add(p);
            SaveConn();
            UpdateServerCards();
            ERR($"已新增服务器「{p.Name}」· 隧道端口 REST {rest} / agent {agent} —— 正在切换过去（改地址请到「连接设置」）");
            _ = SwitchServerAsync(p.Id);
        }
        catch (Exception ex) { ERR("新增服务器失败: " + ex.Message); }
    }

    /// <summary>删除一台（不能删激活档、不能删最后一台 —— 避免"手里一台都不剩"）。</summary>
    private void DeleteServer(string id)
    {
        try
        {
            if (ServersCfg.Servers.Count <= 1) { ERR("只剩一台服务器，不能删除"); return; }
            if (id == ActiveSrv.Id) { ERR("不能删除当前激活的服务器 —— 先切到别的服务器再删"); return; }
            var p = ServersCfg.Find(id);
            if (p is null) { return; }
            KillTunnel(p);
            histByServer.Remove(p.Id);
            ServersCfg.Servers.Remove(p);
            SaveConn();
            UpdateServerCards();
            ERR($"已删除服务器「{p.Name}」（它的 plugin-data\\{p.SafeId} 目录保留，需要的话手动删）");
        }
        catch (Exception ex) { ERR("删除服务器失败: " + ex.Message); }
    }

    /// <summary>给新档分一对没被占用、也没被别的档用的本地隧道端口（REST/agent 相差 1）。</summary>
    private static (int rest, int agent) AllocTunnelPorts()
    {
        var used = new HashSet<int>();
        foreach (var s in ServersCfg.Servers) { used.Add(s.Port); used.Add(s.AgentPort); }
        for (int rest = 17880; rest <= 17976; rest += 2)
        {
            int agent = rest + 1;
            if (used.Contains(rest) || used.Contains(agent)) { continue; }
            if (PortFree(rest) && PortFree(agent)) { return (rest, agent); }
        }
        return (17880, 17881);
    }

    private static bool PortFree(int port)
    {
        try
        {
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            l.Start();
            l.Stop();
            return true;
        }
        catch { return false; }
    }


    // ===== 每档的实时数值（后台常驻轮询写入；卡片直接显示） =====
    /// <summary>最近一次采样值（后台轮询写进 histByServer，这里只读最后一个点）。</summary>
    private (double cpu, double mem, double up, double down) LastMetricsOf(string id)
    {
        if (!histByServer.TryGetValue(id, out var h)) { return (0, 0, 0, 0); }
        double Last(List<double> l) => l.Count > 0 ? l[^1] : 0;
        return (Last(h.Cpu), Last(h.Mem), Last(h.Up), Last(h.Down));
    }

    /// <summary>该档当前的 CPU/内存/上下行（激活档读活列表，非激活档读后台快照）。</summary>
    private (double cpu, double mem, double up, double down) MetricsFor(ServerProfile p)
        => p.Id == ActiveSrv.Id
            ? (cpuHist.Count > 0 ? cpuHist[^1] : 0, memHist.Count > 0 ? memHist[^1] : 0,
               upHist.Count > 0 ? upHist[^1] : 0, downHist.Count > 0 ? downHist[^1] : 0)
            : LastMetricsOf(p.Id);

    private static string SpeedText(double kb)
        => kb >= 1024 ? (kb / 1024.0).ToString("F2") + " MB/s" : kb.ToString("F1") + " KB/s";


    // ===== 维护（整链重置/备份/恢复 三合一）=====
    private void OnShowMaint(object? sender, RoutedEventArgs e) => ShowView("maint");

    // ===== 连接设置：本机管理 / 远端管理 两张卡片 + 模式切换 =====
    /// <summary>
    /// 模式切换（本机 ↔ 远端）后，把【与"管哪台机器"相关】的缓存与视图全部重置并重取。
    /// ★ 后端本身会自动重建（FileBackend 里比较 IsRemote/Root），但界面上的数据是各页面各自缓存的：
    ///   世界列表、日志游标、文件页当前目录、配置对象、指标/快照基准、agent 连接……
    ///   不重置就会出现"切了模式，控制台/文件/配置还是旧那台的内容"（用户实测反馈）。
    /// </summary>
    private void AfterModeSwitch()
    {
        ResetConnScopedState();
        // agent 立刻重探：它会按新的 base 重启 日志/stdout/指标 三条流（原来是等最多 20 秒的定时器）
        _ = Task.Run(() => TickAgentAsync());
        _ = RefreshWorldsAsync();
        RefreshLog();
        _ = Task.Run(() => RefreshSnapshot());
        RefreshActiveViews();
    }

    /// <summary>只重置"与哪台机器相关"的游标/缓存/基线，不触发重取。模式切换与服务器切换共用。</summary>
    private void ResetConnScopedState()
    {
        lastLogFile = ""; lastRemoteLine = ""; logPos = 0; lastRemoteLogMs = 0;
        // ★ 不能写 FsRoot：它是 FileBackend.Root，会【构造文件后端】——直连/远端模式下可能发 HTTP，
        //   而这里是 UI 线程。文件页第一次读时会自己校正回真正的根（见 ShowFiles 里的 FsExists 兜底）。
        fileCwd = UseRemote ? RemoteDir : LocalDir;
        lastWorldRel = "";                       // 地图：下次重新解析、导出目标失效
        cfgLauncher = null; cfgEdge = null;      // 配置页：丢掉旧机器读出来的配置对象
        SnapPrevAtMs = 0; SnapshotAtMs = 0; SnapPrevCpuMs = -1; SnapshotProcs = 0;
        MetricsAtMs = 0; AgentMetricsAtMs = 0; AgentSentBytes = -1; AgentRecvBytes = -1;
        RestAlive = false; dotsInit = false;     // 状态灯重判
        lastServerUp = null;                     // 让插件重新收到一次"上线"事件（新档是另一台服务端）
        if (_fileBackend is CachedFileBackend c) { c.InvalidateCache(); }
    }

    /// <summary>当前正在看的页面就地重刷（否则得手动切走再切回来）。</summary>
    private void RefreshActiveViews()
    {
        try
        {
            if (PluginsView.IsVisible) { ShowFiles(); }                                // 文件管理页
            if (ConfigView.IsVisible) { OnShowConfig(null, new RoutedEventArgs()); }    // 配置编辑页
            if (MapView.IsVisible)                                                      // 地图预览：清掉上一台、并按新档重新载入
            {
                ForgetMapView();
                _ = ReloadMapForActiveServerAsync();
            }
            if (ChainView.IsVisible) { RefreshChain(); }                               // 世界链：重取（async void）
            if (EditView.IsVisible)                                                     // 编辑器：标明"属于另一台"
            {
                EditHint.Text = "⚠ 服务器已切换：这个编辑缓冲区属于上一台服务器，保存会被拦下。请切回原服务器，或重新打开文件。";
            }
        }
        catch { }
    }

    /// <summary>
    /// 切档后在地图页时自动重载：重取世界下拉 → 尽量保留同名选择（没有就挑拼接图/第一个）→ 渲染 → 适应窗口。
    /// 用户报"停在地图页时切服务器不会自动加载"就是这条路原来缺了。
    /// </summary>
    private async Task ReloadMapForActiveServerAsync()
    {
        try
        {
            if (MapPicker is null) { return; }
            string keep = lastMapPick.Length > 0 ? lastMapPick : (MapPicker.SelectedItem as string ?? "");
            SetStatusLine("已切换服务器，正在重新读取世界列表…");
            await RefreshMapPickerAsync();                       // 内部会重读该档的世界名与磁盘文件
            var items = MapPicker.ItemsSource as IEnumerable<string>;
            if (items is null || !items.Any())
            {
                SetStatusLine("该服务器没有可显示的世界文件");
                return;
            }
            var list = items.ToList();
            int idx = list.IndexOf(keep);
            if (idx < 0) { idx = list.FindIndex(x => x.StartsWith("全部", StringComparison.Ordinal)); }
            if (idx < 0) { idx = 0; }
            MapPicker.SelectedIndex = idx;
            await LoadMap();                                     // 渲染（LoadMap 自己会更新 lastMapPick）
            FitMap();
        }
        catch (Exception ex) { SetStatusLine("切档后重载地图失败: " + ex.Message); }
    }

    /// <summary>丢弃地图预览里上一台服务器的图（图是"世界文件渲染结果"，跨台展示就是错的）。</summary>
    private void ForgetMapView()
    {
        try
        {
            if (MapImage?.Source is IDisposable old) { old.Dispose(); }
            if (MapImage is not null) { MapImage.Source = null; }
            lastWorldRel = "";
            if (MapInfo is not null)
            {
                MapInfo.Text = "服务器已切换 —— 上一台的地图已释放，请重新选择世界或点「渲染」。";
            }
            if (MapSelectRect is not null) { MapSelectRect.IsVisible = false; }
        }
        catch { }
    }

    // ===== 多服务器：切换激活档 =====
    /// <summary>切档门闩：4 个定时器 + 3 条流在切换期间一律让路（否则旧档的采样/日志会写进新档）。</summary>
    private static bool switching;

    /// <summary>正在切往/已在的那一档（用于"点下去立刻高亮"的乐观反馈，切完清空）。</summary>
    private static string pendingActiveId = "";

    /// <summary>切档期间被点到的下一档（合并连点，切完立刻接着切）。</summary>
    private static string pendingSwitchId = "";

    /// <summary>插件重载去抖代号：只有最后一次请求真正执行。</summary>
    private static int pluginReloadGen;

    /// <summary>切档后"重活"去抖代号（世界/日志/agent/快照）。</summary>
    private static int postSwitchGen;

    /// <summary>最近一次切档完成的时间（TickCount64）。用于"切完这几秒内别去 SSH 捞日志尾巴"。</summary>
    private static long switchDoneMs;

    /// <summary>插件当前是按哪一档加载的。切回同一档时跳过重载（实测单次重载 2~13 秒，多为插件自己的 OnLoad）。</summary>
    private static string pluginLoadedProfileId = "";

    /// <summary>
    /// 切档后的"重活"统一去抖 250ms：世界列表 / 日志首屏 / agent 三条流 / 合并快照。
    /// ★ 为什么必须去抖：切档本身压到 5~10ms 之后，用户连点会被**真的执行**十几次，
    ///   每次都重拉世界 + 重灌 60~120 行日志首屏 + 重探 agent → 实测把 UI 冻到 2.9 秒。
    ///   合并成"最后一次切完统一做一遍"就干净了。
    /// </summary>
    private void SchedulePostSwitchWork()
    {
        int my = ++postSwitchGen;
        _ = Task.Run(async () =>
        {
            await Task.Delay(250);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (switching && sw.ElapsedMilliseconds < 3000) { await Task.Delay(100); }
            if (my != postSwitchGen || switching) { return; }
            try { await RefreshWorldsAsync(); } catch { }
            if (my != postSwitchGen) { return; }
            // 日志要碰控件，回 UI 线程做。★ 已有该档历史（切回来的情况）就不再重读文件/远端尾巴，直接显示现成的
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (term.Count == 0) { RefreshLog(); }
                    UpdateServerCards();
                    RedrawChartsNow();
                }
                catch { }
            });
            _ = Task.Run(async () =>
            {
                await TickAgentAsync();                // 接日志/指标流（探活 + 起 3 条流）
                // ★ 立刻补两次采样：否则三张图要等 2 秒定时器（首帧还是基线）→ 最长 4 秒都显示"采样中…"
                Dispatcher.UIThread.Post(() => { try { SampleMetrics(); } catch { } });
                await Task.Delay(350);
                Dispatcher.UIThread.Post(() => { try { SampleMetrics(); } catch { } });
            });
            _ = Task.Run(() => RefreshSnapshot());     // 合并快照（ssh，后台）
        });
    }

    /// <summary>
    /// 插件按新档重载（去抖 1500ms）：连续切档时只有最后一次真正执行。
    /// 每次重载都会拆掉 5 个插件、重新扫描/加载、跑 OnLoad（ApiProbe 还会跑一整轮自检 + 一次 ssh），
    /// 所以"来回快切"时它是最大的一笔开销 —— 合并掉就能明显不卡。
    /// </summary>
    private void ScheduleProfilePluginReload()
    {
        int my = ++pluginReloadGen;
        _ = Task.Run(async () =>
        {
            // ① 去抖：连续切档只做最后一次
            await Task.Delay(1500);
            if (my != pluginReloadGen) { return; }
            // ② 等"切档空档"：切档期间 UI 线程正忙（重建列表/图表/卡片），这时候重载插件会和它抢，
            //    实测把 UI 冻住近 900ms。最多等 10 秒，等不到就跳过（下一轮切档会再排）。
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (switching && wait.ElapsedMilliseconds < 10000) { await Task.Delay(200); }
            if (switching || my != pluginReloadGen) { return; }
            // ★ 用户关掉了"切档重载插件"：仍然把 DataRoot 指向新档（插件下次读写就走新目录），
            //   但不动插件实例 —— 代价是插件的内存态（Lua 定时任务上次执行时间等）会跨档共用。
            if (!ServersCfg.ReloadPluginsOnSwitch)
            {
                try
                {
                    string newRoot = ActiveSrv.PluginDataRoot(AppContext.BaseDirectory);
                    if (pluginHost is not null)
                    {
                        pluginHost.Options.DataRoot = newRoot;
                        // ★ 关键：DataDir 是"首次访问后缓存"的，只换 DataRoot 不会失效 ——
                        //   不清缓存的话插件会继续读写上一档的目录（A-suite 文档代理复核时抓到的真 bug）
                        pluginHost.InvalidateDataDirs();
                    }
                    string sample = pluginHost is null || pluginHost.Plugins.Count == 0 ? "(无插件)" : pluginHost.Plugins[0].Name;
                    try
                    {
                        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "switchperf.log"),
                            $"{DateTime.Now:HH:mm:ss.fff}	switch-noreload	profile={ActiveSrv.Name}	dataRoot={newRoot}	samplePlugin={sample}" + Environment.NewLine);
                    }
                    catch { }
                    Dispatcher.UIThread.Post(() => ERR($"已切档但不重载插件（reloadPluginsOnSwitch=false）：DataRoot→{newRoot}（已失效 DataDir 缓存）"));
                }
                catch { }
                return;
            }
            // ★ 已经按这一档加载过插件了就不重载：来回快切（A→B→A）最后回到原档时，这一步整个省掉
            //   （实测单次 Reload 2.3~12.8 秒，主要花在插件自己的 OnLoad：ApiProbe 一轮自检含 ssh、Lua 编译）
            if (ActiveSrv.Id == pluginLoadedProfileId) { return; }
            try
            {
                if (pluginHost is null) { return; }
                // 执行时再取一次：期间可能又换档了
                string reloadId = ActiveSrv.Id;                      // ★ 这一轮重载属于哪一档
                pluginHost.Options.DataRoot = ActiveSrv.PluginDataRoot(AppContext.BaseDirectory);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                suppressPluginLog = true;
                pluginReloadProfileId = reloadId;                    // ★ 它的日志只回这一档的终端
                try { pluginHost.Reload(); }
                finally { suppressPluginLog = false; pluginReloadProfileId = ""; }
                sw.Stop();
                pluginLoadedProfileId = reloadId;
                Dispatcher.UIThread.Post(() => ERR($"插件已按「{ActiveSrv.Name}」重载：{pluginHost.Plugins.Count} 个，用时 {sw.ElapsedMilliseconds} ms（逐条加载日志已折叠）"));
                try
                {
                    string pf = Path.Combine(AppContext.BaseDirectory, "switchperf.log");
                    File.AppendAllText(pf, $"{DateTime.Now:HH:mm:ss.fff}	plugin-reload={sw.ElapsedMilliseconds}ms	profile={ActiveSrv.Name}	插件数={pluginHost.Plugins.Count}	失败={pluginHost.Report?.Failed.Count ?? 0}" + Environment.NewLine);
                }
                catch { }
            }
            catch (Exception ex) { Dispatcher.UIThread.Post(() => ERR("切档重载插件失败: " + ex.Message)); }
        });
    }

    private void SaveHistFor(string id)
        => histByServer[id] = new MetricHistory { Cpu = cpuHist, Mem = memHist, Up = upHist, Down = downHist, Times = timeHist };

    private void LoadHistFor(string id)
    {
        if (histByServer.TryGetValue(id, out var h))
        {
            cpuHist = h.Cpu; memHist = h.Mem; upHist = h.Up; downHist = h.Down; timeHist = h.Times;
        }
        else
        {
            cpuHist = []; memHist = []; upHist = []; downHist = []; timeHist = [];
        }
        // 采样基线也要重置：否则第一拍会把"上一台的累计值"当成本台的差分起点（CPU/网络会出假尖峰）
        samplesSeen = 0; lastSample = DateTime.Now; lastCpuTime = TimeSpan.Zero;
        lastBytesSeen = false; lastBytesSent = 0; lastBytesRecv = 0; snapPushedAt = 0;
    }

    /// <summary>停掉某档的 ssh 隧道进程（本机 ssh 客户端，不是远端服务端；远端服务端一律不动）。</summary>
    private static void KillTunnel(ServerProfile p)
    {
        int pid = p.TunnelPid;
        p.TunnelPid = 0;
        if (pid <= 0) { pid = FindTunnelPidByPort(p.Port); }   // 认领不到 pid 时按端口兜底（只杀 ssh 客户端）
        p.LastSpawnMs = 0;                                      // 允许立刻重建（否则冷却期会让切回来的那台起不来）
        p.TunnelKey = "";
        if (pid <= 0) { return; }
        try { using var pr = System.Diagnostics.Process.GetProcessById(pid); pr.Kill(); } catch { }
    }

    /// <summary>
    /// 令牌为空的新档：直接从服务端的配置文件里读一个（本机直读 / 远端走 agent 或 ssh）。
    /// 与「连接设置 → 自动检测」同一套解析规则；只在切换后令牌为空时跑一次。
    /// </summary>
    private async Task<bool> TryFillTokenAsync(ServerProfile p)
    {
        try
        {
            string root = p.IsRemote ? p.RemoteDir : p.LocalDir;
            string[] cands =
            [
                Path.Combine(root, "config", "TShockAPI", "config.json"),
                Path.Combine(root, "tshock", "config.json"),
                Path.Combine(root, "config", "config.json"),
            ];
            string txt = "";
            foreach (string c in cands)
            {
                txt = await Task.Run(() => FsReadText(c));
                if (!string.IsNullOrWhiteSpace(txt)) { break; }
            }
            if (string.IsNullOrWhiteSpace(txt)) { return false; }
            if (ParseJsonObj(txt) is not JsonObject o) { return false; }
            string tok = "";
            int port = 0;
            if (o["ApplicationRestTokens"] is JsonObject dict) { foreach (var kv in dict) { tok = kv.Key; break; } }
            if (int.TryParse(o["RestApiPort"]?.ToString(), out int p1) && p1 > 0) { port = p1; }
            if (tok.Length == 0 && o["Settings"] is JsonObject st)
            {
                if (int.TryParse(st["RestApiPort"]?.ToString(), out int p2) && p2 > 0) { port = p2; }
                if (st["ApplicationRestTokens"] is JsonObject d2) { foreach (var kv in d2) { tok = kv.Key; break; } }
            }
            if (tok.Length == 0) { return false; }
            p.Token = tok;
            if (!p.IsRemote && port > 0) { p.Port = port; }   // 本机档的 Port 必须是服务端自己的 REST 端口
            return true;
        }
        catch { return false; }
    }


    // ===== 后台档常驻监控（"没在看的那些服务器"也保持新鲜：切过去立刻有世界列表/曲线） =====
    //   参考 PGame-TSManager：每台服务器各有一个常驻对象在跑自己的状态，切换只是换视图。
    //   这里不搞"每档一个进程对象"的大改，而是给非激活档加一条独立轮询：
    //   保活隧道 -> REST /world/list（写进该档的 rows 集合）-> agent /metrics（写进该档的指标历史）。
    //   激活档不走这条（它有现成的 SSE + 2 秒采样管线），所以不会重复拉。
    private sealed class BgMon
    {
        public long LastRestMs;
        public long LastAgentMs;
        public long LastSent = -1, LastRecv = -1, LastNetMs;
        public int RestFails;      // 连续失败次数：偶发一次不要把卡片打成"未运行"
    }
    private static readonly HttpClient BgHttp = NewLocalHttp(TimeSpan.FromSeconds(8));
    private readonly Dictionary<string, BgMon> bgMon = [];

    /// <summary>后台档轮询间隔：世界列表 20 秒、指标 5 秒（对远端是每档每轮两次小请求，可忽略）。</summary>
    private const int BgRestIntervalMs = 20000;
    private const int BgAgentIntervalMs = 5000;

    private async Task PollBackgroundAsync()
    {
        if (switching) { return; }
        foreach (var p in ServersCfg.Servers.ToArray())
        {
            if (p.Id == ActiveSrv.Id) { continue; }     // 激活档交给现有管线
            try { await PollOneBackgroundAsync(p); } catch { }
        }
    }

    private async Task PollOneBackgroundAsync(ServerProfile p)
    {
        if (!bgMon.TryGetValue(p.Id, out var st)) { st = new BgMon(); bgMon[p.Id] = st; }
        long now = Environment.TickCount64;

        // ① 隧道：保活（不在听就拉一条；TrimTunnels 保证总数不超上限）
        if (p.IsRemote)
        {
            EnsureTunnel(p);
            TrimTunnels(ActiveSrv);
        }

        string restBase = p.DirectReady ? "http://" + p.DirectHost + ":" + p.DirectPort
                                        : "http://127.0.0.1:" + p.Port;
        // ★ 本机档没有隧道：agent 就在 7879（AgentRemotePort）；档上的 AgentPort 是"隧道本地入口"，只对远端档有意义
        string agentBase = p.DirectReady ? restBase
            : p.IsRemote ? "http://127.0.0.1:" + p.AgentPort
            : "http://127.0.0.1:" + AgentRemotePort;

        // ② 世界列表
        if (now - st.LastRestMs >= BgRestIntervalMs)
        {
            st.LastRestMs = now;
            try
            {
                string url = restBase + "/world/list?token=" + Uri.EscapeDataString(p.Token);
                var swRest = System.Diagnostics.Stopwatch.StartNew();
                string txt = await BgHttp.GetStringAsync(url);
                swRest.Stop();
                p.RestMs = swRest.ElapsedMilliseconds;      // 卡片上显示"这台慢不慢"
                using var doc = JsonDocument.Parse(txt);
                var incoming = new List<(string n, string s, bool r)>();
                if (doc.RootElement.TryGetProperty("worlds", out var ws))
                {
                    foreach (var w in ws.EnumerateArray())
                    {
                        incoming.Add((w.GetProperty("name").GetString() ?? "?",
                                      w.GetProperty("width").GetInt32() + "x" + w.GetProperty("height").GetInt32(),
                                      w.GetProperty("running").GetBoolean()));
                    }
                }
                st.RestFails = 0;
                p.Status = 1;
                ApplyBackgroundWorlds(p, incoming);
            }
            catch
            {
                // 只要失败 1~2 次就显示"REST 不通"，连续 3 次才算"未运行"（避免网络抖一下卡片就红）
                st.RestFails++;
                if (st.RestFails >= 3) { p.Status = 3; }
                else if (p.Status == 1) { p.Status = 2; }
                UpdateServerCards();
            }
        }

        // ③ 指标（远端走 agent /metrics；本机档直接量"该档目录下的那个进程"）
        if (now - st.LastAgentMs >= BgAgentIntervalMs)
        {
            st.LastAgentMs = now;
            if (!p.IsRemote)
            {
                // ★ 本机档：不能借用 7879 的 agent（它只认主部署），否则两个本机档读数完全一样
                var lm = LocalMetrics(p);
                if (lm is null) { ApplyBackgroundMetrics(p, 0, 0, 0, 0); }
                else
                {
                    double up0 = 0, down0 = 0;
                    var (sent, recv) = NetTotals();
                    double secs0 = st.LastNetMs > 0 ? Math.Max(0.5, (now - st.LastNetMs) / 1000.0) : 0;
                    if (secs0 > 0 && sent >= 0 && st.LastSent >= 0)
                    {
                        up0 = Math.Max(0, (sent - st.LastSent) / 1024.0 / secs0);
                        down0 = Math.Max(0, (recv - st.LastRecv) / 1024.0 / secs0);
                    }
                    st.LastSent = sent; st.LastRecv = recv; st.LastNetMs = now;
                    double memMbLocal = lm.Value.memMb;
                    p.Status = 1;
                    ApplyBackgroundMetrics(p, lm.Value.cpu, memMbLocal / 1024.0, up0, down0);
                }
                return;
            }
            try
            {
                string txt = await BgHttp.GetStringAsync(agentBase + "/metrics");
                using var doc = JsonDocument.Parse(txt);
                var srv = doc.RootElement.GetProperty("server");
                double cpu = srv.TryGetProperty("cpuSingleCorePct", out var c) ? c.GetDouble() : 0;
                double memMb = srv.TryGetProperty("privateMB", out var m) ? m.GetDouble() : 0;
                long sent = -1, recv = -1;
                if (doc.RootElement.TryGetProperty("net", out var net))
                {
                    sent = net.TryGetProperty("sentBytes", out var sb) ? sb.GetInt64() : -1;
                    recv = net.TryGetProperty("recvBytes", out var rb) ? rb.GetInt64() : -1;
                }
                double up = 0, down = 0, secs = st.LastNetMs > 0 ? Math.Max(0.5, (now - st.LastNetMs) / 1000.0) : 0;
                if (secs > 0 && sent >= 0 && st.LastSent >= 0)
                {
                    up = Math.Max(0, (sent - st.LastSent) / 1024.0 / secs);
                    down = Math.Max(0, (recv - st.LastRecv) / 1024.0 / secs);
                }
                st.LastSent = sent; st.LastRecv = recv; st.LastNetMs = now;
                ApplyBackgroundMetrics(p, cpu, memMb / 1024.0, up, down);
            }
            catch { }
        }
    }

    /// <summary>把后台拉到的世界列表写进"该档自己的"集合（界面可见时才真正重排）。</summary>
    private void ApplyBackgroundWorlds(ServerProfile p, List<(string n, string s, bool r)> incoming)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (!rowsByServer.TryGetValue(p.Id, out var list))
                {
                    list = [];
                    rowsByServer[p.Id] = list;
                }
                bool changed = list.Count != incoming.Count + 1;
                if (!changed)
                {
                    for (int i = 0; i < incoming.Count; i++)
                    {
                        var r = list[i + 1];
                        if (r.Name != incoming[i].n || r.Size != incoming[i].s || r.Running != incoming[i].r) { changed = true; break; }
                    }
                }
                if (changed)
                {
                    list.Clear();
                    list.Add(new WorldRow { Name = "全部世界", Size = "", Running = true });
                    foreach (var (n, s, run) in incoming) { list.Add(new WorldRow { Name = n, Size = s, Running = run }); }
                }
                p.WorldCount = incoming.Count(x => x.r);
                UpdateServerCards();
            }
            catch { }
        });
    }

    /// <summary>把后台拉到的指标写进"该档自己的"历史（切过去曲线就是现成的）。</summary>
    private void ApplyBackgroundMetrics(ServerProfile p, double cpu, double memGb, double up, double down)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (!histByServer.TryGetValue(p.Id, out var h)) { h = new MetricHistory(); histByServer[p.Id] = h; }
                PushCarry(h.Cpu, cpu); PushCarry(h.Mem, memGb); PushCarry(h.Up, up); PushCarry(h.Down, down);
                h.Times.Add(DateTime.Now);
                if (h.Times.Count > MaxSamples) { h.Times.RemoveAt(0); }
                UpdateServerCards();      // 卡片上的 CPU/内存/上下行跟着变
            }
            catch { }
        });
    }


    // ===== 本机档：按"部署目录"定位它自己的服务端进程 =====
    //   ★ 原来用 Process.GetProcessesByName("UnifierTSL").FirstOrDefault()：本机挂两份部署时，
    //     两个档都会读到同一个进程 → 卡片内存/CPU 完全一样（用户 2026-10-17 报的现象）。
    private readonly Dictionary<string, (TimeSpan cpu, long ms)> localCpuPrev = [];
    private readonly Dictionary<string, (int pid, string key)> localPidCache = [];

    private System.Diagnostics.Process? LocalServerProc(ServerProfile p)
    {
        try
        {
            string want = (p.LocalDir ?? "").TrimEnd('\\', '/');
            if (want.Length == 0) { return null; }
            if (localPidCache.TryGetValue(p.Id, out var cached) && cached.key == want && cached.pid > 0)
            {
                try
                {
                    var cp = System.Diagnostics.Process.GetProcessById(cached.pid);
                    if (!cp.HasExited) { return cp; }
                }
                catch { }
            }
            foreach (var pr in System.Diagnostics.Process.GetProcessesByName("UnifierTSL"))
            {
                try
                {
                    if (pr.HasExited) { continue; }
                    string? path = pr.MainModule?.FileName;
                    if (path is null) { continue; }
                    if (path.StartsWith(want, StringComparison.OrdinalIgnoreCase))
                    {
                        localPidCache[p.Id] = (pr.Id, want);
                        return pr;
                    }
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    /// <summary>整机网卡累计字节（排除回环，避免隧道流量重复计）。后台线程用，~75~174ms/次。</summary>
    private static (long sent, long recv) NetTotals()
    {
        long s = 0, r = 0;
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) { continue; }
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) { continue; }
                var stat = ni.GetIPStatistics();
                s += stat.BytesSent; r += stat.BytesReceived;
            }
        }
        catch { }
        return (s, r);
    }

    /// <summary>本机档自己那份服务端的 (单核 CPU%, 私有内存 MB)；进程不在返回 null。</summary>
    private (double cpu, double memMb)? LocalMetrics(ServerProfile p)
    {
        var pr = LocalServerProc(p);
        if (pr is null) { return null; }
        try
        {
            pr.Refresh();
            long nowMs = Environment.TickCount64;
            double cpuPct = 0;
            TimeSpan cpuNow = pr.TotalProcessorTime;
            if (localCpuPrev.TryGetValue(p.Id, out var prev) && nowMs > prev.ms)
            {
                double secs = (nowMs - prev.ms) / 1000.0;
                if (secs >= 0.2)
                {
                    cpuPct = Math.Max(0, (cpuNow - prev.cpu).TotalMilliseconds / (secs * 1000.0) * 100.0);
                    cpuPct = Math.Min(cpuPct, 100.0);
                }
            }
            localCpuPrev[p.Id] = (cpuNow, nowMs);
            return (cpuPct, pr.PrivateMemorySize64 / 1048576.0);
        }
        catch { return null; }
    }

    // ===== 非激活档的后台日志流（去掉"切到新档终端空 1 秒"这段） =====
    //   每个非激活的远端档起一条 /stream/log SSE，行先攒在本地，300ms 批量灌进"该档自己的"终端集合。
    //   该档不是当前显示的那个 -> 它的集合没绑在 ListBox 上，追加不触发任何布局。
    //   上限 4 条（与 MaxLiveTunnels 对齐），激活档不订阅（它走现有那条主日志流）。
    private sealed class LogSub
    {
        public CancellationTokenSource Cts = new();
    }
    private readonly Dictionary<string, LogSub> logSubs = [];
    private const int MaxBackgroundLogSubs = 8;   // 硬上限（servers.json 的 bgLogSubs 会被夹到这个值）

    private ObservableCollection<TermLine> TermFor(string id)
    {
        if (id == ActiveSrv.Id) { return term; }
        if (!termByServer.TryGetValue(id, out var c)) { c = []; termByServer[id] = c; }
        return c;
    }

    private void StopBackgroundLog(string id)
    {
        if (logSubs.TryGetValue(id, out var sub))
        {
            try { sub.Cts.Cancel(); } catch { }
            logSubs.Remove(id);
        }
    }

    /// <summary>按当前档位情况增删后台日志订阅（由 5 秒定时器调用）。</summary>
    private void SyncBackgroundLogs()
    {
        int head = 0;                      // 已订阅条数（含本轮新增）
        foreach (var p in ServersCfg.Servers.ToArray())
        {
            bool want = p.Id != ActiveSrv.Id && p.IsRemote && p.HasSsh;
            if (!want) { StopBackgroundLog(p.Id); continue; }
            if (logSubs.ContainsKey(p.Id)) { head++; continue; }
            int cap = Math.Max(0, Math.Min(ServersCfg.BgLogSubs, MaxBackgroundLogSubs));
            if (head < cap) { head++; } else { continue; }
            var sub = new LogSub();
            logSubs[p.Id] = sub;
            _ = Task.Run(() => BackgroundLogLoopAsync(p, sub.Cts.Token));
        }
    }

    private async Task BackgroundLogLoopAsync(ServerProfile p, CancellationToken ct)
    {
        string baseUrl = p.DirectReady ? "http://" + p.DirectHost + ":" + p.DirectPort : "http://127.0.0.1:" + p.AgentPort;
        var pending = new List<string>();
        long lastFlush = Environment.TickCount64;
        void Flush()
        {
            if (pending.Count == 0) { return; }
            var batch = pending.ToArray();
            pending.Clear();
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    // ★ 日志流是终端里绝大部分内容 —— 必须走统一写入口，否则过滤/暂停计数/丢弃计数全都统计不到它
                    //   （原来这里直接 coll.Add，绕过了 AppendTermLine，导致"过滤 ExcuCmd 显示 0/245"）。
                    foreach (var raw in batch)
                    {
                        AppendTermLine(p.Id, new TermLine { Text = raw, Brush = GetBrush(PickColor(raw)) });
                    }
                }
                catch { }
            });
        }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var http = NewLocalHttp(Timeout.InfiniteTimeSpan);
                using var resp = await http.GetAsync(baseUrl + "/stream/log", HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                while (!ct.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(ct);
                    if (line is null) { break; }
                    if (line.StartsWith("data: ")) { pending.Add(line.Substring(6)); }
                    if (Environment.TickCount64 - lastFlush > 300)
                    {
                        lastFlush = Environment.TickCount64;
                        Flush();
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch { }
            Flush();
            if (ct.IsCancellationRequested) { break; }
            try { await Task.Delay(2000, ct); } catch { break; }
        }
        Flush();
    }

    /// <summary>
    /// 切到另一台服务器。顺序是硬要求：先停旧流（agent 三条 + 日志游标）→ 停旧隧道 → 换档 →
    /// 重置缓存与指标历史 → 插件按新档重建（DataRoot 跟着档走）→ 起新隧道 → 重取世界/日志。
    /// </summary>
    private async Task SwitchServerAsync(string id)
    {
        var target = ServersCfg.Find(id);
        if (target is null) { ERR("切换失败：servers.json 里没有 id=" + id); return; }
        if (switching)
        {
            // ★ 切档进行中又点了别的卡片：不丢点击、也不叠加——记下最后点的那张，当前这次结束后接着切。
            //   原来直接 return → 点击被吞掉，用户会连点，体感就是"很卡/没反应"。
            if (target.Id != ActiveSrv.Id) { pendingSwitchId = target.Id; pendingActiveId = target.Id; UpdateServerCards(); }
            return;
        }
        if (target.Id == ActiveSrv.Id) { pendingActiveId = ""; UpdateServerCards(); return; }

        switching = true;
        pendingActiveId = target.Id;      // ★ 点下去立刻高亮（下一行刷新卡片，0 ms 反馈）
        UpdateServerCards();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var phases = new List<(string Name, long Ms)>();
        long lastMark = 0;
        void Phase(string name) { phases.Add((name, sw.ElapsedMilliseconds - lastMark)); lastMark = sw.ElapsedMilliseconds; }
        string fromName = ActiveSrv.Name;
        string fromId = ActiveSrv.Id;
        try
        {
            // ① 记住旧档：指标历史 + 世界数
            SaveHistFor(ActiveSrv.Id);
            if (rows.Count > 1) { ActiveSrv.WorldCount = rows.Count - 1; }

            // ② 先停流（顺序不能反：旧档的 SSE 一旦先连上新 base，日志就串台了）
            //   ★ 目标档若正跑着后台日志流，也要停掉：接下来主日志流会接管，否则会双写
            StopBackgroundLog(target.Id);
            try { agentLogCts?.Cancel(); } catch { }
            agentLogCts = null;
            AgentAlive = false; AgentBaseUsed = ""; AgentProbeErr = "";
            AgentMetricsAtMs = 0; AgentSentBytes = -1; AgentRecvBytes = -1;
            lastAgentSent = -1; lastAgentRecv = -1;
            AgentClient.Alive = false; AgentClient.Base = ""; AgentClient.ResetRoot();
            lastLogFile = ""; lastRemoteLine = ""; logPos = 0; lastRemoteLogMs = 0;
            LastSshError = ""; TunnelAlive = false;

            // ③ ★ 不再杀旧隧道：每档有自己的端口对（17878/17879、17880/17881…），互不冲突，
            //   而"杀掉再重建"意味着每次切回来都要重付一次 ssh 握手（实测 ~1.5~2 秒，这条链路 RTT 1.7 秒）——
            //   用户来回切会觉得"点了要等好几秒"。改成保活 + 上限 LRU 淘汰（MaxLiveTunnels）。
            TrimTunnels(ActiveSrv);
            Phase("隧道保活/淘汰");

            // ④ 换档并落盘（active 立刻持久化：下次启动还是这台）
            ServersCfg.ActiveId = target.Id;
            ActiveSrv = target;
            target.LastUsedMs = Environment.TickCount64;
            target.LastSwitchMs = sw.ElapsedMilliseconds;
            SaveConn();
            Phase("换档落盘");

            // ⑤ 与"哪台机器"相关的一切重置 + 指标历史换档 + 插件数据目录换档
            ResetConnScopedState();
            // ★ 换"每档一份"的 UI 集合引用：目标档上次的世界列表/终端内容原样回来，不清空、不重读
            rowsByServer[fromId] = rows;      // 旧档（fromId）保存引用，切回来时原样复用
            termByServer[fromId] = term;
            rows = rowsByServer.TryGetValue(target.Id, out var rowsNew) ? rowsNew : [];
            // ★ 目标档没有终端缓冲时，必须**建一份并登记**（原来只给个临时空集合，
            //   结果它不进入字典、且控件可能还挂着旧集合 → 出现过"切到新档却看到上一台日志"）
            if (!termByServer.TryGetValue(target.Id, out var termNew)) { termNew = []; termByServer[target.Id] = termNew; }
            term = termNew;
            // 过滤层同样"每档一份"：视图集合换引用，并按该档自己的过滤词重建一次
            termViewByServer[fromId] = termView;                  // 旧档的视图原样存回
            // ★ 必须显式取/建目标档的集合：此时 ActiveSrv 已经是 target，TermViewFor(target) 会返回"字段本体"，
            //   直接用它会把旧档刚存好的那份清空并灌成新档内容（两个档共用一个集合）。
            if (!termViewByServer.TryGetValue(target.Id, out var viewNew)) { viewNew = []; termViewByServer[target.Id] = viewNew; }
            termView = viewNew;
            termView.Clear();
            foreach (var line in term) { if (MatchesFilter(target.Id, line)) { termView.Add(line); } }
            while (termView.Count > MaxTermLines) { termView.RemoveAt(0); }
            if (TbTermFilter is not null)
            {
                suppressFilterEvent = true;
                TbTermFilter.Text = FilterFor(target.Id);
                suppressFilterEvent = false;
            }
            termDropped = 0;
            termPausedNew = 0;
            if (rows.Count == 0) { rows.Add(new WorldRow { Name = "全部世界", Size = "", Running = true }); }
            WorldList.ItemsSource = rows;
            // 先解绑再绑：避免 ItemsControl 复用旧集合的视觉缓存
            TermLines.ItemsSource = null;
            TermLines.ItemsSource = termView;
            UpdateTermTitle();
            if (term.Count == 0)
            {
                AppendTermLine(target.Id, new TermLine { Text = "已切换到「" + target.Name + "」，正在读取该档日志…", Brush = GetBrush("#9BA3AE") });
            }
            WorldList.SelectedIndex = 0;
            UpdateTarget();
            ConnState.Text = "切换中…";
            if (ChainText is not null) { ChainText.Text = "世界链: —"; }
            LoadHistFor(target.Id);
            RedrawChartsNow();          // ★ 立刻用新档自己的历史重画（已在内存里，不等去抖/下一次采样）
            // 插件数据目录先换好；真正 Reload 放到世界列表刷新之后（否则插件 OnLoad 时 Worlds 是空的）
            try
            {
                if (pluginHost is not null) { pluginHost.Options.DataRoot = ActiveSrv.PluginDataRoot(AppContext.BaseDirectory); }
            }
            catch (Exception ex) { ERR("切档换插件数据目录失败: " + ex.Message); }
            ApplyConsoleMode();
            Phase("重置状态");
            // ★ serverWasUp 原来是"只置 true 从不复位"的单向闩锁：切档后它仍是上一档的"曾经在跑"，
            //   于是切到一张停着的卡时，掉线自启会拿别人的旧状态把这张卡拉起来。切档必须清零。
            serverWasUp = false;
            downTicks = 0;
            watchdogFailures = 0;

            // ⑥ 起新档的隧道与流（★ 显式传目标档：切档期间别的线程不允许建隧道）
            if (target.IsRemote)
            {
                // ★ 建隧道是【阻塞】的（TCP 探活 800ms + 起进程）——放后台，UI 线程只等端口，否则每次切档白冻 0.8 秒
                await Task.Run(() => EnsureTunnel(target));
                bool restOk = await WaitPortAsync(target.Port, 5000);
                if (target.HasSsh) { await WaitPortAsync(target.AgentPort, 3000); }
                if (!restOk) { ERR($"警告：隧道 REST 端口 {target.Port} 5 秒内没就绪（世界列表可能暂时为空，20 秒定时器会自动重试）"); }
                TrimTunnels(target);          // 起好新的再淘汰，别把正在建的那条淘汰掉
            }
            Phase("隧道就绪");
            // 令牌空的新档：先尝试从服务端配置里自动读（否则世界列表一定空，用户会以为切坏了）
            if (target.Token.Length == 0)
            {
                bool got = await TryFillTokenAsync(target);
                if (got) { SaveConn(); ERR($"「{target.Name}」令牌为空 → 已从服务端配置自动读到（REST 端口 {target.Port}），已保存"); }
                else { ERR($"「{target.Name}」令牌为空 → 世界列表可能为空，请到「连接设置 → 自动检测」填一次并保存"); }
            }
            Phase("换档收尾");

            // ⑦ 世界列表到位后再重载插件（DataRoot 已换成新档的）。
            //   ★ 去抖：来回快切时只重载最后一次（每次重载都会重建 5 个插件并跑 OnLoad，很贵）
            ScheduleProfilePluginReload();
            SchedulePostSwitchWork();                        // 世界/日志/agent/快照：去抖 250ms 后统一做
            Phase("派发重活");

            UpdateServerCards();
            RedrawChartsNow();
            // 页面级刷新要读 ssh/渲染，延后 400ms 再由 UI 线程做，别压在切档关键路径上
            _ = Task.Run(async () => { await Task.Delay(400); Dispatcher.UIThread.Post(RefreshActiveViews); });
            Phase("收尾");
            sw.Stop();
            // 诊断：每次切档追加一行（脚本/探针可量化"来回切换"，写满 256KB 自动清空）
            try
            {
                string pf = Path.Combine(AppContext.BaseDirectory, "switchperf.log");
                var fi = new FileInfo(pf);
                if (fi.Exists && fi.Length > 256 * 1024) { File.WriteAllText(pf, ""); }
                // ★ 页面级状态一并落盘，供回归套件断言"切档后页面有没有跟着走"
                int mapImg = (MapImage?.Source is not null) ? 1 : 0;
                int chainLen = (ChainText?.Text ?? "").Length;
                int editGuard = PageStateGuard.EditorSaveAllowed(editorProfileId, ActiveSrv.Id, out _) ? 1 : 0;
                int termLines = term?.Count ?? -1;
                File.AppendAllText(pf, $"{DateTime.Now:HH:mm:ss.fff}	{fromName} → {target.Name}	total={sw.ElapsedMilliseconds}ms	"
                    + string.Join(" ", phases.Select(x => x.Name + "=" + x.Ms))
                    + $"	mapImg={mapImg} chainLen={chainLen} editGuard={editGuard} termLines={termLines}" + Environment.NewLine);
            }
            catch { }
            string slow = string.Join(" / ", phases.Where(x => x.Ms >= 25).Select(x => x.Name + " " + x.Ms));
            ERR($"已切换服务器：{fromName} → 「{target.Name}」（{target.AddressText}）· {(UseRemote ? "远端" : "本机")} · 用时 {sw.ElapsedMilliseconds} ms" + (slow.Length > 0 ? "【" + slow + "】" : ""));
        }
        catch (Exception ex) { ERR("切换服务器失败: " + ex.Message); }
        finally
        {
            switching = false;
            switchDoneMs = Environment.TickCount64;
            try { SyncBackgroundLogs(); } catch { }     // 被切走的那台重新纳入后台日志订阅
            pendingActiveId = "";
            string next = pendingSwitchId;
            pendingSwitchId = "";
            UpdateServerCards();
            // 切档期间点过的最后一张：现在接着切（用户连点时只多一次真正的切换，中间那些被合并掉）
            if (next.Length > 0 && next != ActiveSrv.Id) { _ = SwitchServerAsync(next); }
        }
    }


    // ===== 联调钩子：--switch-test <档id|档名> =====
    //   真机验证"切档"这条路径用的（与 --selfcheck / --open-plugin 同一类钩子，不是产品功能）：
    //   启动 6 秒后自动切到目标档，再把"端口/agent 归属/世界数/流状态"写成 switchcheck.txt。
    private async Task RunSwitchTestAsync(string target)
    {
        var sb = new System.Text.StringBuilder();
        string fromId = ActiveSrv.Id, fromName = ActiveSrv.Name;
        int fromRest = ConnPort, fromAgent = AgentLocalPort;
        sb.AppendLine("UTSLManager 切档自检（--switch-test）  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine(new string('-', 72));
        sb.AppendLine($"切换前：{fromName}（{fromId}）· 隧道 REST {fromRest} / agent {fromAgent} · 世界 {Math.Max(0, rows.Count - 1)} · base {AgentBaseUsed}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await SwitchServerAsync(target);
        sw.Stop();
        await Task.Delay(2500);
        sb.AppendLine($"切换后：{ActiveSrv.Name}（{ActiveSrv.Id}）· 隧道 REST {ConnPort} / agent {AgentLocalPort} · 世界 {Math.Max(0, rows.Count - 1)} · base {AgentBaseUsed}");
        sb.AppendLine($"切档耗时：{sw.ElapsedMilliseconds} ms");
        sb.AppendLine(new string('-', 72));
        sb.AppendLine($"本机/远端判定：{(UseRemote ? "远端" : "本机")} · RemoteDir={RemoteDir} · LocalDir={LocalDir}");
        sb.AppendLine($"AgentAlive={AgentAlive} · AgentBaseUsed={AgentBaseUsed} · AgentProbeErr={(AgentProbeErr.Length == 0 ? "(无)" : AgentProbeErr)}");
        // ★ 隧道现在是【保活】设计（最多 4 条，LRU）：切完档旧档端口仍在听是【正常】的。
        //   判"坏没坏"看的是：新档端口必须就绪、旧档端口要么仍在听（保活）要么已淘汰，
        //   但绝不能"在听却被别的档占着"。所以这里只如实打印布尔值 + 注明保活下允许 True。
        bool fromAgentUp = PortListening(fromAgent), toAgentUp = PortListening(AgentLocalPort);
        bool fromRestUp = PortListening(fromRest), toRestUp = PortListening(ConnPort);
        sb.AppendLine($"旧档 agent 端口 {fromAgent} 仍在监听：{fromAgentUp}（保活下允许 True；关键看是否归该档自己的 ssh -N）");
        sb.AppendLine($"新档 agent 端口 {AgentLocalPort} 在监听：{toAgentUp}（期望 true）");
        sb.AppendLine($"旧档 REST 端口 {fromRest} 在监听：{fromRestUp}（保活下允许 True）");
        sb.AppendLine($"新档 REST 端口 {ConnPort} 在监听：{toRestUp}（期望 true）");
        sb.AppendLine($"agent 自报 root：{await PingRootAsync(AgentLocalPort)}（期望 {RemoteDir}）");
        sb.AppendLine($"世界数：{Math.Max(0, rows.Count - 1)} · 指标历史帧数 cpu={cpuHist.Count} mem={memHist.Count}");
        sb.AppendLine($"插件宿主 DataRoot：{(pluginHost is null ? "(null)" : pluginHost.Options.DataRoot)} · 插件数 {(pluginHost?.Plugins.Count ?? 0)}");
        sb.AppendLine($"隧道进程 pid：{TunnelPid}");
        sb.AppendLine(new string('-', 72));
        sb.AppendLine("终端最后 8 行：");
        foreach (var t in term.Skip(Math.Max(0, term.Count - 8))) { sb.AppendLine("  | " + t.Text); }
        try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "switchcheck.txt"), sb.ToString()); } catch { }
        ERR("切档自检完成 → switchcheck.txt");
    }

    private static bool PortListening(int port)
    {
        try
        {
            using var c = new System.Net.Sockets.TcpClient();
            bool ok = c.ConnectAsync("127.0.0.1", port).Wait(700);
            return ok;
        }
        catch { return false; }
    }

    private static async Task<string> PingRootAsync(int agentPort)
    {
        try
        {
            using var http = NewLocalHttp(TimeSpan.FromSeconds(3));
            string s = await http.GetStringAsync("http://127.0.0.1:" + agentPort + "/ping");
            using var doc = JsonDocument.Parse(s);
            return doc.RootElement.TryGetProperty("root", out var r) ? (r.GetString() ?? "") : "(无 root 字段)";
        }
        catch (Exception ex) { return "探测失败: " + ex.GetType().Name; }
    }

    /// <summary>在图表画布中央画一行提示（空历史时用，别让中间区域整块空白）。</summary>
    private static void ChartPlaceholder(Canvas canvas, string text)
    {
        try
        {
            canvas.Children.Clear();
            var tb = new TextBlock
            {
                Text = text, FontSize = 11, Tag = "ph",
                Foreground = new SolidColorBrush(Color.Parse("#848D9A")),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            Canvas.SetLeft(tb, 10);
            Canvas.SetTop(tb, Math.Max(0, canvas.Bounds.Height / 2 - 8));
            canvas.Children.Add(tb);
        }
        catch { }
    }

    /// <summary>切档后立刻用"新档自己的历史"重画三张图（否则要等下一拍。历史为空时不动，等采样）。</summary>
    private void RedrawChartsNow()
    {
        try
        {
            if (!ConsoleView.IsVisible) { return; }
            if (timeHist.Count == 0)
            {
                // ★ 这一档还没有历史：不要画空图（整块闪空最像"卡"）。画一行"采样中…"，
                //   最多 2 秒后第一个采样就会把它替换掉。
                CpuNow.Text = "—"; MemNow.Text = "—"; NetNow.Text = "—";
                ChartPlaceholder(CpuChart, "采样中…");
                ChartPlaceholder(MemChart, "采样中…");
                ChartPlaceholder(NetChart, "采样中…");
                return;
            }
            CpuNow.Text = "—"; MemNow.Text = "—"; NetNow.Text = "—";
            DrawChart(CpuChart, CpuYLabels, CpuXLabels, [cpuHist], ["#0A84FF"], "cpu", timeHist);
            DrawChart(MemChart, MemYLabels, MemXLabels, [memHist], ["#00D2E0"], "mem", timeHist);
            DrawChart(NetChart, NetYLabels, NetXLabels, [upHist, downHist], ["#DB34F2", "#FF9230"], "net", timeHist);
        }
        catch { }
    }


    private void OnShowConn(object? sender, RoutedEventArgs e)
    {
        // ★ 先切页面再填内容：填充过程万一抛异常，页面本身仍然打得开（错误由下面的 catch 报到控制台）。
        ShowView("conn");
        try
        {
            ConnHostPanel.Children.Clear();

            // ---- 小工具：字段行 / 卡片 ----
            TextBox Field(StackPanel host, string label, string val, double w)
            {
                var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Margin = new Avalonia.Thickness(0, 3, 0, 3) };
                sp.Children.Add(new TextBlock { Text = label, FontSize = 12, Width = 110, Foreground = new SolidColorBrush(Color.Parse("#9A9AA0")), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                var tb = new TextBox { Text = val, FontSize = 12, Width = w, CornerRadius = new Avalonia.CornerRadius(8) };
                sp.Children.Add(tb);
                host.Children.Add(sp);
                return tb;
            }
            Border Card(string title, string sub, out StackPanel body, out TextBlock badge)
            {
                var inner = new StackPanel { Spacing = 4 };
                inner.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = Avalonia.Media.FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#E6E6EA")) });
                if (sub.Length > 0)
                {
                    inner.Children.Add(new TextBlock { Text = sub, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#8A8A90")), TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 0, 0, 4) });
                }
                // 「✓ 当前正在使用」徽标：两张卡片都常驻显示，用它 + 边框色指出当前模式
                badge = new TextBlock { Text = "✓ 当前正在使用", FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#0A84FF")), IsVisible = false, Margin = new Avalonia.Thickness(0, 2, 0, 2) };
                inner.Children.Add(badge);
                body = new StackPanel { Spacing = 2 };
                inner.Children.Add(body);
                return new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#141417")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#2A2A30")),
                    BorderThickness = new Avalonia.Thickness(1),
                    CornerRadius = new Avalonia.CornerRadius(14),
                    Padding = new Avalonia.Thickness(14, 12),
                    Margin = new Avalonia.Thickness(0, 8, 0, 0),
                    Child = inner,
                };
            }
            void Row(StackPanel host, params Control[] items)
            {
                var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Margin = new Avalonia.Thickness(0, 6, 0, 0) };
                foreach (var it in items) { sp.Children.Add(it); }
                host.Children.Add(sp);
            }
            Button Btn(string text)
                => new() { Content = text, FontSize = 12, Padding = new Avalonia.Thickness(14, 5), CornerRadius = new Avalonia.CornerRadius(9) };
            TextBlock Hint(string text, string color = "#8A8A90")
                => new() { Text = text, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse(color)), TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 6, 0, 0) };

            // ---- 顶部：模式切换（切到本机不会丢掉已填的 SSH 配置，只是暂时不用）----
            var modeSwitch = new Avalonia.Controls.ToggleSwitch
            {
                OnContent = "远端管理（走 SSH）",
                OffContent = "本机管理（直接管本机目录）",
                IsChecked = UseRemote,
                FontSize = 12,
                Margin = new Avalonia.Thickness(0, 0, 0, 2),
            };
            ConnHostPanel.Children.Add(modeSwitch);

            // ---- 当前正在编辑的是哪台服务器（多档：连接设置改的是【激活档】）----
            var srvPanel = new StackPanel { Margin = new Avalonia.Thickness(0, 8, 0, 0) };
            var tbSrvName = Field(srvPanel, "服务器名称:", ActiveSrv.Name, 260);
            ConnHostPanel.Children.Add(srvPanel);
            ConnHostPanel.Children.Add(Hint($"当前编辑：第 {ServersCfg.Servers.FindIndex(x => x.Id == ActiveSrv.Id) + 1} / {ServersCfg.Servers.Count} 台（id={ActiveSrv.Id}）· 隧道端口 REST {ConnPort} / agent {AgentLocalPort}。左侧卡片点一下就能切，切档后这里改的是新那台。"));

            Border cardLocal = Card("本机管理", "直接读写本机 UTSL 目录、本机启停进程、本机日志。REST 三项在远端模式下表示【隧道本地端】的地址。", out StackPanel bodyLocal, out TextBlock badgeLocal);
            Border cardRemote = Card("远端管理", "控制面走 SSH，数据面走同一条 SSH 隧道（REST + 云端 agent 两条转发）；也可以改成 agent 直连。", out StackPanel bodyRemote, out TextBlock badgeRemote);
            ConnHostPanel.Children.Add(cardLocal);
            ConnHostPanel.Children.Add(cardRemote);

            // ---- 本机卡片 ----
            var tbLocal = Field(bodyLocal, "本地 UTSL 目录:", LocalDir, 380);
            var tbHost = Field(bodyLocal, "REST 主机:", ConnHost, 200);
            var tbPort = Field(bodyLocal, "REST 端口:", ConnPort.ToString(), 90);
            var tbTok = Field(bodyLocal, "令牌:", Token, 300);
            var tbStartArgs = Field(bodyLocal, "启动参数:", ActiveSrv.StartArgs, 380);
            bodyLocal.Children.Add(Hint("空 = 不带参数启动。★ 第二份本机部署（如 utsl-deploy-b）必须留空 —— 带 -joinserver first -logmode txt 会打印 banner 后直接退出（实测）；主档沿用这套参数。"));
            var tbStartEnv = Field(bodyLocal, "启动环境变量:", ActiveSrv.StartEnv, 380);
            bodyLocal.Children.Add(Hint("每行一条 KEY=VALUE；空 = 不加。UTSL_CONSOLE_MODE 由上面「显示世界控制台」开关自动管理，不要写这里。"));
            var btnAuto = Btn("自动检测（读本机 TShock 配置）");
            Row(bodyLocal, btnAuto);
            bodyLocal.Children.Add(Hint("⚠ 安全提示：REST 请勿直接暴露公网 —— 仅监听内网 / 走 SSH 隧道 / 用强令牌。令牌在 servers.json 里以 DPAPI 加密保存（同机其它用户读到也解不开）。"));

            // ---- 远端卡片 ----
            var tbSshHost = Field(bodyRemote, "SSH 主机:", SshHost, 220);
            var tbSshPort = Field(bodyRemote, "SSH 端口:", SshPort.ToString(), 90);
            var tbSshUser = Field(bodyRemote, "SSH 用户:", SshUser, 160);
            var tbSshKey = Field(bodyRemote, "SSH 密钥:", SshKey, 380);
            var tbSshDir = Field(bodyRemote, "远端 UTSL 目录:", RemoteDir, 220);
            var tbRestPort = Field(bodyRemote, "远端 REST 端口:", RemoteRestPort.ToString(), 90);
            var tbAgentPort = Field(bodyRemote, "agent 隧道端口:", AgentLocalPort.ToString(), 90);
            bodyRemote.Children.Add(Hint("隧道：本地 REST " + ConnPort + " → 远端 127.0.0.1:" + RemoteRestPort + "；本地 agent " + AgentLocalPort + " → 远端 7879。★ 多台服务器必须各用一对不同的本地端口（REST/agent 各一个），否则会连到同一台上去。"));
            // ---- 直连（面板）模式 ----
            var chkDirect = new CheckBox
            {
                Content = "直连模式（面板式：agent 暴露端口 + 令牌认证，不走 SSH 隧道）",
                IsChecked = DirectMode, FontSize = 12, Margin = new Avalonia.Thickness(0, 12, 0, 2),
            };
            bodyRemote.Children.Add(chkDirect);
            var tbDirectHost = Field(bodyRemote, "直连地址:", DirectHost, 220);
            var tbDirectPort = Field(bodyRemote, "直连端口:", DirectPort.ToString(), 90);
            var tbDirectTok = Field(bodyRemote, "访问令牌:", DirectToken, 300);
            bodyRemote.Children.Add(Hint("⚠ 直连会把 agent 暴露到网络（它可写文件、能启停进程）。务必：① 用长随机令牌；② 安全组/防火墙只放行你自己的 IP；③ 有条件就套一层 HTTPS 反向代理。agent 侧没有令牌会拒绝绑定对外地址。"));
            var btnTestSsh = Btn("测试 SSH");
            var btnTunnel = Btn("启动 / 修复隧道");
            var btnAgent = Btn("安装 / 更新云端 agent");
            Row(bodyRemote, btnTestSsh, btnTunnel, btnAgent);
            var agentStatus = new TextBlock
            {
                Text = "云端 agent：—", FontSize = 11, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.Parse("#9A9AA0")), Margin = new Avalonia.Thickness(0, 6, 0, 0),
            };
            AgentStatusText = agentStatus;
            bodyRemote.Children.Add(agentStatus);
            bodyRemote.Children.Add(Hint("远端需先放好 UTSL 运行目录并安装「控制台启动器 C:\\utsl-launch」（见 utsl-remote\\README.md）。"));

            // ---- 底部（两模式共用）----
            // ---- 世界控制台窗口（两种模式共用；模式在服务端进程启动时决定，改了要重启服务端）----
            var chkWorldConsoles = new CheckBox
            {
                Content = "显示「世界控制台」窗口（每个已加载世界一个；默认关闭）",
                IsChecked = ShowWorldConsoles, FontSize = 12, Margin = new Avalonia.Thickness(0, 12, 0, 0),
            };
            chkWorldConsoles.IsCheckedChanged += (_, _) =>
            {
                ShowWorldConsoles = chkWorldConsoles.IsChecked == true;
                ApplyConsoleMode();
                SaveConn();
                ERR(ShowWorldConsoles
                    ? "已设为显示世界控制台窗口 —— 重启服务端后生效（UTSL 的 PerWorld 模式）"
                    : "已设为不显示世界控制台窗口（headless）—— 重启服务端后生效");
            };
            ConnHostPanel.Children.Add(chkWorldConsoles);

            // ---- 停机方式（两种模式共用）----
            var stopPanel = new StackPanel { Margin = new Avalonia.Thickness(0, 12, 0, 0) };
            var tbStopCmd = Field(stopPanel, "停机指令:", ActiveSrv.StopCommand, 200);
            var tbStopSec = Field(stopPanel, "等待秒数:", ActiveSrv.StopTimeoutSec.ToString(), 90);
            ConnHostPanel.Children.Add(stopPanel);
            ConnHostPanel.Children.Add(Hint("停机三段式：先发这条指令到服务端控制台（UTSL 的 /off = 存盘 + 踢人 + 断开），再等「世界全停」或超时，最后才结束进程。空 = 直接结束进程。★ UTSL 宿主进程不会因 /off 自己退出（实测：世界停了、进程还在、REST 仍 200），所以最后那一步不能省；能省掉的是「数据来不及存盘就被杀」。"));

            // ---- 多服务器行为（全档通用，落 servers.json 顶层）----
            ConnHostPanel.Children.Add(new TextBlock
            {
                Text = "多服务器行为", FontSize = 11, FontWeight = Avalonia.Media.FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Color.Parse("#9BA3AE")), Margin = new Avalonia.Thickness(0, 14, 0, 2),
            });
            var chkReloadPlugins = new CheckBox
            {
                Content = "切换服务器时重载插件（关掉可省 4~26 秒后台开销）",
                IsChecked = ServersCfg.ReloadPluginsOnSwitch, FontSize = 12, Margin = new Avalonia.Thickness(0, 2, 0, 0),
            };
            ConnHostPanel.Children.Add(chkReloadPlugins);
            ConnHostPanel.Children.Add(Hint("关掉后：插件数据目录仍会随档切换（下次读写就是新档的），但插件**内存态**（Lua 定时任务的上次执行时间、插件计数器）会跨档共用。"
                + "默认开：每个档都从干净状态开始，代价是切档后台重载 4~26 秒（不冻界面）。"));
            chkReloadPlugins.IsCheckedChanged += (_, _) =>
            {
                ServersCfg.ReloadPluginsOnSwitch = chkReloadPlugins.IsChecked == true;
                SaveConn();
                ERR(ServersCfg.ReloadPluginsOnSwitch
                    ? "已开启：切档后重载插件（干净状态）"
                    : "已关闭：切档不重载插件（省后台开销，插件内存态跨档共用）");
            };

            var bgLogPanel = new StackPanel { Margin = new Avalonia.Thickness(0, 6, 0, 0) };
            var tbBgLogs = Field(bgLogPanel, "后台日志流上限:", ServersCfg.BgLogSubs.ToString(), 90);
            ConnHostPanel.Children.Add(bgLogPanel);
            ConnHostPanel.Children.Add(Hint("非激活档各订阅一条 /stream/log（默认 4 条，0 = 不订阅）。上限越少越省连接，但切到没访问过的档时终端会空约 1 秒。"));
            tbBgLogs.LostFocus += (_, _) =>
            {
                if (int.TryParse(tbBgLogs.Text, out int n) && n >= 0)
                {
                    ServersCfg.BgLogSubs = Math.Min(n, 8);
                    tbBgLogs.Text = ServersCfg.BgLogSubs.ToString();
                    SaveConn();
                    try { SyncBackgroundLogs(); } catch { }
                    ERR($"后台日志流上限 = {ServersCfg.BgLogSubs}");
                }
            };
            ConnHostPanel.Children.Add(Hint("说明：UTSL 的窗口模式在服务端【进程启动时】由环境变量 UTSL_CONSOLE_MODE 决定；" +
                "而且每个世界的控制台会话是循环守护的 —— 你手动关掉窗口它会被重新拉起。想让它们彻底不出现，就在这里关掉并重启服务端。"));

            var btnTest = Btn("测试连接");
            var btnSave = Btn("保存并应用");
            Row(ConnHostPanel, btnTest, btnSave);
            // ★ 不要把 ConnInfo 加进来：它在 XAML（MainWindow.axaml:287）里已经挂在可视树上，
            //   再 Add 到 ConnHostPanel 会因为"控件已有父级"抛异常 —— 而 ShowView 原本在方法最后，
            //   于是整个连接设置页都打不开（这就是本次回归的原因）。它一直显示在页面顶部，直接改 Text 即可。

            // ---- 模式切换：两张卡片都常驻显示，只标出当前正在使用的那张 ----
            void ApplyMode()
            {
                bool remoteOn = modeSwitch.IsChecked == true;
                cardLocal.BorderBrush = new SolidColorBrush(Color.Parse(remoteOn ? "#2A2A30" : "#0A84FF"));
                cardRemote.BorderBrush = new SolidColorBrush(Color.Parse(remoteOn ? "#0A84FF" : "#2A2A30"));
                badgeLocal.IsVisible = !remoteOn;
                badgeRemote.IsVisible = remoteOn;
            }
            bool suppressMode = false;      // 代码里改开关时不要触发"保存并应用"
            modeSwitch.IsCheckedChanged += (_, _) =>
            {
                if (suppressMode) { return; }
                bool remoteOn = modeSwitch.IsChecked == true;
                ApplyMode();
                ForceLocal = !remoteOn;
                if (remoteOn && !HasSsh)
                {
                    ConnInfo.Text = "远端模式需要先填【SSH 主机】与【SSH 密钥】—— 已填好请点「保存并应用」";
                    return;
                }
                SaveConn();
                UpdateServerCards();
                if (UseRemote) { EnsureTunnel(); }
                ConnInfo.Text = UseRemote
                    ? $"已切换到远端管理 · SSH {SshUser}@{SshHost}:{SshPort} → {RemoteDir} —— 正在刷新各页面…"
                    : $"已切换到本机管理（{LocalDir}）—— 正在刷新各页面…";
                AfterModeSwitch();
            };
            ApplyMode();

            // ---- 行为：测试连接 ----
            btnTest.Click += async (_, _) =>
            {
                try
                {
                    string url = "http://" + tbHost.Text + ":" + tbPort.Text + "/world/list?token=" + Uri.EscapeDataString(tbTok.Text ?? "");
                    var sw3 = System.Diagnostics.Stopwatch.StartNew();
                    string res = await Http.GetStringAsync(url);
                    sw3.Stop();
                    ConnInfo.Text = $"连接成功 ✓ 延迟 {sw3.ElapsedMilliseconds} ms · {res.Length} 字节";
                }
                catch (Exception ex) { ConnInfo.Text = "连接失败 ✗ " + ex.Message; }
            };

            // ---- 行为：保存并应用（两张卡片的字段一起落盘）----
            btnSave.Click += (_, _) =>
            {
                if ((tbSrvName.Text ?? "").Trim().Length > 0) { ActiveSrv.Name = (tbSrvName.Text ?? "").Trim(); }
                ConnHost = tbHost.Text ?? "127.0.0.1";
                ConnPort = int.TryParse(tbPort.Text, out int p) && p > 0 ? p : 7878;
                Token = tbTok.Text ?? "";
                ActiveSrv.StartArgs = (tbStartArgs.Text ?? "").Trim();
                ActiveSrv.StartEnv = (tbStartEnv.Text ?? "").Trim().Replace("\r\n", "\n");
                ActiveSrv.StopCommand = (tbStopCmd.Text ?? "").Trim();
                ActiveSrv.StopTimeoutSec = int.TryParse(tbStopSec.Text, out int sts) && sts >= 0 ? Math.Min(sts, 120) : 10;
                LocalDir = (tbLocal.Text ?? "").Trim().Length > 0 ? (tbLocal.Text ?? "").Trim() : LocalDir;
                SshHost = (tbSshHost.Text ?? "").Trim();
                SshPort = int.TryParse(tbSshPort.Text, out int sp) && sp > 0 ? sp : 22;
                SshUser = (tbSshUser.Text ?? "").Trim();
                SshKey = (tbSshKey.Text ?? "").Trim();
                RemoteDir = (tbSshDir.Text ?? "").Trim();
                RemoteRestPort = int.TryParse(tbRestPort.Text, out int rp) && rp > 0 ? rp : 7878;
                if (int.TryParse(tbAgentPort.Text, out int ap) && ap > 0) { ActiveSrv.AgentPort = ap; }
                DirectMode = chkDirect.IsChecked == true;
                DirectHost = (tbDirectHost.Text ?? "").Trim();
                DirectPort = int.TryParse(tbDirectPort.Text, out int dtp) && dtp > 0 ? dtp : 7879;
                DirectToken = (tbDirectTok.Text ?? "").Trim();
                // 保存后按"开关 + 是否填了 SSH/直连"重新判定模式：开关指向远端但都填不全 → 仍是本机，并明确提示
                bool wantRemote = modeSwitch.IsChecked == true || DirectReady;
                bool modeChanged = ForceLocal != !wantRemote || UseRemote != wantRemote;
                ForceLocal = !wantRemote;
                // 直连可用时把开关拨到远端（否则界面显示"本机管理"却实际走远端，自相矛盾）
                if (wantRemote && modeSwitch.IsChecked != true) { suppressMode = true; modeSwitch.IsChecked = true; suppressMode = false; ApplyMode(); }
                SaveConn();
                UpdateServerCards();
                if (UseRemote) { EnsureTunnel(); }
                ConnInfo.Text = UseRemote
                    ? $"已保存并应用 · 远端模式（SSH {SshUser}@{SshHost}:{SshPort} → {RemoteDir}）"
                    : wantRemote
                        ? "已保存，但 SSH 主机/密钥为空 → 仍按【本机管理】运行"
                        : $"已保存并应用 · 本机模式（{LocalDir}）";
                // 模式真的变了（或后端根目录变了）就刷新各页面，否则只是保存字段
                if (modeChanged || _fileBackend is null) { AfterModeSwitch(); }
                else { _ = RefreshWorldsAsync(); RefreshLog(); }
            };

            // ---- 行为：自动检测（读本机 TShock 配置）----
            btnAuto.Click += async (_, _) =>
            {
                try
                {
                    int foundPort = 7878; string foundTok = Token;
                    string tsCfg = Path.Combine(FsRoot, "config", "TShockAPI", "config.json");
                    if (!await Task.Run(() => FsExists(tsCfg))) { tsCfg = Path.Combine(FsRoot, "tshock", "config.json"); }
                    if (await Task.Run(() => FsExists(tsCfg)) && JsonNode.Parse(await Task.Run(() => FsReadText(tsCfg))) is JsonObject ts)
                    {
                        if (int.TryParse(ts["RestApiPort"]?.ToString(), out int p1) && p1 > 0) { foundPort = p1; }
                        var dict = ts["ApplicationRestTokens"] as JsonObject;
                        if (dict is not null) { foreach (var kv in dict) { foundTok = kv.Key; break; } }
                        else
                        {
                            if (int.TryParse(ts["Settings"]?["RestApiPort"]?.ToString(), out int p2) && p2 > 0) { foundPort = p2; }
                            var d2 = ts["Settings"]?["ApplicationRestTokens"] as JsonObject;
                            if (d2 is not null) { foreach (var kv in d2) { foundTok = kv.Key; break; } }
                        }
                    }
                    tbHost.Text = "127.0.0.1";
                    tbPort.Text = foundPort.ToString();
                    tbTok.Text = foundTok;
                    ConnInfo.Text = $"自动检测完成：端口 {foundPort}，令牌 {foundTok}（来自服务端配置 ✓ 请点「测试连接」确认）";
                }
                catch (Exception ex) { ConnInfo.Text = "自动检测失败: " + ex.Message; }
            };

            // ---- 行为：测试 SSH ----
            btnTestSsh.Click += async (_, _) =>
            {
                SshHost = (tbSshHost.Text ?? "").Trim();
                SshPort = int.TryParse(tbSshPort.Text, out int sp2) && sp2 > 0 ? sp2 : 22;
                SshUser = (tbSshUser.Text ?? "").Trim();
                SshKey = (tbSshKey.Text ?? "").Trim();
                RemoteDir = (tbSshDir.Text ?? "").Trim();
                if (!HasSsh) { ConnInfo.Text = "SSH 信息不完整 ✗ 需要主机与密钥"; return; }
                ConnInfo.Text = "正在测试 SSH…";
                try
                {
                    var sw2 = System.Diagnostics.Stopwatch.StartNew();
                    string r = await Task.Run(() => SshRun("[Console]::OutputEncoding=[Text.Encoding]::UTF8; (hostname) + ' | UTSL进程=' + ((Get-Process UnifierTSL,UnifierTSL.ConsoleClient -ErrorAction SilentlyContinue) | Measure-Object).Count + ' | 日志=' + ((Get-ChildItem '" + RemoteDir + "\\logs\\*.log' -ErrorAction SilentlyContinue) | Measure-Object).Count", 20000));
                    sw2.Stop();
                    ConnInfo.Text = r.Trim().Length == 0
                        ? "SSH 测试 ✗ 没有返回内容（超时或密钥/主机不对）"
                        : "SSH 测试 ✓ " + sw2.ElapsedMilliseconds + " ms · " + r.Replace("\n", " ");
                }
                catch (Exception ex) { ConnInfo.Text = "SSH 测试失败 ✗ " + ex.Message; }
            };

            // ---- 行为：启动/修复隧道 ----
            btnTunnel.Click += (_, _) =>
            {
                EnsureTunnel();
                ConnInfo.Text = "已尝试建立隧道：本地 " + ConnPort + " → 远端 127.0.0.1:" + RemoteRestPort;
            };

            // ---- 行为：安装/更新 agent ----
            btnAgent.Click += async (_, _) => await InstallAgentAsync(agentStatus);

            RefreshAgentStatus();
            ConnInfo.Text = UseRemote
                ? $"当前：「{ActiveSrv.Name}」远端管理（SSH {SshUser}@{SshHost}）· 配置存于 {ServersFile}"
                : $"当前：「{ActiveSrv.Name}」本机管理（{LocalDir}）· 配置存于 {ServersFile}";
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
            ChainInfo.Text = "读取中…（上次结果保留）";
            // ★ 流畅性：不先 Clear，等数据到手再重建
            // 读 EdgeStitch 配置
            JsonObject? edge = null;
            string edgePath = Path.Combine(FsRoot, "config", "EdgeStitch", "EdgeStitch.json");
            // ★ 冷缓存时这里原本是 FsExists + FsReadText = 两次 ssh 往返（链路 RTT 约 1.7 秒 → 约 3.4 秒），
            //   那正是"点世界链要等 5~6 秒"的一部分。实测契约：FsReadText 读不到返回空串，
            //   ParseJsonObj("") 返回 null → 与"文件不存在"完全同一条路，所以那次 FsExists 是多余的。
            // ★ 并且先定点失效：缓存里可能留着快照写进来的空文本，直接读会把"读不到"当成"没装"。
            // ※ 教训：这一行的替换曾经只写了注释、漏了语句，而 edge 的声明是 null，编译照样通过 ——
            //   所以这类改动必须把结果读回来核对，不能只看 build 成功。
            FsInvalidatePath(edgePath);
            string edgeText = await Task.Run(() => FsReadText(edgePath));
            edge = ParseJsonObj(edgeText);
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
                dot.Children.Add(new TextBlock { Text = w?.Running == true ? "运行中" : "未运行", FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#9A9AA0")) });
                box.Children.Add(dot);
                if (le?[chain[i]] is JsonObject eo)
                {
                    box.Children.Add(new TextBlock { Text = $"西 {eo["西"]?.ToString() ?? "—"} · 东 {eo["东"]?.ToString() ?? "—"}", FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#9BA3AE")) });
                }
                card.Child = box;
                line.Children.Add(card);
                if (i < chain.Count - 1)
                {
                    var arrow = new StackPanel { Spacing = 2, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Avalonia.Thickness(6, 0) };
                    arrow.Children.Add(new TextBlock { Text = "⟶", FontSize = 22, Foreground = new SolidColorBrush(Color.Parse("#0A84FF")), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center });
                    var mid = w is null ? "" : (le?[chain[i]]?["东"]?.ToString() ?? "");
                    var mid2 = le?[chain[i + 1]]?["西"]?.ToString() ?? "";
                    arrow.Children.Add(new TextBlock { Text = $"{mid} / {mid2}", FontSize = 9.5, Foreground = new SolidColorBrush(Color.Parse("#848D9A")), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center });
                    line.Children.Add(arrow);
                }
            }
            ChainHost.Children.Add(new TextBlock { Text = "链路（箭头间为两侧边缘触发值：出口 / 入口）", FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#848D9A")) });
            ChainHost.Children.Add(line);

            // 参数条
            if (edge is not null)
            {
                ChainHost.Children.Add(new TextBlock { Text = "触发参数", FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#848D9A")), Margin = new Avalonia.Thickness(0, 6, 0, 0) });
                var p = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, Margin = new Avalonia.Thickness(0, 6, 0, 0) };
                void Chip(string k, string v)
                {
                    var b = new Border { Background = new SolidColorBrush(Color.Parse("#1C1C21")), BorderBrush = new SolidColorBrush(Color.Parse("#2E2E35")), BorderThickness = new Avalonia.Thickness(1), CornerRadius = new Avalonia.CornerRadius(9), Padding = new Avalonia.Thickness(12, 6) };
                    b.Child = new TextBlock { Text = k + " " + v, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#C9C9CE")) };
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
                // ★ 区分"读取失败"与"确实没装"：原来一律显示"未安装"，把用户误导成"我明明装了"（实机反馈）
                string why;
                if (edgeText.Length == 0)
                {
                    bool exists = await Task.Run(() => FsExists(edgePath));
                    why = exists
                        ? "文件存在但读不到内容（链路超时 / 被拒）→ " + edgePath
                        : "这个路径没有配置文件 → " + edgePath;
                }
                else
                {
                    why = "文件存在但不是合法 JSON（前 60 字符：" + edgeText[..Math.Min(60, edgeText.Length)].Replace("\r", "").Replace("\n", " ") + "）";
                }
                ChainHost.Children.Add(new TextBlock { Text = "未能读取 EdgeStitch 世界链配置：" + why, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#FF7B72")), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            }
            ChainInfo.Text = $"{chain.Count} 个世界" + (edge is null ? "（未读到 EdgeStitch 配置，见下方原因）" : "");
            // ★★ 这里原来又调了一次 ShowView("chain") —— 数据的读取要 5~6 秒（远端冷缓存时是 ssh 往返），
            //   用户等不及切到别的页面后，这行会把页面【抢回】世界链（实机反馈的问题）。
            //   页面在 L3106 点击时已经切过去了，异步完成后只需填内容，绝不能再切视图。
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
        Text = title, FontSize = 13, FontWeight = Avalonia.Media.FontWeight.SemiBold,
        Foreground = new SolidColorBrush(Color.Parse("#E8E8EC")), Margin = new Avalonia.Thickness(0, 14, 0, 2),
    };

    private static TextBlock Lbl(string t, double w = 90) => new()
    {
        Text = t, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#9A9AA0")),
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
        var minus = new Button { Content = "◀", FontSize = 11, Width = 26, Height = 28, Padding = new Avalonia.Thickness(0), CornerRadius = new Avalonia.CornerRadius(7) };
        var plus = new Button { Content = "▶", FontSize = 11, Width = 26, Height = 28, Padding = new Avalonia.Thickness(0), CornerRadius = new Avalonia.CornerRadius(7) };
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
    private long safetySeq;      // 防抖 + 丢弃过期结果

    private async void UpdateSafety()
    {
        if (safeText is null || safeBar is null) { return; }
        // ★ 防抖：这个方法挂在每个 size 文本框的 TextChanged 与步进按钮上，用户每敲一键都会进；
        //   300ms 内的连续调用只让最后一次真正干活（其余直接返回），并保证乱序完成时旧结果不覆盖新结果。
        long my = ++safetySeq;
        try { await Task.Delay(300); } catch { }
        if (my != safetySeq) { return; }
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
        if (my != safetySeq) { return; }        // IO 期间又有新输入 → 放弃本次，避免旧结果覆盖新结果
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

            ConfigHost.Children.Add(Section("启动器 config.json · 世界列表（" + Env.Describe(FsRoot) + "）"));
            ConfigHost.Children.Add(new TextBlock { Text = "⚠ 这些改动【在下次整链重置时生效】（重置会重新生成源地图并按此列表切分）", FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#E3B341")) });
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
            var btnAdd = new Button { Content = "+ 添加世界", FontSize = 12, Padding = new Avalonia.Thickness(12, 4), CornerRadius = new Avalonia.CornerRadius(9) };
            var btnDel = new Button { Content = "− 删除最后一个", FontSize = 12, Padding = new Avalonia.Thickness(12, 4), CornerRadius = new Avalonia.CornerRadius(9) };
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
            safeText = new TextBlock { Text = "—", FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            safeBar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 10, CornerRadius = new Avalonia.CornerRadius(5), Margin = new Avalonia.Thickness(0, 6, 0, 0) };
            ConfigHost.Children.Add(safeText);
            ConfigHost.Children.Add(safeBar);
            safeTip = new TextBlock { Text = "", FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#E3B341")), TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 6, 0, 0) };
            ConfigHost.Children.Add(safeTip);
            ConfigHost.Children.Add(new TextBlock { Text = "安全上限来自实测：12000×2400(2880万) 可用，12800×2400(3072万) OOM；建议留 10% 余量", FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#848D9A")), Margin = new Avalonia.Thickness(0, 4, 0, 0) });

            // EdgeStitch（缺失时优雅提示，不报错）
            ConfigHost.Children.Add(Section("EdgeStitch · 世界链与边缘触发（可选）"));
            if (!await Task.Run(() => FsExists(cfgEdgePath)))
            {
                ConfigHost.Children.Add(new TextBlock { Text = "未找到 EdgeStitch 配置（未安装该插件）—— 此区跳过，本管理器仍可正常管理多世界", FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#848D9A")), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
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
                // ★ 说明：这一项写进 EdgeStitch.json，由【服务端插件】读取（决定世界边缘传送是否成环）。
                //   管理器自己的地图/拼接预览始终按条带渲染，不体现成环 —— 不是没生效，是作用域不在这里。
                Num("首尾相连成环(true/false):", "首尾相连成环", out var tL); pendLoop = tL;
                pendDelta!.TextChanged += (_, _) => UpdateSafety();
                if (cfgEdge?["陆地边缘"] is JsonObject le)
                {
                    ConfigHost.Children.Add(new TextBlock { Text = "陆地边缘（西 / 东，留空=无边缘；41 与 宽-42 是常用值）", FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#848D9A")), Margin = new Avalonia.Thickness(0, 10, 0, 0) });
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
            // ★ 远端模式下 cfgLauncherPath 是【远端路径】，而 File.Copy 只认本机 → 原来这里抛异常被 catch
            //   吞成「保存失败」，并且整段后续写入（含 EdgeStitch）全被跳过：配置实际没保存。
            //   统一走后端（本机直读 / 远端 ssh），并整体放到后台线程（顺带消掉 UI 线程的一趟 ssh）。
            await Task.Run(() =>
            {
                FsWriteText(cfgLauncherPath + "." + stamp + ".bak", FsReadText(cfgLauncherPath));
                if (FsExists(cfgEdgePath)) { FsWriteText(cfgEdgePath + "." + stamp + ".bak", FsReadText(cfgEdgePath)); }
            });

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
                // ★ 原来用 (… as JsonObject)! ：若 "launcher" 是数组/字符串，as 得 null，! 不改变运行时行为 → NRE
                if (cfgLauncher["launcher"] is not JsonObject lo) { lo = new JsonObject(); cfgLauncher["launcher"] = lo; }
                lo["autoStartServers"] = arr;
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
        // ★ 编辑器里有未保存改动时，拦截一切离开"编辑页"的导航（否则切页面会丢编辑内容）
        if (which != "edit" && editorDirty && !editorLeaveOk)
        {
            EditHint.Text = "有未保存的修改 —— 请先点「保存 (Ctrl+S)」；若确实要放弃，请再点一次左侧目标页面。";
            ERR("编辑器里有未保存的修改：已拦截切页。再点一次左侧目标页面即可放弃修改并离开。");
            editorLeaveOk = true;                 // 再点一次就放行（与本程序其它确认操作一致的两步确认风格）
            return;
        }
        editorLeaveOk = false;
        EditView.IsVisible = which == "edit";
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



    private void OnFileCancelTransfer(object? sender, RoutedEventArgs e) { CancelTransfer(); ERR("已请求取消传输…"); }

    // ===== 凭据加密（DPAPI）：对标 MCSManager CVE-2025-50691 —— 别把 token 明文写在本地文件里 =====
    /// <summary>用当前 Windows 用户的 DPAPI 加密（只有本机本用户能解开）。实现已搬到 Servers\SecretBox，这里保留薄封装。</summary>
    private static string DpapiProtect(string plain) => SecretBox.Protect(plain);

    private static string DpapiUnprotect(string b64) => SecretBox.Unprotect(b64);

    // ===== 传输进度与取消（对标面板的上传进度条；我们的链路只有 1.3~2.4 MB/s，长传输没有进度体验很差）=====
    public static Action<string>? TransferStatus;      // 由管理器接到状态栏（后台线程调用，内部会 Post 到 UI）

    /// <summary>静默上下文（如 static 的 SaveConn）往当前服务器终端写一行的出口；断线时什么都不做。</summary>
    public static Action<string, string>? TerminalSink;
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
                // ★ 这里在 UI 线程上（DownloadFileAsync 的第一个 await 之前）→ 远端会先冻一趟 ssh。丢到后台。
                string dirOfFull = Path.GetDirectoryName(full) ?? fileCwd;
                string nameOfFull = Path.GetFileName(full);
                long remoteSize = await Task.Run(() => FsList(dirOfFull)
                    .Where(e => !e.IsDir && e.Name == nameOfFull).Select(e => e.Size).FirstOrDefault());
                knownSize = remoteSize;
                if (File.Exists(local) && remoteSize > 0 && remoteSize == new FileInfo(local).Length)
                {
                    ERR("远端与本机版本一致，已跳过下载: " + local);
                    return;
                }
            }
            ERR("正在下载 " + name + " …（进度见下方状态栏，可随时取消）");
            bool ok = await Task.Run(() => FileBackend.Download(full, local, knownSize));
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
            bool ok = await Task.Run(() => FileBackend.Upload(local, remote));
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
            FilePathText.Text = (FileBackend.IsRemote ? "远端 " + root2 : root2) + "\\" + rel;
            // ★ 远端模式下 FsList 是 SSH 调用，必须离开 UI 线程，否则窗口会「未响应」
            var entries = await Task.Run(() => FsList(fileCwd));
            PluginList.Children.Clear();   // 数据到手才替换
            if (entries.Count == 0)
            {
                // ★ R9：列表为空时必须说清【原因】—— 否则"令牌错/响应畸形"看起来就跟"目录本来就是空的"一样
                string why = AgentClient.LastError;
                if (!await Task.Run(() => FsExists(fileCwd)))
                {
                    if (why.Length > 0) { ERR("读取 " + fileCwd + " 失败：" + why); }
                    else { fileCwd = root2; return; }   // 目录真的不存在 → 退回根目录
                }
                else if (why.Length > 0) { ERR("文件列表为空（agent 未返回条目）：" + why); }
            }
            var dirs = entries.Where(e => e.IsDir).OrderBy(e => e.Name).ToList();
            var files = entries.Where(e => !e.IsDir).OrderBy(e => e.Name).ToList();
            // ★ K6：行数封顶 —— 大目录逐行建 5 个控件会卡住列表（无虚拟化）。
            //   但必须在标题里【明确标注】被截断，绝不能悄悄少显示（那会让人以为文件丢了）。
            const int MaxRows = 400;
            int totalRows = dirs.Count + files.Count;
            string truncNote = "";
            if (totalRows > MaxRows)
            {
                int keepDirs = Math.Min(dirs.Count, MaxRows);
                int keepFiles = Math.Max(0, MaxRows - keepDirs);
                dirs = dirs.Take(keepDirs).ToList();
                files = files.Take(keepFiles).ToList();
                truncNote = $"（共 {totalRows} 项，此处仅显示前 {MaxRows} 项）";
            }
            FileInfo.Text = $"{dirs.Count} 个文件夹 · {files.Count} 个文件" + truncNote;

            // 大小显示：小于 1KB 显示字节（原来整数除法把 <1KB 全显示成 0 KB）
            static string SizeText(long b)
            {
                if (b < 1024) { return b + " B"; }
                if (b < 1024 * 1024) { return (b / 1024.0).ToString("0.#") + " KB"; }
                return (b / 1048576.0).ToString("0.#") + " MB";
            }

            void Row(string icon, string name, string sizeText, string timeText, bool isDir, string full, bool? enabled, long sizeBytes = -1)
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
                var timeTb = new TextBlock { Text = timeText, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#848D9A")), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                Avalonia.Controls.Grid.SetColumn(timeTb, 2);
                sp.Children.Add(timeTb);
                if (enabled is bool en)
                {
                    var cb = new CheckBox { IsChecked = en, FontSize = 12, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
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
                            // ★ 原来两次 FsExists 都在 UI 线程上（远端每趟 ssh 约 1.6 秒 → 一次点击冻约 3.2 秒）。
                            //   一次性丢到后台查，判断与改名都不再占用 UI 线程。
                            bool offExists = false, onExists = false;
                            await Task.Run(() => { offExists = FsExists(off); onExists = FsExists(on); });
                            if (want)
                            {
                                if (!offExists) { ERR("找不到 " + System.IO.Path.GetFileName(off)); }
                                else if (onExists) { ERR($"已存在 {System.IO.Path.GetFileName(on)}，为免覆盖已中止（请先手工处理）"); }
                                else { await Task.Run(() => FsRename(off, on)); ERR($"已启用 {baseName} —— 重启服务端后生效"); }
                            }
                            else
                            {
                                if (!onExists) { ERR("找不到 " + System.IO.Path.GetFileName(on)); }
                                else if (offExists) { ERR($"已存在 {System.IO.Path.GetFileName(off)}，为免覆盖已中止（请先手工处理）"); }
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
                    var dl = new Button { Content = "下载", FontSize = 11, Padding = new Avalonia.Thickness(7, 1), CornerRadius = new Avalonia.CornerRadius(6) };
                    dl.Click += async (_, _) => await DownloadFileAsync(full, name);
                    actSp.Children.Add(dl);
                }
                var del = new Button
                {
                    Content = "删除", FontSize = 11, Padding = new Avalonia.Thickness(7, 1), CornerRadius = new Avalonia.CornerRadius(6),
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
                else if (sizeBytes >= 0)
                {
                    // ★ 点文件名 → 进在线编辑器（类 MCSManager 的用法）。是否可编辑由编辑器自己判定并给出原因。
                    tb.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
                    tb.PointerPressed += (_, _) => OpenInEditor(full, sizeBytes, name);
                }
                rowWrap.Child = sp;
                // ★ v5：插件注册的右键动作（没有插件注册时返回 null，原有交互完全不变）
                try { var pm = BuildFilePluginMenu(full, isDir, sizeBytes); if (pm is not null) { rowWrap.ContextMenu = pm; } } catch { }
                PluginList.Children.Add(rowWrap);
            }

            // ★ P2：空态（原来空目录就是一片空白，看不出是"空"还是"没加载出来"）
            if (dirs.Count == 0 && files.Count == 0)
            {
                var empty = new Border { Padding = new Avalonia.Thickness(14, 20, 14, 20) };
                empty.Child = new TextBlock
                {
                    Text = "这个目录是空的。",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.Parse("#848D9A")),
                };
                PluginList.Children.Add(empty);
            }

            foreach (var d in dirs) { Row("\uE8B7", d.Name, "", d.Mtime.ToString("MM-dd HH:mm"), true, Path.Combine(fileCwd, d.Name), null); }
            foreach (var x in files)
            {
                bool isDll = x.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !x.Name.StartsWith("TShockAPI", StringComparison.OrdinalIgnoreCase);
                bool isOff = x.Name.EndsWith(".dll.disabled", StringComparison.OrdinalIgnoreCase) && !x.Name.StartsWith("TShockAPI", StringComparison.OrdinalIgnoreCase);
                bool? en = (isDll || isOff) ? isDll : (bool?)null;
                Row("\uE7C3", x.Name, SizeText(x.Size), x.Mtime.ToString("MM-dd HH:mm") + (isOff ? " · 已停用" : ""), false, Path.Combine(fileCwd, x.Name), en, x.Size);
            }
            // ★ 同世界链：这里是【异步读取完成】路径，原来又调 ShowView("plugins")，
            //   用户在读取期间切走会被抢回来。视图切换只由点击入口（OnShowPlugins）负责。
        }
        catch (Exception ex) { ERR("读取目录失败: " + ex.Message); }
    }

    // ===== 地图预览（视图切换 + 单世界切换 + 缩放）=====
    private double mapZoom = 1.0;
   private static string MapCacheDir => Path.Combine(AppContext.BaseDirectory, "map-cache");

    /// <summary>★ 一律不用 %TEMP%：这台机器上它不可写（实测 UnauthorizedAccessException），
    ///   而且临时目录容易被清理系统清掉。统一放在程序目录下。</summary>
    private static string ScratchDir => Path.Combine(AppContext.BaseDirectory, "tmp");
    private static string WorldCacheDir => Path.Combine(AppContext.BaseDirectory, "world-cache");
    private static string MapRemoteDir => Path.Combine(RemoteDir, "GenerateMap", "Images");
    private static string MapLocalDir => Path.Combine(LocalDir, "GenerateMap", "Images");
    /// <summary>显示/更新卡片下方的加载进度条（渲染在后台线程，进度回调可能来自非 UI 线程）。</summary>
    private void ShowMapProgress(bool visible, double value)
    {
        if (MapProgress is null) { return; }
        if (Dispatcher.UIThread.CheckAccess())
        {
            MapProgress.IsVisible = visible;
            MapProgress.Value = Math.Clamp(value, 0, 1);
        }
        else
        {
            Dispatcher.UIThread.Post(() => { MapProgress.IsVisible = visible; MapProgress.Value = Math.Clamp(value, 0, 1); });
        }
    }

    /// <summary>
    /// SKBitmap → Avalonia 位图：直接拷像素缓冲。
    /// ★ 不走 PNG 编解码：全分辨率拼接图 PNG 编码要 2.9 秒，解码回位图再花一份 115MB，
    ///   而这里只是 115MB 的一次内存拷贝。
    /// </summary>
    private static unsafe Avalonia.Media.Imaging.WriteableBitmap ToWriteableBitmap(SKBitmap src)
    {
        var wb = new Avalonia.Media.Imaging.WriteableBitmap(
            new Avalonia.PixelSize(src.Width, src.Height),
            new Avalonia.Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Premul);
        using (var fb = wb.Lock())
        {
            byte* dst = (byte*)fb.Address;
            byte* sp = (byte*)src.GetPixels().ToPointer();
            int srcStride = src.RowBytes;
            long copy = Math.Min((long)fb.RowBytes, Math.Min((long)srcStride, (long)src.Width * 4));
            for (int y = 0; y < src.Height; y++)
            {
                Buffer.MemoryCopy(sp + (long)y * srcStride, dst + (long)y * fb.RowBytes, fb.RowBytes, copy);
            }
        }
        return wb;
    }

    private async void OnShowMap(object? sender, RoutedEventArgs e)
    {
        // ★ 地图由管理器本体直接解析 .wld 渲染（见 Maps\WorldMapRenderer.cs）。
        // ★★ 先把页面切过去再异步填数据：远端模式下建列表要 4 次 ssh 往返（每次约 1.6 s），
        //    原来在 UI 线程上同步做，点了就像卡死（实测用户反馈"卡死半天"就是这个）。
        ShowView("map");
        InitMapLayerPresets();
        if (MapGotoBox is not null && !mapGotoHooked)
        {
            mapGotoHooked = true;
            MapGotoBox.KeyDown += (_, ke) =>
            {
                if (ke.Key is Avalonia.Input.Key.Enter or Avalonia.Input.Key.Return)
                {
                    OnMapGoto(null, new RoutedEventArgs());
                    ke.Handled = true;
                }
            };
        }
        MapInfo.Text = "正在读取世界列表…";
        await RefreshMapPickerAsync();
        await LoadMap();
        Dispatcher.UIThread.Post(() => OnMapFit(null, new RoutedEventArgs()), DispatcherPriority.Background);
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        t.Tick += (_, _) => { t.Stop(); OnMapFit(null, new RoutedEventArgs()); };
        t.Start();
    }

    private async void OnRefreshMap(object? sender, RoutedEventArgs e)
    {
        forceWorldReload = true;          // 忽略远端世界缓存
        worldInfoAtMs = 0;                // 目录快照也失效（换过世界要重新列）
        WorldMapRenderer.DropCache();     // 解析缓存也丢掉（按 path+mtime+size，正常情况本就会失效，这里是双保险）
        try { FsInvalidatePath(WorldsDir); } catch { }   // 目录列表缓存也失效：换了新世界名要能立刻出现在下拉里
        try { await RefreshMapPickerAsync(); await LoadMap(); }
        finally { forceWorldReload = false; }
    }

    /// <summary>服务端世界存档目录（唯一地图来源，不读别处）。</summary>
    private static string WorldsDir => Path.Combine(Env.FileRoot, "lib", "Saves", "Worlds");

    // 下拉里几组条目的前缀标记（用前缀而不是自定义对象，是因为 ItemsSource 用的是字符串）
    private const string VirtualMarker = "假世界：";      // 尘歌壶等虚拟世界的存档
    private const string BaselineMarker = "世界基线：";   // _vwc_templates 下的整世界模板

    /// <summary>服务端真正加载的世界（以 config.json 的 autoStartServers 为准；读不到就退回世界列表）。</summary>
    private List<string> ServerWorldNames(IReadOnlyList<string>? fallbackNames = null)
    {
        var names = new List<string>();
        try
        {
            string txt = FsReadText(Path.Combine(FsRoot, "config", "config.json"));
            if (JsonNode.Parse(txt) is JsonObject o && o["launcher"]?["autoStartServers"] is JsonArray arr)
            {
                foreach (var it in arr)
                {
                    string n = (it?["worldName"]?.ToString() ?? it?["name"]?.ToString() ?? "").Trim();
                    if (n.Length > 0 && !names.Contains(n, StringComparer.OrdinalIgnoreCase)) { names.Add(n); }
                }
            }
        }
        catch { }
        if (names.Count == 0)
        {
            // ★ 不能用 rows（UI 绑定的 ObservableCollection）：本方法会被 RefreshMapPickerAsync 在
            //   后台线程调用，而 2 秒定时器会在 UI 线程 Clear/Add 它 → 数据竞态。
            //   调用方在 UI 线程先做快照传进来；没传就退化为空（读不到 config 时才需要它）。
            foreach (string n in fallbackNames ?? []) { if (n != "全部世界") { names.Add(n); } }
        }
        return names;
    }

    private static List<string> WorldFilesIn(string subDir, Func<string, bool>? filter = null)
        => FsList(Path.Combine(WorldsDir, subDir))
            .Where(e => !e.IsDir && e.Name.EndsWith(".wld", StringComparison.OrdinalIgnoreCase))
            .Where(e => e.Size > 1024)          // 0 字节/截断的存档列出来也渲染不了（远端就有一个），直接过滤

            .Where(e => filter is null || filter(e.Name))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => Path.GetFileNameWithoutExtension(e.Name))
            .ToList();

    /// <summary>
    /// 重建地图下拉列表。★ 数据全部在**后台线程**取：远端模式下 ServerWorldNames 要读一次 config.json、
    /// 再列三次目录，每次都是一趟 ssh（约 1.6 s）—— 放 UI 线程上就是"点一下卡死半天"。
    /// ★ 条目 = 自动启动的世界 ∪ **磁盘上所有 .wld**：只按 config.json 的 autoStartServers 列，
    ///   会把"存在但没自动启动"的世界整个藏掉（用户反馈的"地图列表其他地图呢"）。
    /// </summary>
    /// <summary>世界目录条目缓存（远端每趟往返约 200ms；下拉与"取大小/mtime"共用一次列表）。</summary>
    private static readonly Dictionary<string, (long Size, DateTime Mtime)> worldInfoCache = new(StringComparer.OrdinalIgnoreCase);
    private static long worldInfoAtMs;
    private static string worldInfoKey = "";

    // 世界基线模板列表（5 分钟缓存）
    private static readonly List<string> tplCache = [];
    private static long tplCacheAtMs;
    private static string tplCacheKey = "";

    /// <summary>拉一次世界目录（带 60 秒缓存），返回 文件名→(大小, mtime)。</summary>
    private static Dictionary<string, (long Size, DateTime Mtime)> WorldInfoSnapshot(bool force = false)
    {
        string key = WorldsDir;
        if (!force && worldInfoKey == key && Environment.TickCount64 - worldInfoAtMs < 60000 && worldInfoCache.Count > 0)
        {
            return worldInfoCache;
        }
        try
        {
            worldInfoCache.Clear();
            foreach (var e in FsList(key))
            {
                if (e.IsDir) { continue; }
                if (!e.Name.EndsWith(".wld", StringComparison.OrdinalIgnoreCase)) { continue; }
                worldInfoCache[e.Name] = (e.Size, e.Mtime);
            }
            worldInfoKey = key;
            worldInfoAtMs = Environment.TickCount64;
        }
        catch { }
        return worldInfoCache;
    }

    private async Task RefreshMapPickerAsync()
    {
        if (MapPicker is null) { return; }
        string sel = MapPicker.SelectedItem as string ?? "";
        // ★ 在 UI 线程先快照世界名：ServerWorldNames 原本会枚举 UI 绑定的 rows，放到后台线程就是竞态
        List<string> nameSnap = rows.Where(r => r.Name != "全部世界").Select(r => r.Name).ToList();
        List<string> items = await Task.Run(() =>
        {
            var list = new List<string> { "全部（拼接图）" };
            void Add(string s) { if (s.Length > 0 && !list.Contains(s, StringComparer.OrdinalIgnoreCase)) { list.Add(s); } }
            foreach (var n in ServerWorldNames(nameSnap)) { Add(n); }
            foreach (var n in WorldFilesIn(""))
            {
                // 尘歌壶等虚拟世界单独标注，其余按普通世界列（磁盘上有就列出来）
                Add(n.StartsWith("尘歌壶_", StringComparison.Ordinal) ? VirtualMarker + n : n);
            }
            // ★ 世界基线模板目录很少变动：缓存 5 分钟，省掉每次重建下拉的一趟往返（远端 ~200ms）
            AddTemplates();
            void AddTemplates()
            {
                if (tplCacheKey == WorldsDir && Environment.TickCount64 - tplCacheAtMs < 300000 && tplCache.Count > 0)
                {
                    foreach (var n in tplCache) { Add(BaselineMarker + n); }
                    return;
                }
                var names = WorldFilesIn("_vwc_templates");
                tplCache.Clear(); tplCache.AddRange(names);
                tplCacheKey = WorldsDir; tplCacheAtMs = Environment.TickCount64;
                foreach (var n in tplCache) { Add(BaselineMarker + n); }
            }
            return list;
        });
        MapPicker.ItemsSource = items;
        MapPicker.SelectedIndex = items.Count == 0 ? -1 : Math.Max(0, items.IndexOf(sel));
    }

    /// <summary>
    /// 取世界文件：只从服务端世界目录取；远端模式才落一份本地副本供解析。rel 是相对 worlds 目录的路径。
    /// ★ 两个关键点（实测"点地图卡半天、进度条不动"就是这里）：
    ///   1) **先比大小，缓存一致就不下载** —— 否则每次开地图都把 24~72 MB 重拉一遍（按 1.3 MB/s 要几十秒）；
    ///   2) 下载时把进度/状态报出来 —— 原来不传 progress，进度条一直停在 0，看着像卡死。
    /// </summary>
    private static string? ResolveWorldFile(string rel, Action<string>? status = null)
    {
        string full = Path.Combine(WorldsDir, rel);
        if (!Env.IsRemote) { return File.Exists(full) ? full : null; }
        string cache = Path.Combine(WorldCacheDir, rel.Replace('\\', '_').Replace('/', '_'));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            long remoteSize = 0;
            DateTime remoteMtime = default;
            try
            {
                // ★ 从"世界目录快照"里取：原来每个世界都单发一次列目录请求（远端每趟 ~200ms），
                //   拼接 3 个世界就是 3 趟 = 600ms 白等。
                string name = Path.GetFileName(full);
                var snap = WorldInfoSnapshot(forceWorldReload);
                if (snap.TryGetValue(name, out var hit)) { remoteSize = hit.Size; remoteMtime = hit.Mtime; }
                else
                {
                    string dir = Path.GetDirectoryName(full) ?? "";
                    var one = FsList(dir).FirstOrDefault(e => !e.IsDir && e.Name == name);
                    if (one is not null) { remoteSize = one.Size; remoteMtime = one.Mtime; }
                }
            }
            catch { }
            // ★ 缓存判定用"大小 + 远端修改时间 + 本地留存时间"三重比对：
            //   原来只比大小 —— 换了一张同尺寸的世界（或同名覆盖）会一直吃旧缓存，看起来"地图没变"。
            //   forceWorldReload（用户点"刷新"时置位）直接跳过缓存，强制重下。
            bool cacheFresh = false;
            if (File.Exists(cache) && remoteSize > 0 && !forceWorldReload)
            {
                var ci = new FileInfo(cache);
                bool sizeOk = ci.Length == remoteSize;
                bool timeOk = remoteMtime == default || Math.Abs((ci.LastWriteTimeUtc - remoteMtime.ToUniversalTime()).TotalSeconds) < 2;
                cacheFresh = sizeOk && timeOk;
            }
            if (cacheFresh)
            {
                status?.Invoke(Path.GetFileName(rel) + "：本地缓存已是最新，跳过下载");
                return cache;
            }
            if (File.Exists(cache) && forceWorldReload) { status?.Invoke(Path.GetFileName(rel) + "：强制重新下载（忽略缓存）"); }
            status?.Invoke(Path.GetFileName(rel) + "：正在从远端下载" + (remoteSize > 0 ? "（" + (remoteSize / 1048576.0).ToString("F1") + " MB）" : "") + "…");
            if (FileBackend.Download(full, cache, remoteSize, s => status?.Invoke(Path.GetFileName(rel) + "：" + s)) || File.Exists(cache))
            {
                // ★ 下载完把本地缓存 mtime 对齐远端：否则 mtime 比对永远不通过，每次开地图都要重下一遍
                if (remoteMtime != default)
                {
                    try { File.SetLastWriteTimeUtc(cache, remoteMtime.ToUniversalTime()); } catch { }
                }
                return cache; }
        }
        catch { }
        return null;
    }

    // ===== 地图页贡献点（插件 API v4）=====
    private readonly List<(Button Btn, Func<MapViewState, bool>? Enabled)> mapPluginButtons = [];
    private MapViewState mapViewState = new("—", false, false, false, 0, 0, "");
    private event Action? MapViewChanged;

    /// <summary>插件拿到的那份宿主接口。所有成员都只暴露纯数据，不泄露控件类型。</summary>
    private sealed class MapContributionHost(MainWindow w) : IMapContributionHost
    {
        public Action AddToolbarButton(string text, string tooltip, Func<Task> onClick, Func<MapViewState, bool>? enabled = null)
        {
            Button? btn = null;
            void Add()
            {
                btn = new Button
                {
                    Content = text,
                    FontSize = 12,
                    Padding = new Avalonia.Thickness(10, 5),
                    CornerRadius = new Avalonia.CornerRadius(9),
                };
                if (!string.IsNullOrWhiteSpace(tooltip)) { ToolTip.SetTip(btn, tooltip); }
                btn.Click += async (_, _) =>
                {
                    try { await onClick(); }
                    catch (Exception ex) { w.SetStatusLine("插件按钮执行失败：" + ex.Message); }
                };
                if (btn.Margin == default) { btn.Margin = new Avalonia.Thickness(0, 0, 6, 6); }   // 与宿主控件保持一致的间距
                w.MapPluginBar?.Children.Add(btn);
                w.mapPluginButtons.Add((btn, enabled));
                w.RefreshMapPluginButtons();
            }
            if (Dispatcher.UIThread.CheckAccess()) { Add(); } else { Dispatcher.UIThread.Post(Add); }
            return () => Dispatcher.UIThread.Post(() =>
            {
                if (btn is null) { return; }
                w.MapPluginBar?.Children.Remove(btn);
                w.mapPluginButtons.RemoveAll(x => ReferenceEquals(x.Btn, btn));
            });
        }

        public Action AddToolbarControl(Control control)
        {
            void Add()
            {
                if (control.Margin == default) { control.Margin = new Avalonia.Thickness(0, 0, 6, 6); }
                w.MapPluginBar?.Children.Add(control);
            }
            if (Dispatcher.UIThread.CheckAccess()) { Add(); } else { Dispatcher.UIThread.Post(Add); }
            return () => Dispatcher.UIThread.Post(() => w.MapPluginBar?.Children.Remove(control));
        }

        public bool SelectionMode
        {
            get => w.mapSelectMode;
            set
            {
                void Apply() => w.SetSelectionMode(value);
                if (Dispatcher.UIThread.CheckAccess()) { Apply(); } else { Dispatcher.UIThread.Post(Apply); }
            }
        }

        public Task<bool> ShowRegionTemplateAsync(string templateName)
            => Dispatcher.UIThread.CheckAccess()
                ? w.ShowTemplateAsync(templateName)
                : Dispatcher.UIThread.InvokeAsync(() => w.ShowTemplateAsync(templateName));

        public MapViewState CurrentView => w.mapViewState;

        public MapSelection? CurrentSelection => w.hasSelection
            ? new MapSelection(w.selX0, w.selY0, w.selX1 - w.selX0 + 1, w.selY1 - w.selY0 + 1)
            : null;

        public event Action? Changed
        {
            add { w.MapViewChanged += value; }
            remove { w.MapViewChanged -= value; }
        }

        public void SetStatus(string text) => w.SetStatusLine(text);
    }

    /// <summary>视图/选区变化 → 通知插件，并按各自的条件刷新按钮可用性。</summary>
    private void RaiseMapViewChanged()
    {
        try { MapViewChanged?.Invoke(); } catch { }
        RefreshMapPluginButtons();
    }

    private void RefreshMapPluginButtons()
    {
        foreach ((Button btn, Func<MapViewState, bool>? enabled) in mapPluginButtons)
        {
            try { btn.IsEnabled = enabled is null || enabled(mapViewState); } catch { btn.IsEnabled = true; }
        }
    }

    private void ClearMapPluginBar()
    {
        // ★ 只移除【插件加进来的】控件。MapPluginBar（MainWindow.axaml:345）里还有宿主自己的
        //   MapPicker + 6 个内置按钮（适应/1:1/±/刷新），直接 Children.Clear() 会把它们一起抹掉 ——
        //   实测后果：插件一加载（启动即执行），地图页就再也选不了世界，且 MapPicker 停在默认项
        //   导致"框选"永远被拒。
        foreach ((Button btn, _) in mapPluginButtons.ToArray())
        {
            try { MapPluginBar?.Children.Remove(btn); } catch { }
        }
        mapPluginButtons.Clear();
        MapViewChanged = null;      // ★ 一并清掉插件的事件订阅，否则旧插件实例被永久引用（热重载 ALC 收不回）
    }

    // ===== v5：文件页插件动作 =====
    // 与地图页同一套模式：宿主持有界面、插件只"挂"动作。
    // ★ 重载时必须整体清空：这些委托指向插件实例，留着会把插件的可回收 ALC 永久钉住（热重载收不回）。
    private sealed record FileActionReg(string Text, Func<FileActionContext, Task> OnClick, Func<FileActionContext, bool>? Visible);
    private readonly List<FileActionReg> fileActionRegs = [];

    private void ClearFilePluginActions() => fileActionRegs.Clear();

    /// <summary>插件加载完成后调用：让实现 IFileActionPlugin 的插件往文件页注册右键动作。</summary>
    private void ContributeFileActionsToPlugins()
    {
        ClearFilePluginActions();
        if (pluginHost is null) { return; }
        var host = new FileActionHost(this);
        foreach (LoadedPlugin p in pluginHost.Plugins.ToArray())
        {
            if (p.Instance is not IFileActionPlugin fp) { continue; }
            try
            {
                fp.ContributeFileActions(host);
                Term($"[插件] {p.Name} 已向文件页注册右键动作", "#9BA3AE");
            }
            catch (Exception ex)
            {
                Term($"[插件] {p.Name} 文件页注册失败: {ex.Message}", "#FF7B72");
            }
        }
    }

    private sealed class FileActionHost(MainWindow w) : IFileActionHost
    {
        public Action RegisterFileAction(string text, Func<FileActionContext, Task> onClick, Func<FileActionContext, bool>? visible = null)
        {
            var reg = new FileActionReg(text, onClick, visible);
            w.fileActionRegs.Add(reg);
            return () => w.fileActionRegs.Remove(reg);
        }
    }

    /// <summary>为文件页某一行构造插件右键菜单；没有任何插件动作时返回 null（原有交互完全不变）。</summary>
    private ContextMenu? BuildFilePluginMenu(string full, bool isDir, long size)
    {
        if (fileActionRegs.Count == 0) { return null; }
        string rel = "";
        string dirRel = "";
        try
        {
            rel = Path.GetRelativePath(FsRoot, full).Replace('\\', '/');
            dirRel = Path.GetDirectoryName(rel)?.Replace('\\', '/') ?? "";
        }
        catch { }
        var info = new FileActionContext(rel, full, dirRel, isDir, Math.Max(0, size));
        var items = new List<MenuItem>();
        foreach (var reg in fileActionRegs.ToArray())
        {
            try { if (reg.Visible is not null && !reg.Visible(info)) { continue; } } catch { continue; }
            var mi = new MenuItem { Header = reg.Text };
            mi.Click += async (_, _) =>
            {
                try { await reg.OnClick(info); }
                catch (Exception ex) { ERR($"[插件动作] {reg.Text} 失败: {ex.Message}"); }
            };
            items.Add(mi);
        }
        if (items.Count == 0) { return null; }
        return new ContextMenu { ItemsSource = items };
    }
    /// <summary>插件加载完成后调用：让实现 IMapContributionPlugin 的插件往地图页注入界面。</summary>
    private void ContributeMapToPlugins()
    {
        ClearMapPluginBar();
        if (pluginHost is null) { return; }
        var host = new MapContributionHost(this);
        foreach (LoadedPlugin p in pluginHost.Plugins.ToArray())
        {
            if (p.Instance is not IMapContributionPlugin mp) { continue; }
            try
            {
                mp.ContributeMap(host);
                Term($"[插件] {p.Name} 已向地图页注入界面", "#9BA3AE");
            }
            catch (Exception ex)
            {
                Term($"[插件] {p.Name} 地图页注入失败: {ex.Message}", "#FF7B72");
            }
        }
        RefreshMapPluginButtons();
    }

    /// <summary>服务端三联图的拼接规格（config\EdgeStitch\EdgeStitch.json）。</summary>
    private StitchConfig.Plan? ReadStitchPlan()
    {
        try { return StitchConfig.Parse(FsReadText(Path.Combine(FsRoot, "config", "EdgeStitch", "EdgeStitch.json"))); }
        catch { return null; }
    }

    /// <summary>取区块模板文件（只从服务端 _utpl_templates 取）；远端模式拉一份本地副本。
    /// ★ 暂存不用 %TEMP%（这台机器上它不可写），放程序目录下。</summary>
    private static async Task<string?> ResolveTemplateFileAsync(string name)
    {
        string full = Path.Combine(WorldsDir, "_utpl_templates", name + UtplCodec.Extension);
        if (!Env.IsRemote) { return File.Exists(full) ? full : null; }
        string staging = Path.Combine(AppContext.BaseDirectory, "utpl-cache", name + UtplCodec.Extension);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
            // ★ 与 ResolveWorldFile 一样先比大小：原来是 Download(...) || File.Exists(...)，|| 左侧总会先执行
            //   → 每次预览都重下一遍（远端一趟 ssh + 传输）。现在大小一致就直接用本地副本。
            long remote = 0;
            try
            {
                string d = Path.GetDirectoryName(full) ?? WorldsDir;
                string n = Path.GetFileName(full);
                remote = await Task.Run(() => FsList(d).Where(e => !e.IsDir && e.Name == n).Select(e => e.Size).FirstOrDefault());
            }
            catch { }
            if (File.Exists(staging) && remote > 0 && new FileInfo(staging).Length == remote) { return staging; }
            if (await Task.Run(() => FileBackend.Download(full, staging)) || File.Exists(staging)) { return staging; }
        }
        catch { }
        return null;
    }

    private async void OnMapPickChanged(object? sender, SelectionChangedEventArgs e)
    {
        await LoadMap();
        Dispatcher.UIThread.Post(() => OnMapFit(null, new RoutedEventArgs()), DispatcherPriority.Background);
    }

    /// <summary>渲染当前选中的世界（后台线程；同一个世界第二次打开时复用已解析结果）。</summary>
    private async Task LoadMap()
    {
        try
        {
            if (MapImage is null || MapInfo is null) { return; }
            string pick = MapPicker?.SelectedItem as string ?? "";
            if (pick.Length == 0) { MapInfo.Text = Env.Describe("世界目录") + " 里没有世界文件"; return; }

            bool stitch = pick.StartsWith("全部", StringComparison.Ordinal);
           string label = stitch ? "全部（拼接图）"
               : pick.StartsWith(VirtualMarker, StringComparison.Ordinal) ? pick[VirtualMarker.Length..]
                : pick.StartsWith(BaselineMarker, StringComparison.Ordinal) ? "世界基线 " + pick[BaselineMarker.Length..]
                : pick;

            // 单世界时先算好相对路径（纯字符串运算，不碰 IO）—— 导出模板要用；拼接图没有单一世界。
            // ★ 注意：只先放在局部变量里，**渲染成功后**才写进 lastWorldRel。
            //   否则加载失败时会出现"图上还是旧世界、导出却去裁新世界"的不一致。
            string relThisLoad = stitch ? ""
                : pick.StartsWith(VirtualMarker, StringComparison.Ordinal) ? pick[VirtualMarker.Length..] + ".wld"
                : pick.StartsWith(BaselineMarker, StringComparison.Ordinal) ? Path.Combine("_vwc_templates", pick[BaselineMarker.Length..] + ".wld")
                : pick + ".wld";

            mapLoadSeq++;                       // 新一轮加载：上一轮遗留的预览全部作废
            MapInfo.Text = label + " · 渲染中…";
            ShowMapProgress(true, 0);
            // ★ TEdit 的载入进度不是单调的（换阶段会从 0 重新报，实测拼接会出现 7 次回退），
            //   这里统一钳成单调递增。Progress<double> 的回调在 UI 线程 → lastProg 无需加锁。
            double lastProg = 0;
            IProgress<double> prog = new Progress<double>(p =>
            {
                lastProg = Math.Max(lastProg, p);
                ShowMapProgress(true, lastProg);
            });

            // 状态文字（下载/缓存命中/渲染阶段）——可能来自后台线程，统一在这里切回 UI 线程
            void MapStatus(string s)
            {
                if (Dispatcher.UIThread.CheckAccess()) { MapInfo.Text = s; }
                else { Dispatcher.UIThread.Post(() => MapInfo.Text = s); }
            }

            var result = await Task.Run(() =>
            {
                if (!WorldMapRenderer.EnsureReady())
                {
                    return (Bmp: (SKBitmap?)null, Error: "初始化失败：" + WorldMapRenderer.Error, Stats: (MapRenderStats?)null);
                }

                // ---- 拼接图：三片本来是一张超大地图，按 EdgeStitch 的世界链与切片间距拼回去 ----
                if (stitch)
                {
                    StitchConfig.Plan? plan = ReadStitchPlan();
                    string[] chain = plan?.Chain ?? ServerWorldNames().ToArray();
                    if (chain.Length == 0) { return (Bmp: (SKBitmap?)null, Error: "读不到 EdgeStitch 世界链，也读不到服务端世界列表", Stats: (MapRenderStats?)null); }

                    int spacing = plan?.SpacingTiles ?? -1;
                    var slices = new List<StitchSlice>();
                    var missing = new List<string>();

                    // ★ 并行预取：实测 3 个世界串行 15.3s / 并行 8.3s（1.18 → 2.17 MB/s）。
                    //   单条 SSH 流的窗口把吞吐绑死了，多条并发才能把链路吃满。
                    //   远端档才有下载；本机档 ResolveWorldFile 只做存在性判断，并行也是无害的。
                    var resolvedPaths = new string?[chain.Length];
                    int finishedDl = 0;
                    Parallel.For(0, chain.Length, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
                    {
                        string? p = ResolveWorldFile(chain[i] + ".wld", s => MapStatus("并行下载（" + (Volatile.Read(ref finishedDl)) + "/" + chain.Length + " 完成）：" + s));
                        Interlocked.Increment(ref finishedDl);
                        resolvedPaths[i] = p;
                    });

                    for (int i = 0; i < chain.Length; i++)
                    {
                        string? p = resolvedPaths[i];
                        if (p is null) { missing.Add(chain[i]); continue; }
                        slices.Add(new StitchSlice(chain[i], p, spacing > 0 ? i * spacing : -1));
                    }
                    ShowMapProgress(true, 0.35);
                    if (missing.Count > 0) { return (Bmp: (SKBitmap?)null, Error: "缺少世界文件：" + string.Join("、", missing), Stats: (MapRenderStats?)null); }

                    // ★ 拼接也走全分辨率：3900×2+4200 = 12000 格宽，任何缩放都会糊掉细节
                    int totalSlices = Math.Max(1, slices.Count);
                    int mySeq = mapLoadSeq;
                    void OnSlice(SKBitmap canvas, int done, int total)
                    {
                        SKBitmap? prev = WorldMapRenderer.MakePreview(canvas, 2000);
                        if (prev is null) { return; }
                        Dispatcher.UIThread.Post(() =>
                        {
                            // ★ 这次加载已经出成品（或被新的加载取代）→ 丢掉这张迟到的预览，
                            //   否则预览会把成品覆盖回去（实测：成品 12000×2400 被 2000 宽的预览盖掉，缩放还停在预览的 57%）
                            if (mySeq != mapLoadSeq) { prev.Dispose(); return; }
                            try
                            {
                                mapPreviewShown = true;
                                using (prev) { PresentBitmap(prev, keepView: true); }
                                ShowMapProgress(true, 0.45 + 0.5 * done / (double)total);
                                SetStatusLine(label + " · 正在拼接（" + done + "/" + total + " 片已就绪，先看预览）…");
                            }
                            catch { }
                        });
                    }
                    SKBitmap? cv = WorldMapRenderer.RenderStitched(slices, 1, out MapRenderStats sst, out string serr, p => prog.Report(p), mapLayers, OnSlice);
                    if (cv is null) { return (Bmp: (SKBitmap?)null, Error: serr, Stats: (MapRenderStats?)null); }

                    // ★ 拼接会顺序载入 3 个世界（每个约 200MB 的 Tile[,]），而 GC 默认**不把大对象堆还给系统**。
                    //   实测（同一段代码）：不显式压缩时工作集停在 ~1029MB，普通 GC.Collect() 只降到 ~699MB，
                    //   Aggressive + compacting 才能回到 ~65MB。这里跑在后台线程上，不会卡 UI。
                    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                    return (Bmp: cv, Error: "", Stats: (MapRenderStats?)sst);
                }
                // ---- 单个世界（服务端世界 / 假世界 / 世界基线）----
                string rel = relThisLoad;
                ShowMapProgress(true, 0.05);
                string? path = ResolveWorldFile(rel, MapStatus);
                if (path is null) { return (Bmp: (SKBitmap?)null, Error: "找不到文件 " + rel, Stats: (MapRenderStats?)null); }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                // 单世界实测：载入约 68%、渲染约 32% —— 按 0.6/0.4 合成，让进度条在载入阶段也在动
                (TEdit.Terraria.World? w, string lerr) = WorldMapRenderer.LoadWorld(path, p => prog.Report(p * 0.60));
                if (w is null) { return (Bmp: (SKBitmap?)null, Error: "解析失败：" + lerr, Stats: (MapRenderStats?)null); }
                long loadMs = sw.ElapsedMilliseconds;
                // ★ 一律全分辨率（scale=1）：不缩放才是最清晰
                var swr = System.Diagnostics.Stopwatch.StartNew();
                SKBitmap bmp2 = WorldMapRenderer.Render(w, 1, p => prog.Report(0.60 + p * 0.40), mapLayers);
                long renderMs = swr.ElapsedMilliseconds;
                return (Bmp: bmp2, Error: "", Stats: (MapRenderStats?)new MapRenderStats(
                    w.TilesWide, w.TilesHigh, bmp2.Width, bmp2.Height,
                    w.Chests.Count, w.Signs.Count, w.TileEntities.Count, loadMs, renderMs, 0, 1,
                    w.SpawnX, w.SpawnY));
            });

            SKBitmap? outBmp = result.Bmp;
            if (outBmp is null)
            {
                ShowMapProgress(false, 0);
                MapInfo.Text = label + " · " + result.Error;
                mapViewState = new MapViewState(label, false, false, false, 0, 0, "");
                RaiseMapViewChanged();
                return;
            }
            mapIsStitch = stitch;
            mapSpawnX = stitch ? -1 : (result.Stats?.SpawnX ?? -1);
            mapSpawnY = stitch ? -1 : (result.Stats?.SpawnY ?? -1);
            lastMapPick = pick;
            mapLoadSeq++;                       // 作废所有还在队列里的预览
            bool keepView = mapPreviewShown && mapZoom > 0;
            mapPreviewShown = false;
            using (outBmp) { PresentBitmap(outBmp, keepView); }   // 换图统一走这里（含布局尺寸与适应窗口）
            try { OnMapOverlayToggle(null, new RoutedEventArgs()); } catch { }
            ShowMapProgress(false, 1);
            lastWorldRel = relThisLoad;      // 只有成功渲染才更新（导出的目标世界）
           mapInfoHead = label + " · " + (result.Stats?.Describe() ?? "");
            ClearSelection();                // 换图后旧选区无意义
            RefreshMapInfo();
            mapViewState = new MapViewState(label, !stitch, stitch, false,
                result.Stats?.WorldWidth ?? 0, result.Stats?.WorldHeight ?? 0, relThisLoad);
            RaiseMapViewChanged();           // v4：通知插件视图变了
        }
        catch (Exception ex) { ShowMapProgress(false, 0); MapInfo.Text = "渲染失败: " + ex.Message; }
    }

    // ===== 地图视口：RenderTransform 缩放/平移（不进布局，同帧原子生效）=====
    // 原来用 ScrollViewer + 改 Image.Width/Height：缩放（改尺寸）与偏移（改 ScrollViewer.Offset）
    // 是两步，中间会渲染出"新尺寸 + 旧偏移"的一帧 —— 就是滚轮缩放时看到的错位/闪烁；
    // 而且每次滚轮都要重跑一遍 12000×2400 的布局。改用 RenderTransform 后这两个问题一起没了。
    private Avalonia.Media.ScaleTransform? mapScaleT;
    private Avalonia.Media.TranslateTransform? mapTransT;
    private Avalonia.Vector mapOffset;          // 图片左上角相对视口的位移（已含缩放）
    private bool mapResizeHooked;

    private void EnsureMapTransform()
    {
        if (MapImage is null) { return; }
        if (mapScaleT is null)
        {
            mapScaleT = new Avalonia.Media.ScaleTransform(1, 1);
            mapTransT = new Avalonia.Media.TranslateTransform();
            var g = new Avalonia.Media.TransformGroup();
            g.Children.Add(mapScaleT);
            g.Children.Add(mapTransT);
            MapImage.RenderTransformOrigin = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Absolute);
            MapImage.RenderTransform = g;
        }
        if (!mapResizeHooked && MapCard is not null)
        {
            mapResizeHooked = true;
            MapCard.SizeChanged += (_, _) => ApplyMapTransform();   // 窗口尺寸变化时重新夹一下位移
        }
    }

    private Avalonia.Size MapViewport => MapCard?.Bounds.Size ?? default;

    private Avalonia.Size MapSourceSize => MapImage?.Source is Avalonia.Media.Imaging.Bitmap b
        ? new Avalonia.Size(b.PixelSize.Width, b.PixelSize.Height)
        : default;

    /// <summary>位移夹紧：比视口小就居中；比视口大就不许拖出边界。</summary>
    private void ClampMapOffset()
    {
        Avalonia.Size vp = MapViewport, im = MapSourceSize;
        if (vp.Width < 1 || vp.Height < 1 || im.Width < 1) { return; }
        double w = im.Width * mapZoom, h = im.Height * mapZoom;
        mapOffset = new Avalonia.Vector(
            w <= vp.Width ? (vp.Width - w) / 2.0 : Math.Clamp(mapOffset.X, vp.Width - w, 0),
            h <= vp.Height ? (vp.Height - h) / 2.0 : Math.Clamp(mapOffset.Y, vp.Height - h, 0));
    }

    /// <summary>★ 缩放与位移一次性写入：同一帧生效，不会出现"错位的一帧"。</summary>
    private void ApplyMapTransform()
    {
        EnsureMapTransform();
        if (mapScaleT is null || mapTransT is null) { return; }
        ClampMapOffset();
        mapScaleT.ScaleX = mapScaleT.ScaleY = mapZoom;
        mapTransT.X = mapOffset.X;
        mapTransT.Y = mapOffset.Y;
        // 叠加层用同一套缩放/位移（它是独立元素，需要自己一份 transform）
        if (MapOverlay is not null)
        {
            if (MapOverlay.RenderTransform is not Avalonia.Media.TransformGroup)
            {
                var og = new Avalonia.Media.TransformGroup();
                og.Children.Add(new Avalonia.Media.ScaleTransform(1, 1));
                og.Children.Add(new Avalonia.Media.TranslateTransform());
                MapOverlay.RenderTransformOrigin = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Absolute);
                MapOverlay.RenderTransform = og;
            }
            var og2 = (Avalonia.Media.TransformGroup)MapOverlay.RenderTransform!;
            ((Avalonia.Media.ScaleTransform)og2.Children[0]).ScaleX = ((Avalonia.Media.ScaleTransform)og2.Children[0]).ScaleY = mapZoom;
            ((Avalonia.Media.TranslateTransform)og2.Children[1]).X = mapOffset.X;
            ((Avalonia.Media.TranslateTransform)og2.Children[1]).Y = mapOffset.Y;
            MapOverlay.Zoom = mapZoom;
            MapOverlay.Offset = mapOffset;
            MapOverlay.InvalidateVisual();
        }
        UpdateSelectRect();     // 选区覆盖层跟着缩放/平移走
    }

    private string mapInfoHead = "";      // 状态行头部（文件名 + 统计），与缩放/选区分开拼
    private string lastWorldRel = "";      // 最近渲染的世界文件（相对 Worlds 目录），导出模板要用；拼接图为空

    // ===== 地图查看增强（A1~A5）=====
    private int mapSpawnX = -1, mapSpawnY = -1;         // 当前图的出生点（拼接图为 -1）
    private bool mapIsStitch;                           // 当前显示的是拼接图（没有单世界坐标语义）
    private TEdit5.Controls.WorldRenderEngine.Layers.RenderLayerVisibility mapLayers = new();
    private string mapLayerPreset = "全部图层";
    private string lastMapPick = "";                     // 上次渲染的选择（改图层后按它重渲）
    private string mapHoverText = "";                    // 悬停坐标
    private Avalonia.Point mapHoverPoint;                // 悬停位图坐标（用于状态行）
    private bool mapHoverValid;
    private bool mapGotoHooked;
    private bool mapPreviewShown;      // 这次加载是否给过"拼接预览"（决定最终换图时是否保持视图）
    private int mapLoadSeq;            // 加载序号：作废"迟到的预览"（预览是 Post 到 UI 线程的，可能晚于成品）

    /// <summary>"刷新"按钮置位一次：下次取世界文件时忽略远端缓存（用户换过世界要强制重读时用）。</summary>
    private static bool forceWorldReload;
    /// <summary>世界文件正在下载 —— 后台轮询先让路（同一根 SSH 隧道，抢起来实测会掉到 0.2 MB/s）。</summary>
    private static volatile bool worldDlBusy;

    private void InitMapLayerPresets()
    {
        if (MapLayers is null || MapLayers.ItemCount > 0) { return; }
        MapLayers.ItemsSource = new[] { "全部图层", "无背景墙", "只看图块", "只看液体", "只看电线" };
        MapLayers.SelectedIndex = 0;
    }

    private TEdit5.Controls.WorldRenderEngine.Layers.RenderLayerVisibility LayersFromPreset(string preset)
    {
        var v = new TEdit5.Controls.WorldRenderEngine.Layers.RenderLayerVisibility();
        switch (preset)
        {
            case "无背景墙": v.Wall = false; break;
            case "只看图块": v.Wall = false; v.Liquid = false; v.WireRed = v.WireBlue = v.WireGreen = v.WireYellow = false; v.Coatings = false; break;
            case "只看液体": v.Tile = false; v.Wall = false; v.WireRed = v.WireBlue = v.WireGreen = v.WireYellow = false; v.Coatings = false; break;
            case "只看电线": v.Tile = false; v.Wall = false; v.Liquid = false; v.Coatings = false; break;
        }
        return v;
    }

    /// <summary>状态行 = 头部 + 缩放 + 选区。只放事实。</summary>
    private void RefreshMapInfo()
    {
        if (MapInfo is null) { return; }
        string s = mapInfoHead + $" · 缩放 {mapZoom * 100:F0}%";
        if (hasSelection) { s += $" · 选区 {selX1 - selX0 + 1}×{selY1 - selY0 + 1} @({selX0},{selY0})"; }
        if (mapHoverValid && mapHoverText.Length > 0) { s += " · " + mapHoverText; }
        if (mapLayerPreset != "全部图层") { s += " · 图层:" + mapLayerPreset; }
        MapInfo.Text = s;
    }

    private void SetStatusLine(string text)
    {
        if (MapInfo is null) { return; }
        MapInfo.Text = text;
    }

    /// <summary>适应窗口所需的缩放比（不改状态）。</summary>
    private double FitZoom()
    {
        Avalonia.Size vp = MapViewport, im = MapSourceSize;
        if (im.Width < 1 || im.Height < 1) { return 1.0; }
        double vw = vp.Width > 50 ? vp.Width - 24 : Math.Max(400, Bounds.Width - 320);
        double vh = vp.Height > 50 ? vp.Height - 24 : Math.Max(240, Bounds.Height - 220);
        if (vw <= 10 || vh <= 10) { return 1.0; }
        return Math.Clamp(Math.Min(vw / im.Width, vh / im.Height), 0.02, 4.0);
    }

    private void FitMap()
    {
        Avalonia.Size vp = MapViewport, im = MapSourceSize;
        if (im.Width < 1 || vp.Width < 1) { return; }
        mapZoom = FitZoom();
        mapOffset = new Avalonia.Vector((vp.Width - im.Width * mapZoom) / 2.0, (vp.Height - im.Height * mapZoom) / 2.0);
        ApplyMapTransform();
        RefreshMapInfo();
    }

    /// <summary>以视口内某点为锚点缩放到 newZoom（锚点下的内容保持不动）。</summary>
    private void ZoomAt(Avalonia.Point anchor, double newZoom)
    {
        if (MapImage?.Source is null) { return; }
        newZoom = Math.Clamp(newZoom, 0.02, 4.0);
        double oldZoom = mapZoom;
        if (Math.Abs(newZoom - oldZoom) < 1e-9) { return; }
        double ix = (anchor.X - mapOffset.X) / oldZoom;    // 锚点对应的图片像素坐标（缩放前）
        double iy = (anchor.Y - mapOffset.Y) / oldZoom;
        mapZoom = newZoom;
        mapOffset = new Avalonia.Vector(anchor.X - ix * newZoom, anchor.Y - iy * newZoom);
        ApplyMapTransform();
        RefreshMapInfo();
    }

    /// <summary>按钮放大/缩小：以视口中心为锚点。</summary>
    private void ApplyZoom(double z)
    {
        Avalonia.Size vp = MapViewport;
        ZoomAt(new Avalonia.Point(vp.Width / 2.0, vp.Height / 2.0), z);
    }

    private void OnMapZoomIn(object? sender, RoutedEventArgs e) => ApplyZoom(mapZoom * 1.25);
    private void OnMapZoomOut(object? sender, RoutedEventArgs e) => ApplyZoom(mapZoom / 1.25);
    private void OnMapActual(object? sender, RoutedEventArgs e) => ApplyZoom(1.0);
    private void OnMapFit(object? sender, RoutedEventArgs e) => FitMap();

    /// <summary>
    /// 滚轮三种用法：直接滚 = 以光标为锚点缩放；Ctrl+滚 = 上下移动；Shift+滚 = 左右移动。
    /// 处理器挂在 MapImage 上并 Handled = true，与任何外层滚动逻辑都不抢。
    /// </summary>
    private void OnMapWheel(object? sender, Avalonia.Input.PointerWheelEventArgs e)
    {
        if (MapImage?.Source is null || MapCard is null) { return; }
        const double ScrollStep = 90;
        Avalonia.Input.KeyModifiers mods = e.KeyModifiers;

        if ((mods & Avalonia.Input.KeyModifiers.Control) != 0)
        {
            mapOffset = new Avalonia.Vector(mapOffset.X, mapOffset.Y - e.Delta.Y * ScrollStep);
            ApplyMapTransform();
        }
        else if ((mods & Avalonia.Input.KeyModifiers.Shift) != 0)
        {
            mapOffset = new Avalonia.Vector(mapOffset.X - e.Delta.Y * ScrollStep, mapOffset.Y);
            ApplyMapTransform();
        }
        else
        {
            ZoomAt(e.GetPosition(MapCard), mapZoom * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15));
        }
        e.Handled = true;
    }

    // ===== 拖拽平移 =====
    private bool mapPanning;
    private Avalonia.Point mapPanLast;

    private void OnMapPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (MapImage?.Source is null || MapCard is null) { return; }
        Avalonia.Input.PointerPoint pt = e.GetCurrentPoint(MapCard);
        if (pt.Properties.IsRightButtonPressed)
        {
            // 右键 = 以该点为中心（不用打字也能定位）；处于"适应窗口"时顺带放大，否则看起来没反应
            Avalonia.Size vp0 = MapViewport;
            var ip = ToImagePoint(pt.Position);
            double fit = FitZoom();
            if (mapZoom <= fit * 1.2) { mapZoom = Math.Max(fit * 4.0, Math.Min(1.0, fit * 8.0)); }
            mapOffset = new Avalonia.Vector(vp0.Width / 2.0 - ip.X * mapZoom, vp0.Height / 2.0 - ip.Y * mapZoom);
            ApplyMapTransform();
            RefreshMapInfo();
            SetStatusLine($"已居中到 ({ip.X:F0},{ip.Y:F0})");
            e.Handled = true;
            return;
        }
        if (!pt.Properties.IsLeftButtonPressed) { return; }

        if (mapSelectMode)
        {
            // 框选模式：左键拖出矩形
            mapSelStartImg = ToImagePoint(pt.Position);
            mapSelecting = true;
            hasSelection = false;
            UpdateSelectRect();
            e.Pointer.Capture(MapCard);
            e.Handled = true;
            return;
        }

        mapPanning = true;
        mapPanLast = pt.Position;
        e.Pointer.Capture(MapCard);
        e.Handled = true;
    }

    private void OnMapPointerMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (MapCard is null) { return; }

        if (mapSelecting)
        {
            Avalonia.Size im = MapSourceSize;
            if (im.Width < 1) { return; }
            Avalonia.Point ip = ToImagePoint(e.GetPosition(MapCard));
            double xa = Math.Clamp(Math.Min(mapSelStartImg.X, ip.X), 0, im.Width - 1);
            double xb = Math.Clamp(Math.Max(mapSelStartImg.X, ip.X), 0, im.Width - 1);
            double ya = Math.Clamp(Math.Min(mapSelStartImg.Y, ip.Y), 0, im.Height - 1);
            double yb = Math.Clamp(Math.Max(mapSelStartImg.Y, ip.Y), 0, im.Height - 1);
            selX0 = (int)Math.Floor(xa); selY0 = (int)Math.Floor(ya);
            selX1 = (int)Math.Floor(xb); selY1 = (int)Math.Floor(yb);
            hasSelection = selX1 > selX0 && selY1 > selY0;
            UpdateSelectRect();
            RefreshMapInfo();
            e.Handled = true;
            return;
        }

        if (!mapPanning)
        {
            // 悬停：把屏幕点换算成"位图像素 / 世界图块"
            try
            {
                Avalonia.Size im = MapSourceSize;
                if (im.Width >= 1)
                {
                    var ip = ToImagePoint(e.GetPosition(MapCard));
                    if (ip.X >= 0 && ip.Y >= 0 && ip.X < im.Width && ip.Y < im.Height)
                    {
                        mapHoverPoint = ip;
                        mapHoverValid = true;
                        mapHoverText = mapIsStitch
                            ? $"光标 ({ip.X:F0},{ip.Y:F0}) px（拼接图无单世界坐标）"
                            : $"光标 ({ip.X:F0},{ip.Y:F0}) · 区块 ({(int)ip.X / 200},{(int)ip.Y / 150})";
                        RefreshMapInfo();
                    }
                    else if (mapHoverValid) { mapHoverValid = false; mapHoverText = ""; RefreshMapInfo(); }
                }
            }
            catch { }
            return;
        }
        Avalonia.Point p = e.GetPosition(MapCard);
        Avalonia.Vector d = p - mapPanLast;
        mapPanLast = p;
        if (Math.Abs(d.X) < 0.01 && Math.Abs(d.Y) < 0.01) { return; }
        mapOffset = new Avalonia.Vector(mapOffset.X + d.X, mapOffset.Y + d.Y);
        ApplyMapTransform();
        e.Handled = true;
    }

    private void OnMapPointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (mapSelecting)
        {
            mapSelecting = false;
            e.Pointer.Capture(null);
            RaiseMapViewChanged();           // v4：选区变了
            return;
        }
        if (!mapPanning) { return; }
        mapPanning = false;
        e.Pointer.Capture(null);
    }

    // ===== 框选导出 .utpl =====
    private bool mapSelectMode;
    private bool mapSelecting;
    private Avalonia.Point mapSelStartImg;
    private bool hasSelection;
    private int selX0, selY0, selX1, selY1;      // 图格坐标（全分辨率渲染下 1 像素 = 1 图格）

    private bool MapStitchActive => (MapPicker?.SelectedItem as string ?? "").StartsWith("全部", StringComparison.Ordinal);

    private Avalonia.Point ToImagePoint(Avalonia.Point vp) =>
        new((vp.X - mapOffset.X) / Math.Max(1e-9, mapZoom), (vp.Y - mapOffset.Y) / Math.Max(1e-9, mapZoom));

    private Avalonia.Point ToViewportPoint(double ix, double iy) =>
        new(mapOffset.X + ix * mapZoom, mapOffset.Y + iy * mapZoom);

    /// <summary>把选区（图格坐标）画成视口里的矩形覆盖层。</summary>
    private void UpdateSelectRect()
    {
        if (MapSelectRect is null) { return; }
        if (!hasSelection) { MapSelectRect.IsVisible = false; return; }
        Avalonia.Point p0 = ToViewportPoint(selX0, selY0);
        Avalonia.Point p1 = ToViewportPoint(selX1 + 1, selY1 + 1);
        Avalonia.Controls.Canvas.SetLeft(MapSelectRect, p0.X);
        Avalonia.Controls.Canvas.SetTop(MapSelectRect, p0.Y);
        MapSelectRect.Width = Math.Max(1, p1.X - p0.X);
        MapSelectRect.Height = Math.Max(1, p1.Y - p0.Y);
        MapSelectRect.IsVisible = true;
    }

    private void ClearSelection()
    {
        bool had = hasSelection;
        hasSelection = false;
        mapSelecting = false;
        UpdateSelectRect();
        if (had) { RaiseMapViewChanged(); }
    }

    /// <summary>开关框选模式（插件通过 IMapContributionHost.SelectionMode 调）。</summary>
    private void SetSelectionMode(bool on)
    {
        if (on && MapStitchActive) { SetStatusLine("拼接图跨三个世界，不能框选 —— 请先选一个世界"); RaiseMapViewChanged(); return; }
        if (on && mapViewState.IsTemplate) { SetStatusLine("这是模板预览，不能框选 —— 请先选一个世界"); RaiseMapViewChanged(); return; }
        mapSelectMode = on;
        if (!on) { ClearSelection(); RefreshMapInfo(); }
        else { SetStatusLine("框选模式：按住左键在地图上拖出矩形"); }
        RaiseMapViewChanged();
    }

    /// <summary>把一张位图显示到地图页（换图时统一走这里）。</summary>
    private void PresentBitmap(SKBitmap bmp, bool keepView = false)
    {
        if (MapImage is null) { return; }
        double keepZoom = mapZoom;
        Avalonia.Vector keepOffset = mapOffset;
        var wb = ToWriteableBitmap(bmp);
        if (MapImage.Source is IDisposable old) { old.Dispose(); }
        MapImage.Source = wb;
        // ★ 布局尺寸必须显式等于位图尺寸（否则会被容器夹成视口大小，只画左上角一块）。
        MapImage.Width = wb.PixelSize.Width;
        MapImage.Height = wb.PixelSize.Height;
        // 叠加层（网格/出生点）与图像同尺寸、同变换
        if (MapOverlay is not null)
        {
            MapOverlay.Width = wb.PixelSize.Width;
            MapOverlay.Height = wb.PixelSize.Height;
            MapOverlay.SpawnX = mapSpawnX;
            MapOverlay.SpawnY = mapSpawnY;
            MapOverlay.ShowGrid = ChkMapGrid?.IsChecked == true;
            MapOverlay.ShowSpawn = ChkMapSpawn?.IsChecked == true;
            MapOverlay.IsVisible = MapOverlay.ShowGrid || (MapOverlay.ShowSpawn && mapSpawnX >= 0);
        }
        EnsureMapTransform();
        if (keepView) { mapZoom = keepZoom; mapOffset = keepOffset; ApplyMapTransform(); RefreshMapInfo(); }
        else { FitMap(); }
    }

    /// <summary>显示一个区块模板（宿主负责解析+渲染+显示；插件只提供名字）。</summary>
    private async Task<bool> ShowTemplateAsync(string name)
    {
        if (MapImage is null || MapInfo is null) { return false; }
        string? path = await ResolveTemplateFileAsync(name);
        if (path is null) { SetStatusLine("找不到模板 " + name); return false; }

        ShowMapProgress(true, 0);
        double lastProg = 0;
        IProgress<double> prog = new Progress<double>(p => { lastProg = Math.Max(lastProg, p); ShowMapProgress(true, lastProg); });

        var res = await Task.Run(() =>
        {
            if (!WorldMapRenderer.EnsureReady())
            {
                return (Bmp: (SKBitmap?)null, Error: "初始化失败：" + WorldMapRenderer.Error, Stats: (MapRenderStats?)null);
            }
            UtplRegion region;
            try { region = UtplCodec.Read(path); }
            catch (Exception ex) { return (Bmp: (SKBitmap?)null, Error: "模板解析失败：" + ex.Message, Stats: (MapRenderStats?)null); }
            SKBitmap bmp = WorldMapRenderer.RenderRegion(region, 1, out MapRenderStats st, p => prog.Report(p));
            return (Bmp: bmp, Error: "", Stats: (MapRenderStats?)st);
        });

        string label = "区块模板 " + name;
        if (res.Bmp is null)
        {
            ShowMapProgress(false, 0);
            SetStatusLine(label + " · " + res.Error);
            mapViewState = new MapViewState(label, false, false, false, 0, 0, "");
            RaiseMapViewChanged();
            return false;
        }
        using (res.Bmp) { PresentBitmap(res.Bmp); }
        ShowMapProgress(false, 1);
        lastWorldRel = "";                 // 模板预览没有可裁剪的世界
        ClearSelection();
        mapInfoHead = label + " · " + (res.Stats?.Describe() ?? "");
        RefreshMapInfo();
        mapViewState = new MapViewState(label, false, false, true,
            res.Stats?.WorldWidth ?? 0, res.Stats?.WorldHeight ?? 0, "");
        RaiseMapViewChanged();
        return true;
    }



    // ===== 地图增强：跳转 / 叠加层 / 图层 / 导出 =====
    /// <summary>A3：输入 "x,y" 跳到该世界坐标（居中显示）。</summary>
    private void OnMapGoto(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (MapCard is null || MapImage?.Source is null) { return; }
            string raw = (MapGotoBox?.Text ?? "").Trim();
            if (raw.Length == 0) { return; }
            var parts = raw.Split(new[] { ',', '，', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !double.TryParse(parts[0], out double tx) || !double.TryParse(parts[1], out double ty))
            {
                SetStatusLine("跳转需要两个数字，例如 3120,840");
                return;
            }
            Avalonia.Size im = MapSourceSize, vp = MapViewport;
            if (im.Width < 1 || vp.Width < 1) { return; }
            // ★ 整张图正好装得下时（适应窗口），位移会被 ClampMapOffset 夹回居中 —— 跳转看起来"没反应"。
            //   所以先在"适应"状态下自动放大到一个能看清的倍率，再居中。
            double fit = FitZoom();
            if (mapZoom <= fit * 1.2) { mapZoom = Math.Max(fit * 4.0, Math.Min(1.0, fit * 8.0)); }
            mapOffset = new Avalonia.Vector(vp.Width / 2.0 - tx * mapZoom, vp.Height / 2.0 - ty * mapZoom);
            ApplyMapTransform();
            mapHoverValid = false; mapHoverText = "";
            RefreshMapInfo();
            SetStatusLine($"已跳到 ({tx:F0},{ty:F0})");
        }
        catch (Exception ex) { SetStatusLine("跳转失败: " + ex.Message); }
    }

    /// <summary>A2：网格 / 出生点开关。</summary>
    private void OnMapOverlayToggle(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (MapOverlay is null) { return; }
            MapOverlay.ShowGrid = ChkMapGrid?.IsChecked == true;
            MapOverlay.ShowSpawn = ChkMapSpawn?.IsChecked == true;
            MapOverlay.SpawnX = mapSpawnX;
            MapOverlay.SpawnY = mapSpawnY;
            MapOverlay.IsVisible = MapOverlay.ShowGrid || (MapOverlay.ShowSpawn && mapSpawnX >= 0);
            MapOverlay.InvalidateVisual();
        }
        catch { }
    }

    /// <summary>A5：图层预设切换 —— 需要重新渲染（颜色是按层算出来的，改不了已生成的像素）。</summary>
    private async void OnMapLayersChanged(object? sender, SelectionChangedEventArgs e)
    {
        try
        {
            string preset = MapLayers?.SelectedItem as string ?? "全部图层";
            if (preset == mapLayerPreset) { return; }
            mapLayerPreset = preset;
            mapLayers = LayersFromPreset(preset);
            RefreshMapInfo();
            if (lastMapPick.Length == 0) { return; }
            SetStatusLine("图层已切到「" + preset + "」，重新渲染…");
            await LoadMap();
        }
        catch (Exception ex) { SetStatusLine("切换图层失败: " + ex.Message); }
    }

    /// <summary>A4：导出当前视口为 PNG。</summary>
    private void OnMapExportView(object? sender, RoutedEventArgs e) => ExportMap(false);

    /// <summary>A4：导出整张图为 PNG。</summary>
    private void OnMapExportFull(object? sender, RoutedEventArgs e) => ExportMap(true);

    private void ExportMap(bool whole)
    {
        try
        {
            if (MapImage?.Source is not Avalonia.Media.Imaging.Bitmap bmp) { SetStatusLine("还没有渲染任何世界"); return; }
            var (src, _, _) = ReadCurrentMapBitmap();
            if (src is null) { SetStatusLine("导出失败：取不到位图"); return; }
            int sw = src.Width, sh = src.Height;
            int x0 = 0, y0 = 0, w = sw, h = sh;
            if (!whole)
            {
                Avalonia.Size vp = MapViewport;
                x0 = (int)Math.Round((-mapOffset.X) / mapZoom);
                y0 = (int)Math.Round((-mapOffset.Y) / mapZoom);
                w = (int)Math.Round(vp.Width / mapZoom);
                h = (int)Math.Round(vp.Height / mapZoom);
                x0 = Math.Clamp(x0, 0, Math.Max(0, sw - 1)); y0 = Math.Clamp(y0, 0, Math.Max(0, sh - 1));
                w = Math.Clamp(w, 1, sw - x0); h = Math.Clamp(h, 1, sh - y0);
            }
            string dir = Path.Combine(AppContext.BaseDirectory, "map-export");
            Directory.CreateDirectory(dir);
            string name = "map_" + (lastWorldRel.Length > 0 ? Path.GetFileNameWithoutExtension(lastWorldRel) : "stitched")
                        + (whole ? "_full" : "_view") + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
            string outPath = Path.Combine(dir, name);
            using (var crop = new SKBitmap(w, h, SKImageInfo.PlatformColorType, SKAlphaType.Premul))
            {
                using (var c = new SKCanvas(crop))
                {
                    c.DrawBitmap(src, new SKRect(x0, y0, x0 + w, y0 + h), new SKRect(0, 0, w, h));
                }
                using var img = SKImage.FromBitmap(crop);
                using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                using var fs = File.Create(outPath);
                data.SaveTo(fs);
            }
            SetStatusLine($"已导出 {w}×{h} → {outPath}");
            ERR("地图已导出到 " + outPath);
        }
        catch (Exception ex) { SetStatusLine("导出失败: " + ex.Message); }
    }

    /// <summary>把当前显示位图取成 SKBitmap（缓存 ViewModel 里的 SKBitmap 比从 WriteableBitmap 反读快）。</summary>
    private (SKBitmap? bmp, int w, int h) ReadCurrentMapBitmap()
    {
        try
        {
            if (MapImage?.Source is Avalonia.Media.Imaging.WriteableBitmap wb)
            {
                int pw = wb.PixelSize.Width, ph = wb.PixelSize.Height;
                var bmp = new SKBitmap(pw, ph, SKImageInfo.PlatformColorType, SKAlphaType.Premul);
                using (var fb = wb.Lock())
                {
                    // ★ 两边都是 BGRA8888（Avalonia 写回缓冲 & Skia 平台色型），按行 memcpy 即可。
                    //   逐像素 SetPixel 在 12000×2400 上是 2880 万次调用，实测要十几秒。
                    unsafe
                    {
                        byte* dst = (byte*)bmp.GetPixels().ToPointer();
                        int dstRb = bmp.RowBytes;
                        int rowBytes = Math.Min(fb.RowBytes, dstRb);
                        for (int y = 0; y < ph; y++)
                        {
                            Buffer.MemoryCopy((byte*)fb.Address + (long)y * fb.RowBytes,
                                              dst + (long)y * dstRb,
                                              dstRb, rowBytes);
                        }
                    }
                }
                return (bmp, pw, ph);
            }
        }
        catch { }
        return (null, 0, 0);
    }

    /// <summary>双击：没放大就放大到 2×（锚在鼠标处），已放大就回到适应窗口。</summary>
    private void OnMapDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (MapImage?.Source is null || MapCard is null) { return; }
        double fit = FitZoom();
        if (mapZoom > fit * 1.15) { FitMap(); }
        else { ZoomAt(e.GetPosition(MapCard), Math.Min(fit * 2.0, 4.0)); }
        e.Handled = true;
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
            string zipName = latest.Name;
            ERR("恢复中（解压到 " + root + "）…");
            // ★ K5（审查报告）：解压是几十 MB 的磁盘 I/O + 解压，原来直接压在 UI 线程上 → 移到后台线程。
            //   zip-slip 校验原样保留在循环里（归一化后必须落在 utsl-deploy 之内）。
            (int n, int skipped) = await Task.Run(() =>
            {
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
                return (n, skipped);
            });
            ERR($"已从 {zipName} 恢复 {n} 个文件" + (skipped > 0 ? $"（{skipped} 个越界条目已跳过）" : "") + " —— 建议重启服务端使其生效");
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

    /// <summary>
    /// 掉线自启的决策写两处：状态行（ErrText，会被下一次轮询清空）+ 服务器终端（留痕、能截图）。
    /// ★ 只写状态行的话，"第 N/3 次自动重启"这类关键信息 2 秒后就没了，事后无法复盘。
    /// </summary>
    private void Wd(string msg)
    {
        ERR(msg);
        try { Term(msg, "#FEBC2E"); } catch { }
    }

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

    /// <summary>当前能否可靠判断"服务端到底在不在"（本机看进程；远端看 agent 或新鲜快照）。</summary>
    private static bool LivenessKnown()
    {
        if (!UseRemote && !DirectReady) { return true; }                   // 本机：进程表就是事实
        if (DirectReady) { return AgentAlive; }                            // 直连：必须 agent 活着
        return SnapshotAtMs > 0 && Environment.TickCount64 - SnapshotAtMs < 90000;   // 远端：快照要新鲜
    }
    // ★ 换图/启动保护窗口（移植 PGame 的 watchdogWorldBuildSuppress 思路）：
    //   世界加载期间 REST 本来就不通，若用"REST 通不通"当存活判据会误判掉线 → 重启循环。
    private static long watchdogProtectUntilMs;

    /// <summary>开启保护窗口：启动/重启/整链重置后的一段时间内，掉线自启不介入。</summary>
    private static void ProtectWatchdog(int seconds, string why)
    {
        watchdogProtectUntilMs = Environment.TickCount64 + seconds * 1000L;
        _ = why;
    }

    // ★ v6：插件请求起停服务端 —— 与按钮同一条路径，但把"是谁请求的"记进终端（可审计）。
    private async Task<bool> PluginStartServerAsync(string reason)
    {
        try
        {
            Term($"--- 插件请求启动服务端（{reason}）---", "#79C0FF");
            ProtectWatchdog(600, "plugin-start:" + reason);
            ProcessResult r = await ProcessBackend.StartAsync();
            Term(r.Message, PickColor(r.Message));
            ErrText.Text = r.Ok ? "插件已请求启动服务端" : r.Message;
            return r.Ok;
        }
        catch (Exception ex) { ERR("插件启动服务端失败: " + ex.Message); return false; }
    }

    private async Task<bool> PluginStopServerAsync(string reason)
    {
        try
        {
            Term($"--- 插件请求停止服务端（{reason}）---", "#FEBC2E");
            await GracefulStopAsync();
            ProcessResult r = await ProcessBackend.StopAsync();
            Term(r.Message, PickColor(r.Message));
            ErrText.Text = r.Ok ? "插件已请求停止服务端" : r.Message;
            return r.Ok;
        }
        catch (Exception ex) { ERR("插件停止服务端失败: " + ex.Message); return false; }
    }

    /// <summary>★ 修 bug#15：目标是否为本机 —— 远端目标绝不在本机拉起/停止服务端进程。</summary>
    private static bool TargetIsLocal => ConnHost is "127.0.0.1" or "localhost" or "::1" or "0.0.0.0";

    private static string ServerDir => LocalDir;
    private static string ServerExe => Path.Combine(LocalDir, "UnifierTSL.exe");

    // ★ 必须按【本档部署目录】判（AnyAliveIn），不能按"本机有没有 UnifierTSL"判：
    //   同机跑着主档时，第二份部署点「启动」会被"服务端在跑"挡回去 —— 这就是 7898 一直起不来的真因。
    private static bool ServerRunning => ServerDir.Length > 0 && LocalProcessBackend.AnyAliveIn(ServerDir);   // ★ 用 !HasExited 判，别按名字判（僵尸条目会让掉线自启失效）

    private async void OnStartServer(object? sender, RoutedEventArgs e)
    {
        ActiveSrv.UserStopped = false;      // 用户主动启动 → 恢复掉线自启
        bool remote = ProcessBackend.IsRemote;
        if (remote)
        {
            ProtectWatchdog(600, "remote-start");   // 启动后 10 分钟内掉线自启不介入（世界加载可能很久）
            Term("--- 远端启动中（SSH + WMI + 控制台启动器），请稍候 ---", "#79C0FF");
            ErrText.Text = "远端启动中…";
        }
        else
        {
            if (ServerRunning) { return; }   // 只挡"本档已在跑"，同机另一份部署不算
            ProtectWatchdog(600, "local-start");
        }
        ProcessResult r = await ProcessBackend.StartAsync();
        if (!remote) { if (!r.Ok) { ErrText.Text = r.Message; } return; }
        Term(r.Message, PickColor(r.Message));
        ErrText.Text = r.Ok ? "远端启动指令已下发" : r.Message;
    }

    private async void OnStopServer(object? sender, RoutedEventArgs e)
    {
        bool remote = ProcessBackend.IsRemote;
        if (remote)
        {
            Term("--- 远端停止中（只杀 UTSL，不动机器人）---", "#79C0FF");
            ErrText.Text = "远端停止中…";
        }
        await GracefulStopAsync();          // ★ 三段式第 1~2 段：先请它自己收尾（存盘/踢人），等世界全停
        ProcessResult r = await ProcessBackend.StopAsync();
        // ★ 用户手动停机：明确告诉掉线自启"这一次不要自动拉起"（重新点启动会清掉这个标记）
        ActiveSrv.UserStopped = true;
        if (remote)
        {
            Term(r.Message, PickColor(r.Message));
            ErrText.Text = r.Ok ? "远端停止指令已下发" : r.Message;
        }
        else if (!r.Ok) { ErrText.Text = r.Message; }
        NotifyServerState(false);
    }

    /// <summary>
    /// 停机三段式（第 1~2 段）：先发停机指令请服务端自己收尾（UTSL 的 /off = 存盘 + 踢人 + 断开），
    /// 再等「世界全部停掉」或超时，最后才由调用方结束进程。
    /// ★ 实测（2026-10-18）：UTSL 的宿主进程【不会】因为 /off 自己退出 —— 世界停了、进程还在、REST 仍 200。
    ///   所以第 3 段（kill）不能省；能省掉的是"数据来不及存盘就被杀"。
    /// </summary>
    private async Task GracefulStopAsync()
    {
        ServerProfile prof = ActiveSrv;
        string cmd = (prof.StopCommand ?? "").Trim();
        int timeoutSec = prof.StopTimeoutSec;
        if (cmd.Length == 0 || timeoutSec <= 0)
        {
            Term("--- 停机：本档未配置停机指令 → 直接结束进程（可用「连接设置 → 停机指令」打开）---", "#8E8E93");
            return;
        }
        try { _ = await Http.GetStringAsync(Rest + "/world/list?token=" + Uri.EscapeDataString(Token)); }
        catch
        {
            Term("--- 停机：REST 不可达 → 跳过停机指令，直接结束进程 ---", "#FEBC2E");
            return;
        }
        try
        {
            string url = Rest + "/world/command?token=" + Uri.EscapeDataString(Token)
                       + "&world=all&cmd=" + Uri.EscapeDataString(cmd) + "&scope=console";
            string res = await Http.GetStringAsync(url);
            Term($"→ 停机指令 {cmd}（全部世界）  " + res.Replace("\n", " ").Replace("\r", "").Trim(), "#79C0FF");
        }
        catch (Exception ex)
        {
            Term("停机指令发送失败（继续直接结束进程）: " + ex.Message, "#FEBC2E");
            return;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < timeoutSec)
        {
            await Task.Delay(400);
            if (!UseRemote && !LocalProcessBackend.AnyAliveIn(ServerDir))
            {
                Term($"停机：进程已在 {sw.Elapsed.TotalSeconds:F1}s 内自行退出", "#30D158");
                return;
            }
            bool anyRunning;
            try
            {
                string txt = await Http.GetStringAsync(Rest + "/world/list?token=" + Uri.EscapeDataString(Token));
                using var doc = JsonDocument.Parse(txt);
                anyRunning = doc.RootElement.TryGetProperty("worlds", out var ws)
                             && ws.EnumerateArray().Any(w => w.TryGetProperty("running", out var r) && r.GetBoolean());
            }
            catch { anyRunning = false; }        // REST 已经不通 = 正在收尾
            if (!anyRunning)
            {
                Term($"停机：世界已全部停止（{sw.Elapsed.TotalSeconds:F1}s，已存盘），接下来结束进程", "#30D158");
                return;
            }
        }
        Term($"停机：{timeoutSec}s 内世界仍未全停 → 直接结束进程（可能有未保存的进度）", "#FEBC2E");
    }

    private async void OnRestartServer(object? sender, RoutedEventArgs e)
    {
        ProtectWatchdog(600, "restart");   // ★ 重启后给世界加载留出时间，避免掉线自启介入造成重启循环
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
