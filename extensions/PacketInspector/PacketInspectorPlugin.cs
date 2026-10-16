using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UTSLManager.Plugins;

namespace PacketInspector;

/// <summary>
/// PacketInspector —— 管理器侧「数据包查看器」。
/// 两种模式：
///   ① 按玩家聚合（默认，防刷屏）：每个「玩家 × 包类型」只登记一次，之后只涨计数；玩家下线自动移除。
///   ② 原始包流：逐包列表（排查单包问题时用）。
/// 数据来源：服务端插件 NetPacketChannel 的 REST（/packets/players 与 /packets）。
/// 命令：!packets players | raw | on | off | clear | top | who | test | status
/// </summary>
public sealed class PacketInspectorPlugin : IManagerPlugin
{
    private const int MaxRows = 400;
    private const int MaxAppendPerPoll = 150;
    private const int MaxTypesPerPlayer = 30;

    private static ScrollViewer Gutter(ScrollViewer sv)
    {
        // ★ 不再自绘槽位（主题自带槽位，会并存成两条）：只留出右边距 + 常显滚动条，
        //   真正的「上下封顶」由 App 级样式给 ScrollBar 加上下 Margin 实现（露出圆角端点）
        sv.Padding = new Thickness(0, 0, 13, 0);
        sv.AllowAutoHide = false;
        return sv;
    }

    private static readonly IBrush Fg = new SolidColorBrush(Color.Parse("#F2F2F5"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#7C7C82"));
    private static readonly IBrush Body = new SolidColorBrush(Color.Parse("#B9B9C0"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#66CCFF"));
    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#7EE787"));
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#E3B341"));
    private static readonly IBrush Err = new SolidColorBrush(Color.Parse("#FF7B72"));
    private static readonly FontFamily Mono = new("Cascadia Mono,Consolas,monospace");

    // 纯数据 DTO：后台线程只解析数据，控件一律在 UI 线程建（Avalonia 控件有线程亲和性）
    private sealed record TypeDto(int Id, string Name, string AltName, long Count, long Bytes, int Len, string Hex, long FirstMs, long LastMs, string Decoded, bool Limited, bool Blocked, bool DecodeOn, bool DecodeFailed);
    private sealed record PlayerDto(int Dir, int Who, string Name, bool Online, string World, long FirstMs, long LastMs, long Total, long Bytes, int TypeCount, List<TypeDto> Types);

    private IManagerContext? _ctx;
    private Control? _root;      // ★ 用于判断面板是否可见（不可见时不轮询）
    private bool _modePlayers = true;      // 默认聚合模式
    private bool _syncingMode;

    // 原始包流
    private long _since;
    private int _polling;
    private long _totalSeen;
    private double _pps;
    private long _lastServerTotal = -1;
    private DateTime _lastRateAt = DateTime.Now;
    private string _filterWorld = "";
    private string _filterType = "";
    private string _filterId = "";
    private StackPanel? _listHost;
    private ScrollViewer? _listScroll;
    private bool _autoScroll = true;

    // 玩家聚合
    private bool _onlineOnly;
    private StackPanel? _playersHost;
    private long _trackedPlayers;
    private long _onlinePlayers;
    private long _aggTotal;
    private string _playersSig = "";
    private int _dirFilter = -1;            // -1 全部 / 0 C→S / 1 S→C
    private int _rawDirFilter = -1;
    private CheckBox? _limitOn;
    private TextBox? _limitMax;
    private TextBox? _limitWindow;
    private ComboBox? _limitActionBox;
    private TextBlock? _violations;

    // 共享 UI
    private TextBlock? _status;
    private TextBlock? _stats;
    private StackPanel? _rawPanel;
    private StackPanel? _playersPanel;
    private ComboBox? _modeBox;

    public string Name => "数据包查看器";
    public string Icon => "▤";
    public string Description => "查看 UTSL 收到的包：按玩家×类型聚合（防刷屏）或原始包流";
    public string Author => "Zykor-Club";
    public int RequiredApiVersion => ManagerApi.Version;

    public void OnLoad(IManagerContext ctx)
    {
        _ctx = ctx;
        ctx.RegisterCommand("packets", "查看器：players/raw/on/off/clear/top/who/test/status", args =>
        {
            _ = HandleCommandAsync(args);
            return Task.CompletedTask;
        });
        ctx.RegisterMetric("抓包 · 已收到", "包", () => _totalSeen);
        ctx.RegisterMetric("抓包 · 速率", "包/秒", () => Math.Round(_pps, 1));
        ctx.RegisterMetric("玩家 · 跟踪中", "人", () => _trackedPlayers);
        ctx.RegisterMetric("玩家 · 在线", "人", () => _onlinePlayers);
        ctx.RegisterStatus("抓包", () => _modePlayers ? "聚合中" : (Volatile.Read(ref _polling) == 1 ? "监听中" : "空闲"));
        ctx.RunBackground(PollLoopAsync, "包轮询");
        ctx.Log("[数据包查看器] 已加载（默认按玩家聚合）。服务端需装 NetPacketChannel。");
    }

    public void OnUnload()
    {
        Volatile.Write(ref _polling, 0);
        try
        {
            IManagerContext? c = _ctx;
            if (c is not null) { _ = c.RestPost("/packets/control?on=false"); }
        }
        catch { }
    }

    public Control? CreateView(IManagerContext ctx)
    {
        _ctx = ctx;
        var root = new StackPanel { Spacing = 10 };
        _root = root;   // ★ 记录根视图，供轮询判断可见性

        // ===== 顶部工具栏（一行：模式 / 方向 / 过滤 / 操作）=====
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        _modeBox = new ComboBox { Width = 128, FontSize = 11.5 };
        _modeBox.Items.Add(new ComboBoxItem { Content = "按玩家聚合" });
        _modeBox.Items.Add(new ComboBoxItem { Content = "原始包流" });
        _modeBox.SelectedIndex = 0;   // ⚠️ 必须在 Items 之后设，否则不显示
        ToolTip.SetTip(_modeBox, "按「玩家 × 包类型」聚合计数（防刷屏），或看逐包原始流");
        _modeBox.SelectionChanged += (_, _) =>
        {
            if (_syncingMode) { return; }
            SetMode(_modeBox.SelectedIndex == 0);
        };
        // 默认聚合模式；切到原始包流会自动开始接收（见 SetMode）
        top.Children.Add(_modeBox);

        var dirBox = new ComboBox { Width = 90, FontSize = 11.5 };
        dirBox.Items.Add(new ComboBoxItem { Content = "全部" });
        dirBox.Items.Add(new ComboBoxItem { Content = "C→S" });
        dirBox.Items.Add(new ComboBoxItem { Content = "S→C" });
        dirBox.SelectedIndex = 0;
        ToolTip.SetTip(dirBox, "C→S = 客户端发来的包（有字节、可解码）；S→C = 服务端发出的包（仅类型/计数/目标）");
        dirBox.SelectionChanged += (_, _) => { _dirFilter = dirBox.SelectedIndex - 1; _playersSig = ""; };
        top.Children.Add(dirBox);

        var online = new CheckBox { Content = "只看在线", FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        online.IsCheckedChanged += (_, _) => { _onlineOnly = online.IsChecked == true; _playersSig = ""; };
        top.Children.Add(online);

        top.Children.Add(Sep());
        top.Children.Add(Tip(BtnAsync("⟳", async () => { _playersSig = ""; await PollPlayersAsync(); }), "刷新"));
        top.Children.Add(Tip(BtnAsync("⊗", ClearAsync), "清空服务端缓冲区与聚合数据"));
        top.Children.Add(Tip(BtnAsync("⊕", InjectAsync), "注入测试包（不经过真实管线，仅用于自测）"));
        top.Children.Add(Tip(BtnAsync("⇩", ExportPlayersAsync), "导出聚合数据为 JSONL"));
        root.Children.Add(top);

        _status = new TextBlock { Text = "正在读取…", FontSize = 11.5, Foreground = Muted, TextWrapping = TextWrapping.Wrap };
        root.Children.Add(_status);

        // ===== 聚合模式面板 =====
        _playersPanel = new StackPanel { Spacing = 8 };

        // 规则默认收起，避免一屏都是字
        var rbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 6, 0, 0) };
        _limitOn = new CheckBox { Content = "自动限流", FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(_limitOn, "按「玩家 × 包类型」滑窗计数，超阈值自动丢包/踢人；平时零成本（不注册类型）");
        rbar.Children.Add(_limitOn);
        rbar.Children.Add(Lbl("窗口"));
        _limitWindow = Tb(56, "1000"); _limitWindow.Text = "1000";
        ToolTip.SetTip(_limitWindow, "统计窗口（毫秒）");
        rbar.Children.Add(_limitWindow);
        rbar.Children.Add(Lbl("ms · 上限"));
        _limitMax = Tb(52, "120"); _limitMax.Text = "120";
        ToolTip.SetTip(_limitMax, "窗口内允许的最大包数");
        rbar.Children.Add(_limitMax);
        _limitActionBox = new ComboBox { Width = 94, FontSize = 11.5 };
        _limitActionBox.Items.Add(new ComboBoxItem { Content = "只告警" });
        _limitActionBox.Items.Add(new ComboBoxItem { Content = "丢包" });
        _limitActionBox.Items.Add(new ComboBoxItem { Content = "踢下线" });
        _limitActionBox.SelectedIndex = 1;
        rbar.Children.Add(_limitActionBox);
        rbar.Children.Add(BtnAsync("应用", ApplyRulesAsync));
        rbar.Children.Add(BtnAsync("清空规则", ClearRulesAsync));
        _playersPanel.Children.Add(new Expander { Header = "限流与封包", IsExpanded = false, FontSize = 11.5, Content = rbar });

        _violations = new TextBlock { FontSize = 11, Foreground = Warn, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        _playersPanel.Children.Add(_violations);

        _playersPanel.Children.Add(AggHeader());
        _playersHost = new StackPanel { Spacing = 6 };
        _playersPanel.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse("#101013")),
            BorderBrush = new SolidColorBrush(Color.Parse("#2A2A30")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8),
            Child = Gutter(new ScrollViewer { MaxHeight = 430, Content = _playersHost, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }),
        });
        root.Children.Add(_playersPanel);

        // ===== 原始包流面板 =====
        _rawPanel = new StackPanel { Spacing = 8, IsVisible = false };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        bar.Children.Add(BtnAsync("▶ 开始抓包", StartCaptureAsync));
        bar.Children.Add(BtnAsync("■ 停止", StopCaptureAsync));
        bar.Children.Add(BtnAsync("⊗ 清空", ClearAsync));
        bar.Children.Add(BtnAsync("⊕ 注入测试", InjectAsync));
        bar.Children.Add(BtnAsync("⇩ 导出 JSONL", ExportAsync));
        var rawDir = new ComboBox { Width = 90, FontSize = 11.5 };
        rawDir.Items.Add(new ComboBoxItem { Content = "全部方向" });
        rawDir.Items.Add(new ComboBoxItem { Content = "C→S" });
        rawDir.Items.Add(new ComboBoxItem { Content = "S→C" });
        rawDir.SelectedIndex = 0;
        rawDir.SelectionChanged += (_, _) => { _rawDirFilter = rawDir.SelectedIndex - 1; };
        bar.Children.Add(rawDir);
        var auto = new CheckBox { Content = "自动滚动", IsChecked = true, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        auto.IsCheckedChanged += (_, _) => _autoScroll = auto.IsChecked == true;
        bar.Children.Add(auto);
        _rawPanel.Children.Add(bar);

        var filt = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var wf = Tb(90, "世界（可空）");
        wf.TextChanged += (_, _) => _filterWorld = (wf.Text ?? "").Trim();
        ToolTip.SetTip(wf, "按世界名过滤，留空=全部");
        filt.Children.Add(wf);
        var tf = Tb(140, "类型（可空）");
        tf.TextChanged += (_, _) => _filterType = (tf.Text ?? "").Trim();
        ToolTip.SetTip(tf, "按包类型名过滤（如 PlayerControls），留空=全部");
        filt.Children.Add(tf);
        var idf = Tb(64, "ID（可空）");
        idf.TextChanged += (_, _) => _filterId = (idf.Text ?? "").Trim();
        ToolTip.SetTip(idf, "按包 ID 过滤，留空=全部");
        filt.Children.Add(idf);
        _rawPanel.Children.Add(filt);

        _rawPanel.Children.Add(new TextBlock
        {
            // 与 FormatLine 逐字段同构：方向(1)+" "+时间(12)+"  "+世界(8)+" "+玩家(10)+" #"+ID(3)+" "+类型(24)+" "+长度(5)+"  "+hex
            Text = "↓" + " " + PadDisp("时间", 12) + "  " + PadDisp("世界", 8) + " " + PadDisp("玩家", 10)
                   + " #" + "ID " + " " + PadDisp("类型", 24) + " " + PadDisp("长度", 5) + "  " + "Payload (hex)",
            FontSize = 11, FontFamily = Mono, Foreground = Muted, TextWrapping = TextWrapping.NoWrap,
        });

        _stats = new TextBlock { Text = "—", FontSize = 11, Foreground = Body, TextWrapping = TextWrapping.Wrap };
        _rawPanel.Children.Add(_stats);

        _listHost = new StackPanel { Spacing = 0 };
        _listScroll = new ScrollViewer
        {
            MaxHeight = 430,
            Content = _listHost,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _rawPanel.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse("#0E0E10")),
            BorderBrush = new SolidColorBrush(Color.Parse("#2A2A30")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8),
            Child = _listScroll,
        });
        root.Children.Add(_rawPanel);

        root.Children.Add(BuildHelp());
        return root;
    }

    private void SetMode(bool players)
    {
        _modePlayers = players;
        _playersSig = "";
        // 切到「原始包流」就自动开始接收（服务端抓包本来就是开着的），否则用户会看到一个永远空的列表
        if (!players && Volatile.Read(ref _polling) != 1) { _ = StartCaptureAsync(); }
        Dispatcher.UIThread.Post(() =>
        {
            if (_rawPanel is not null) { _rawPanel.IsVisible = !players; }
            if (_playersPanel is not null) { _playersPanel.IsVisible = players; }
            if (_modeBox is not null && _modeBox.SelectedIndex != (players ? 0 : 1))
            {
                _syncingMode = true;
                try { _modeBox.SelectedIndex = players ? 0 : 1; } finally { _syncingMode = false; }
            }
        });
    }

    // ===== 轮询 =====

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(800, ct); } catch (OperationCanceledException) { return; }
            try
            {
                // ★ 性能：面板不可见就完全不轮询（原来隐藏时照样每 800ms 发请求 + 重建行）
                if (_root is null || !_root.IsEffectivelyVisible) { continue; }
                if (_modePlayers) { await PollPlayersAsync(); }
                else if (Volatile.Read(ref _polling) == 1) { await PollOnceAsync(); }
            }
            catch (Exception ex) { SetStatus("轮询失败: " + ex.Message, Err); }
        }
    }

    private async Task PollPlayersAsync()
    {
        IManagerContext? ctx = _ctx;
        if (ctx is null) { return; }
        string json = await ctx.RestGet("/packets/players?onlineOnly=" + (_onlineOnly ? "1" : "0") + "&dir=" + _dirFilter);
        var players = new List<PlayerDto>();
        var sig = new StringBuilder();
        long tracked = 0, onlineCount = 0, aggTotal = 0;
        bool aggEnabled = true;
        string onlineNames = "";

        using (JsonDocument doc = JsonDocument.Parse(json))
        {
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("aggEnabled", out JsonElement ae)) { aggEnabled = ae.ValueKind == JsonValueKind.True; }
            if (root.TryGetProperty("aggTotal", out JsonElement at)) { aggTotal = at.GetInt64(); }
            if (root.TryGetProperty("trackedPlayers", out JsonElement tp)) { tracked = tp.GetInt64(); }
            if (root.TryGetProperty("players", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement p in arr.EnumerateArray())
                {
                    var types = new List<TypeDto>();
                    if (p.TryGetProperty("types", out JsonElement ta) && ta.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement t in ta.EnumerateArray())
                        {
                            types.Add(new TypeDto(
                                t.TryGetProperty("id", out JsonElement i) ? i.GetInt32() : -1,
                                Str(t, "name"),
                                Str(t, "altName"),
                                t.TryGetProperty("count", out JsonElement c) ? c.GetInt64() : 0,
                                t.TryGetProperty("bytes", out JsonElement b) ? b.GetInt64() : 0,
                                t.TryGetProperty("len", out JsonElement l) ? l.GetInt32() : 0,
                                Str(t, "hex"),
                                t.TryGetProperty("firstMs", out JsonElement fm) ? fm.GetInt64() : 0,
                                t.TryGetProperty("lastMs", out JsonElement lm) ? lm.GetInt64() : 0,
                                Str(t, "decodedPreview"),
                                t.TryGetProperty("limited", out JsonElement ld) && ld.ValueKind == JsonValueKind.True,
                                t.TryGetProperty("blocked", out JsonElement bk) && bk.ValueKind == JsonValueKind.True,
                                t.TryGetProperty("decodeOn", out JsonElement dc) && dc.ValueKind == JsonValueKind.True,
                                t.TryGetProperty("decodeFailed", out JsonElement df) && df.ValueKind == JsonValueKind.True));
                        }
                    }
                    var dto = new PlayerDto(
                        p.TryGetProperty("dir", out JsonElement dr) ? dr.GetInt32() : 0,
                        p.TryGetProperty("who", out JsonElement w) ? w.GetInt32() : -1,
                        Str(p, "name"),
                        p.TryGetProperty("online", out JsonElement oe) && oe.ValueKind == JsonValueKind.True,
                        Str(p, "world"),
                        p.TryGetProperty("firstMs", out JsonElement pf) ? pf.GetInt64() : 0,
                        p.TryGetProperty("lastMs", out JsonElement pl) ? pl.GetInt64() : 0,
                        p.TryGetProperty("total", out JsonElement pt) ? pt.GetInt64() : 0,
                        p.TryGetProperty("bytes", out JsonElement pb) ? pb.GetInt64() : 0,
                        p.TryGetProperty("typeCount", out JsonElement ptc) ? ptc.GetInt32() : 0,
                        types);

                    if (dto.Online && !players.Exists(x => x.Who == dto.Who && x.Online)) { onlineCount++; if (dto.Name.Length > 0) { onlineNames += (onlineNames.Length == 0 ? "" : ", ") + dto.Name; } }
                    sig.Append(dto.Who).Append(':').Append(dto.Total).Append(':').Append(dto.TypeCount).Append(';');
                    players.Add(dto);
                }
            }
        }
        _trackedPlayers = tracked;
        _onlinePlayers = onlineCount;
        _aggTotal = aggTotal;
        string sigText = sig.ToString();

        string status = (aggEnabled ? "● 聚合中" : "○ 聚合已关") + "   ·   跟踪 " + tracked + " 个槽位，在线 " + onlineCount + " 人"
                        + (onlineNames.Length > 0 ? "（" + onlineNames + "）" : "")
                        + "   ·   累计 " + aggTotal + " 包"
                        + "   ·   " + (players.Count == 0 ? "暂无数据（没有玩家发包，或未装服务端插件）" : "显示 " + players.Count + " 名玩家");

        // ★ 控件必须在 UI 线程创建（后台线程建 Avalonia 控件会静默不渲染）
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            SetStatusText(status, aggEnabled ? Green : Warn);
            if (_playersHost is null) { return; }
            if (_playersSig == sigText) { return; }
            _playersSig = sigText;
            _playersHost.Children.Clear();
            foreach (PlayerDto d in players) { _playersHost.Children.Add(BuildPlayerCard(d)); }
        });

        await PollRulesAsync();
    }

    private async Task PollOnceAsync()
    {
        IManagerContext? ctx = _ctx;
        if (ctx is null) { return; }

        var q = new StringBuilder("/packets?since=" + _since + "&limit=800");
        if (_rawDirFilter >= 0) { q.Append("&dir=").Append(_rawDirFilter); }
        if (_filterWorld.Length > 0) { q.Append("&world=").Append(Uri.EscapeDataString(_filterWorld)); }
        if (_filterType.Length > 0) { q.Append("&name=").Append(Uri.EscapeDataString(_filterType)); }
        if (int.TryParse(_filterId, out int idFilter)) { q.Append("&id=").Append(idFilter); }

        string json = await ctx.RestGet(q.ToString());
        var lines = new List<string>();
        var brushes = new List<IBrush>();
        bool capturing;
        int typeCount = -1;
        long serverTotal = -1;

        using (JsonDocument doc = JsonDocument.Parse(json))
        {
            JsonElement root = doc.RootElement;
            capturing = root.TryGetProperty("capturing", out JsonElement ce) && ce.ValueKind == JsonValueKind.True;
            if (root.TryGetProperty("next", out JsonElement ne)) { _since = ne.GetInt64(); }
            if (root.TryGetProperty("packets", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement p in arr.EnumerateArray())
                {
                    lines.Add(FormatLine(p));
                    brushes.Add(BrushFor(p));
                }
            }
        }

        try
        {
            using JsonDocument md = JsonDocument.Parse(await ctx.RestGet("/packets/meta"));
            JsonElement mr = md.RootElement;
            if (mr.TryGetProperty("totalCaptured", out JsonElement te)) { serverTotal = te.GetInt64(); }
            if (mr.TryGetProperty("packetTypeCount", out JsonElement pc)) { typeCount = pc.GetInt32(); }
        }
        catch { }

        DateTime now = DateTime.Now;
        if (serverTotal >= 0 && _lastServerTotal >= 0)
        {
            double secs = Math.Max(0.2, (now - _lastRateAt).TotalSeconds);
            _pps = Math.Max(0, (serverTotal - _lastServerTotal) / secs);
        }
        if (serverTotal >= 0) { _lastServerTotal = serverTotal; _lastRateAt = now; }
        if (lines.Count > 0) { _totalSeen += lines.Count; }

        string status = (capturing ? "● 抓包中" : "○ 已停止")
                        + "   ·   since=" + _since
                        + "   ·   本次会话已收到 " + _totalSeen + " 包"
                        + "   ·   " + _pps.ToString("F1") + " 包/秒"
                        + (typeCount >= 0 ? "   ·   服务端识别 " + typeCount + " 种包" : "")
                        + (lines.Count > 0 ? "   ·   本次 +" + lines.Count : "");

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            SetStatusText(status, capturing ? Green : Muted);
            AppendLines(lines, brushes);
        });
    }

    // ===== 操作 =====

    private async Task StartCaptureAsync()
    {
        IManagerContext? ctx = _ctx;
        if (ctx is null) { return; }
        if (Volatile.Read(ref _polling) == 1) { return; }
        try
        {
            using JsonDocument doc = JsonDocument.Parse(await ctx.RestGet("/packets/control?on=true&maxBytes=64"));
            if (doc.RootElement.TryGetProperty("seq", out JsonElement se)) { _since = se.GetInt64(); }
            ClearList();
            _totalSeen = 0;
            _lastServerTotal = -1;
            Volatile.Write(ref _polling, 1);
            SetStatus("● 抓包中…", Green);
            ctx.Log("[数据包查看器] 已开始原始抓包（since=" + _since + "）");
        }
        catch (Exception ex)
        {
            SetStatus("开始抓包失败: " + ex.Message + "（服务端是否装了 NetPacketChannel？）", Err);
        }
    }

    private async Task StopCaptureAsync()
    {
        IManagerContext? ctx = _ctx;
        Volatile.Write(ref _polling, 0);
        if (ctx is null) { return; }
        try { await ctx.RestGet("/packets/control?on=false"); SetStatus("○ 已停止原始抓包（聚合仍在进行）", Muted); }
        catch (Exception ex) { SetStatus("停止失败: " + ex.Message, Err); }
    }

    private async Task ClearAsync()
    {
        IManagerContext? ctx = _ctx;
        if (ctx is null) { return; }
        try
        {
            await ctx.RestGet("/packets/clear");
            _since = 0; _totalSeen = 0; _lastServerTotal = -1; _playersSig = "";
            ClearList();
            SetStatus("已清空服务端缓冲区与玩家聚合", Muted);
            ctx.Log("[数据包查看器] 已清空服务端抓包缓冲与聚合");
        }
        catch (Exception ex) { SetStatus("清空失败: " + ex.Message, Err); }
    }

    private async Task InjectAsync()
    {
        IManagerContext? ctx = _ctx;
        if (ctx is null) { return; }
        try
        {
            // 造两个假玩家，方便验证「按玩家聚合」：Alice 发 10 个 PlayerUpdate，Bob 发 3 个 PlayerUpdate + 2 个 SyncPlayer
            await ctx.RestGet("/packets/test?who=3&name=Alice&id=13&count=10");
            await ctx.RestGet("/packets/test?who=4&name=Bob&id=13&count=3");
            await ctx.RestGet("/packets/test?who=4&name=Bob&id=7&count=2");
            if (Volatile.Read(ref _polling) == 1) { await ctx.RestGet("/packets/test?id=13&count=2"); }
            _playersSig = "";
            ctx.Log("[数据包查看器] 已注入测试数据（Alice#3 10×id13；Bob#4 3×id13 + 2×id7）");
        }
        catch (Exception ex) { SetStatus("注入失败: " + ex.Message, Err); }
    }

    private async Task ExportAsync()
    {
        IManagerContext? ctx = _ctx;
        if (ctx is null) { return; }
        try
        {
            string json = await ctx.RestGet("/packets?since=0&limit=5000");
            Directory.CreateDirectory(ctx.DataDir);
            string file = Path.Combine(ctx.DataDir, "packets-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".jsonl");
            using JsonDocument doc = JsonDocument.Parse(json);
            var sb = new StringBuilder();
            int n = 0;
            if (doc.RootElement.TryGetProperty("packets", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement p in arr.EnumerateArray()) { sb.Append(p.GetRawText()).Append((char)10); n++; }
            }
            File.WriteAllText(file, sb.ToString());
            ctx.Log("[数据包查看器] 已导出 " + n + " 个原始包到 " + file);
            SetStatus("已导出到 " + file, Green);
        }
        catch (Exception ex) { SetStatus("导出失败: " + ex.Message, Err); }
    }

    private async Task ExportPlayersAsync()
    {
        IManagerContext? ctx = _ctx;
        if (ctx is null) { return; }
        try
        {
            string json = await ctx.RestGet("/packets/players?onlineOnly=0");
            Directory.CreateDirectory(ctx.DataDir);
            string file = Path.Combine(ctx.DataDir, "players-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
            File.WriteAllText(file, json);
            ctx.Log("[数据包查看器] 已导出玩家聚合到 " + file);
            SetStatus("已导出到 " + file, Green);
        }
        catch (Exception ex) { SetStatus("导出失败: " + ex.Message, Err); }
    }

    // ===== 规则（④）=====

    private async Task ApplyRulesAsync()
    {
        IManagerContext? ctx = _ctx;
        if (ctx is null) { return; }
        bool on = _limitOn?.IsChecked == true;
        int max = int.TryParse(_limitMax?.Text, out int m) && m > 0 ? m : 120;
        int win = int.TryParse(_limitWindow?.Text, out int w) && w >= 50 ? w : 1000;
        string act = _limitActionBox?.SelectedIndex switch { 2 => "kick", 1 => "drop", _ => "warn" };
        try
        {
            await ctx.RestGet("/packets/rules?auto=" + (on ? "1" : "0") + "&max=" + max + "&window=" + win + "&action=" + act);
            ctx.Log("[数据包查看器] 规则已应用：自动限流=" + on + "，每 " + win + "ms 超过 " + max + " 包 → " + act);
            _playersSig = "";
            await PollPlayersAsync();
        }
        catch (Exception ex) { SetStatus("应用规则失败: " + ex.Message, Err); }
    }

    private async Task ClearRulesAsync()
    {
        IManagerContext? ctx = _ctx;
        if (ctx is null) { return; }
        try
        {
            await ctx.RestGet("/packets/rules?auto=0");
            await ctx.RestGet("/packets/block?clear=1");
            await ctx.RestGet("/packets/decode?clear=1");
            _playersSig = "";
            ctx.Log("[数据包查看器] 已清空 限流/封包/解码 规则");
            await PollPlayersAsync();
        }
        catch (Exception ex) { SetStatus("清空规则失败: " + ex.Message, Err); }
    }

    private async Task DecodeTypeAsync(int id, bool on)
    {
        IManagerContext? ctx = _ctx;
        if (ctx is null) { return; }
        await ctx.RestGet("/packets/decode?" + (on ? "add=" : "remove=") + id);
        ctx.Log("[数据包查看器] " + (on ? "已开启" : "已关闭") + " #" + id + " 的字段解码");
        _playersSig = "";
        await PollPlayersAsync();
    }

    private async Task BlockPairAsync(int dir, int who, int id, bool on)
    {
        IManagerContext? ctx = _ctx;
        if (ctx is null) { return; }
        await ctx.RestGet("/packets/block?dir=" + dir + "&who=" + who + "&id=" + id + (on ? "" : "&off=1"));
        ctx.Log("[数据包查看器] " + (on ? "已拦截" : "已解除拦截") + (dir == 1 ? " S→C " : " C→S ") + "槽位 #" + who + " 的 #" + id);
        _playersSig = "";
        await PollPlayersAsync();
    }

    private async Task PollRulesAsync()
    {
        IManagerContext? ctx = _ctx;
        if (ctx is null) { return; }
        try
        {
            using JsonDocument doc = JsonDocument.Parse(await ctx.RestGet("/packets/rules"));
            JsonElement r = doc.RootElement;
            bool auto = r.TryGetProperty("autoEnabled", out JsonElement ae) && ae.ValueKind == JsonValueKind.True;
            int max = r.TryGetProperty("autoMax", out JsonElement me) ? me.GetInt32() : 120;
            int win = r.TryGetProperty("autoWindowMs", out JsonElement we) ? we.GetInt32() : 1000;
            string act = r.TryGetProperty("autoAction", out JsonElement ac) ? (ac.GetString() ?? "warn") : "warn";
            long dropped = r.TryGetProperty("dropped", out JsonElement de) ? de.GetInt64() : 0;
            string watchTxt = JoinArr(r, "watch");
            var vlines = new List<string>();
            if (r.TryGetProperty("violations", out JsonElement vs) && vs.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement v in vs.EnumerateArray())
                {
                    if (vlines.Count >= 6) { break; }
                    string nm = Str(v, "name");
                    if (nm.Length == 0) { nm = "#" + (v.TryGetProperty("who", out JsonElement wi) ? wi.GetInt32() : 0); }
                    vlines.Add(nm + " 的 " + Str(v, "type") + " 达 " + (v.TryGetProperty("count", out JsonElement ce) ? ce.GetInt32() : 0) + " 包 → " + Str(v, "action"));
                }
            }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_violations is not null)
                {
                    _violations.Text = "关注名单 [" + watchTxt + "]   ·   违规 " + vlines.Count + " 条（累计丢包 " + dropped + "）：" + string.Join("   ·   ", vlines);
                    _violations.IsVisible = vlines.Count > 0 || watchTxt.Length > 0;
                }
                if (_limitOn is not null) { _limitOn.IsChecked = auto; }
                if (_limitMax is not null && _limitMax.IsFocused == false) { _limitMax.Text = max.ToString(); }
                if (_limitWindow is not null && _limitWindow.IsFocused == false) { _limitWindow.Text = win.ToString(); }
                if (_limitActionBox is not null) { _limitActionBox.SelectedIndex = act == "kick" ? 2 : act == "drop" ? 1 : 0; }
            });
        }
        catch { }
    }

    private async Task LogRulesAsync()
    {
        IManagerContext? ctx = _ctx;
        if (ctx is null) { return; }
        try
        {
            using JsonDocument doc = JsonDocument.Parse(await ctx.RestGet("/packets/rules"));
            JsonElement r = doc.RootElement;
            ctx.Log("[数据包查看器] 自动限流=" + (r.TryGetProperty("autoEnabled", out JsonElement ae) && ae.ValueKind == JsonValueKind.True)
                     + " max=" + (r.TryGetProperty("autoMax", out JsonElement me) ? me.GetInt32() : 0)
                     + " action=" + (r.TryGetProperty("autoAction", out JsonElement ac) ? ac.GetString() : "")
                     + " 已丢包=" + (r.TryGetProperty("dropped", out JsonElement de) ? de.GetInt64() : 0)
                     + " 受限=[" + JoinArr(r, "limitedPairs") + "] 封包=[" + JoinArr(r, "blockedPairs") + "] 解码=[" + JoinArr(r, "decodeIds") + "]");
        }
        catch (Exception ex) { ctx.Log("[数据包查看器] 读取规则失败: " + ex.Message); }
    }

    private async Task WatchAsync(string query)
    {
        IManagerContext? ctx = _ctx;
        if (ctx is null) { return; }
        try
        {
            await ctx.RestGet("/packets/config?" + query);
            await PollRulesAsync();
            ctx.Log("[数据包查看器] 关注名单已更新（关注的人即使没开全局抓包，也会单独抓全量原始包）");
        }
        catch (Exception ex) { ctx.Log("[数据包查看器] 更新关注名单失败: " + ex.Message); }
    }

    private async Task LogConfigAsync()
    {
        IManagerContext? ctx = _ctx;
        if (ctx is null) { return; }
        try
        {
            using JsonDocument doc = JsonDocument.Parse(await ctx.RestGet("/packets/config"));
            JsonElement r = doc.RootElement;
            ctx.Log("[数据包查看器] 配置文件 " + Str(r, "configPath")
                     + "；关注名单=[" + JoinArr(r, "watch") + "]"
                     + "；抓包=" + (r.TryGetProperty("capturing", out JsonElement c) && c.ValueKind == JsonValueKind.True)
                     + "；解码=[" + JoinArr(r, "decodeIds") + "]；封包=[" + JoinArr(r, "blocked") + "]");
        }
        catch (Exception ex) { ctx.Log("[数据包查看器] 读取配置失败: " + ex.Message); }
    }

    private static string JoinArr(JsonElement r, string name)
        => r.TryGetProperty(name, out JsonElement a) && a.ValueKind == JsonValueKind.Array
            ? string.Join(",", a.EnumerateArray().Select(x => x.ToString()))
            : "—";

    private async Task HandleCommandAsync(PluginCommandArgs args)
    {
        string sub = args.Args.Count > 0 ? args.Args[0].ToLowerInvariant() : "status";
        switch (sub)
        {
            case "players": case "agg": SetMode(true); await LogPlayersAsync(); break;
            case "raw": SetMode(false); break;
            case "on": SetMode(false); await StartCaptureAsync(); break;
            case "off": await StopCaptureAsync(); break;
            case "clear": await ClearAsync(); break;
            case "top": await LogTopAsync(); break;
            case "who": await LogPlayersAsync(); break;
            case "test": await InjectAsync(); break;
            case "rules": await LogRulesAsync(); break;
            case "limit":
                if (args.Args.Count >= 2 && (args.Args[1] is "on" or "off"))
                {
                    bool onoff = args.Args[1] == "on";
                    await Dispatcher.UIThread.InvokeAsync(() => { if (_limitOn is not null) { _limitOn.IsChecked = onoff; } });
                    await ApplyRulesAsync();
                }
                else { await LogRulesAsync(); }
                break;
            case "block":
                if (args.Args.Count >= 3 && int.TryParse(args.Args[1], out int bw) && int.TryParse(args.Args[2], out int bid)) { await BlockPairAsync(0, bw, bid, true); }
                else { _ctx?.Log("[数据包查看器] 用法: !packets block <槽位> <包ID>"); }
                break;
            case "unblock":
                if (args.Args.Count >= 3 && int.TryParse(args.Args[1], out int uw) && int.TryParse(args.Args[2], out int uid)) { await BlockPairAsync(0, uw, uid, false); }
                break;
            case "decode":
                if (args.Args.Count >= 2 && int.TryParse(args.Args[1], out int did)) { await DecodeTypeAsync(did, true); }
                else { _ctx?.Log("[数据包查看器] 用法: !packets decode <包ID>"); }
                break;
            case "watch":
                if (args.Args.Count >= 2) { await WatchAsync("watch=" + Uri.EscapeDataString(args.Args[1])); }
                else { await LogConfigAsync(); }
                break;
            case "watch-":
                if (args.Args.Count >= 2) { await WatchAsync("watchRemove=" + Uri.EscapeDataString(args.Args[1])); }
                break;
            case "watchclear": await WatchAsync("watchClear=1"); break;
            case "config": await LogConfigAsync(); break;
            default: await LogStatusAsync(); break;
        }
    }

    private async Task LogStatusAsync()
    {
        IManagerContext? ctx = _ctx;
        if (ctx is null) { return; }
        try
        {
            using JsonDocument md = JsonDocument.Parse(await ctx.RestGet("/packets/meta"));
            JsonElement r = md.RootElement;
            string text = "[数据包查看器] capturing=" + (r.TryGetProperty("capturing", out JsonElement c) && c.ValueKind == JsonValueKind.True)
                          + " agg=" + (r.TryGetProperty("aggEnabled", out JsonElement a) && a.ValueKind == JsonValueKind.True)
                          + " packetTypeCount=" + (r.TryGetProperty("packetTypeCount", out JsonElement pc) ? pc.GetInt32() : 0)
                          + " trackedPlayers=" + (r.TryGetProperty("trackedPlayers", out JsonElement tp) ? tp.GetInt32() : 0)
                          + " buffered=" + (r.TryGetProperty("buffered", out JsonElement b) ? b.GetInt32() : 0);
            ctx.Log(text);
            SetStatus(text, Body);      // ★ 走 SetStatus（内部 Dispatcher.Post）：原来直接 SetStatusText，是在后台线程改控件
        }
        catch (Exception ex) { ctx.Log("[数据包查看器] 读取状态失败: " + ex.Message); }
    }

    private async Task LogPlayersAsync()
    {
        IManagerContext? ctx = _ctx;
        if (ctx is null) { return; }
        try
        {
            using JsonDocument md = JsonDocument.Parse(await ctx.RestGet("/packets/players?onlineOnly=0"));
            JsonElement r = md.RootElement;
            ctx.Log("[数据包查看器] 玩家聚合（在线/总数："
                    + (r.TryGetProperty("players", out JsonElement pa) && pa.ValueKind == JsonValueKind.Array ? pa.GetArrayLength() : 0)
                    + " 条）：");
            if (r.TryGetProperty("players", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement p in arr.EnumerateArray())
                {
                    bool online = p.TryGetProperty("online", out JsonElement oe) && oe.ValueKind == JsonValueKind.True;
                    ctx.Log("   " + (online ? "●" : "○") + " #" + p.GetProperty("who").GetInt32() + " " + Str(p, "name")
                            + "  共 " + p.GetProperty("total").GetInt64() + " 包 / " + p.GetProperty("typeCount").GetInt32() + " 种类型");
                }
            }
        }
        catch (Exception ex) { ctx.Log("[数据包查看器] 读取玩家聚合失败: " + ex.Message); }
    }

    private async Task LogTopAsync()
    {
        IManagerContext? ctx = _ctx;
        if (ctx is null) { return; }
        try
        {
            using JsonDocument md = JsonDocument.Parse(await ctx.RestGet("/packets/stats"));
            JsonElement r = md.RootElement;
            long total = r.TryGetProperty("total", out JsonElement t) ? t.GetInt64() : 0;
            ctx.Log("[数据包查看器] 原始抓包共 " + total + " 个包；Top:");
            if (r.TryGetProperty("types", out JsonElement types) && types.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (JsonElement ty in types.EnumerateArray())
                {
                    if (i++ >= 15) { break; }
                    ctx.Log("   #" + ty.GetProperty("id").GetInt32() + " " + ty.GetProperty("name").GetString() + "  x" + ty.GetProperty("count").GetInt64());
                }
            }
        }
        catch (Exception ex) { ctx.Log("[数据包查看器] 读取统计失败: " + ex.Message); }
    }

    // ===== 渲染 =====

    private Control BuildPlayerCard(PlayerDto d)
    {
        string name = d.Name.Length == 0 ? "(未命名)" : d.Name;
        string lastStr = d.LastMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(d.LastMs).ToLocalTime().ToString("HH:mm:ss") : "-";
        string firstStr = d.FirstMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(d.FirstMs).ToLocalTime().ToString("HH:mm:ss") : "-";

        var sp = new StackPanel { Spacing = 2 };
        sp.Children.Add(new TextBlock
        {
            Text = (d.Dir == 1 ? "↑ S→C ⚠仅类型/计数   " : "↓ C→S   ") + (d.Online ? "● " : "○ ") + name + "   #" + d.Who + (d.World.Length == 0 ? "" : "   " + d.World)
                   + "   共 " + d.Total + " 包 · " + d.TypeCount + " 种类型   （首次 " + firstStr + " / 最近 " + lastStr + "）"
                   + (d.Online ? "" : "   ← 未在线（连接中 / 已离线残留）"),
            FontSize = 12.5, Foreground = d.Online ? Green : Warn, FontWeight = FontWeight.SemiBold,
        });

        int shown = 0;
        foreach (TypeDto t in d.Types)
        {
            if (shown >= MaxTypesPerPlayer)
            {
                sp.Children.Add(new TextBlock { Text = "      … 还有 " + Math.Max(0, d.TypeCount - shown) + " 种（用「导出 JSONL / players」看全量）", FontSize = 10.5, Foreground = Muted });
                break;
            }
            string tFirstStr = t.FirstMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(t.FirstMs).ToLocalTime().ToString("HH:mm:ss") : "-";
            string mark = ((t.Blocked ? "⊘" : t.Limited ? "⚠" : "·") + (t.DecodeOn && t.DecodeFailed ? "✗" : "")).Trim();   // 解码是否开启由按钮高亮表示
            string alt = (t.AltName.Length > 0 && t.AltName != t.Name) ? "(" + t.AltName + ")" : "";
            IBrush color = t.Blocked ? Err : t.Limited ? Warn : Body;
            bool noBytes = d.Dir == 1;   // S→C 走 NetMessage 层：只有类型/目标，没有线缆字节
            int id = t.Id;
            var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            // ★ 开启状态用「按钮高亮」表达，不再往文字上打 ✔（用户反馈打勾不好看）
            var dec = new Button { Content = "解码", FontSize = 10, Padding = new Thickness(7, 1), CornerRadius = new CornerRadius(6) };
            if (t.DecodeOn)
            {
                IBrush hl = t.DecodeFailed ? Warn : Accent;
                dec.Background = new SolidColorBrush(Color.Parse(t.DecodeFailed ? "#3A2E12" : "#123039"));
                dec.Foreground = hl;
                dec.BorderBrush = hl;
                dec.BorderThickness = new Thickness(1);
            }
            ToolTip.SetTip(dec, t.DecodeOn ? (t.DecodeFailed ? "已开启解码，但该类型解析失败（TrProtocol 定义与真实载荷不符）—— 点击关闭" : "已开启字段解码 —— 点击关闭") : "开启字段解码");
            dec.Click += async (_, _) => { try { await DecodeTypeAsync(id, !t.DecodeOn); } catch { } };
            var blk = new Button { Content = "拦截", FontSize = 10, Padding = new Thickness(7, 1), CornerRadius = new CornerRadius(6) };
            if (t.Blocked)
            {
                blk.Background = new SolidColorBrush(Color.Parse("#3A1E1E"));
                blk.Foreground = Err;
                blk.BorderBrush = Err;
                blk.BorderThickness = new Thickness(1);
            }
            ToolTip.SetTip(blk, t.Blocked ? "已拦截该方向/槽位的此类型 —— 点击解除" : "拦截该方向/槽位的此类型");
            blk.Click += async (_, _) => { try { await BlockPairAsync(d.Dir, d.Who, id, !t.Blocked); } catch { } };
            if (d.Dir != 1) { btns.Children.Add(dec); }   // S→C 无字节，解码按钮无意义
            btns.Children.Add(blk);
            // ★ 用真正的 Grid 列（列宽由布局决定，与字体/CJK 宽度无关），与表头 AggHeader() 共用 AggCols
            var row = new Grid { ColumnDefinitions = new Avalonia.Controls.ColumnDefinitions(AggCols + ",Auto") };
            void Cell(int col, string text, bool right = false, bool muted = false)
            {
                var tb = new TextBlock
                {
                    Text = text, FontSize = 11, FontFamily = Mono, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = muted ? Muted : color, TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 0, 10, 0),
                };
                if (right) { tb.TextAlignment = TextAlignment.Right; }
                Avalonia.Controls.Grid.SetColumn(tb, col);
                row.Children.Add(tb);
            }
            Cell(0, mark);
            Cell(1, "#" + id.ToString("D3"));
            Cell(2, t.Name + alt);
            Cell(3, t.Count.ToString(), true);
            Cell(4, noBytes ? "—" : t.Len + "B", true, noBytes);
            Cell(5, tFirstStr);
            Cell(6, noBytes ? "—" : t.Hex, false, noBytes);
            Avalonia.Controls.Grid.SetColumn(btns, 7);
            row.Children.Add(btns);
            stackRow(sp, row);

            if (t.Decoded.Length > 0)
            {
                sp.Children.Add(new TextBlock
                {
                    Text = "        ↳ " + t.Decoded,
                    FontSize = 11, FontFamily = Mono, Foreground = Accent, TextWrapping = TextWrapping.Wrap,
                });
            }
            shown++;
        }

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#17171B")),
            BorderBrush = new SolidColorBrush(Color.Parse("#2A2A30")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 8),
            Child = sp,
        };
    }

    private static void stackRow(StackPanel sp, Control c) => sp.Children.Add(c);

    // ★ 聚合卡片的列宽：表头与数据行共用 → 像素级对齐（等宽字体 + CJK 回退字体做不到这一点）
    // 前 6 列全部固定宽度（只有最后的 Payload 列自适应）—— 这样表头与数据行的列边界在像素上必然一致，
    // 不受两处容器可用宽度细微差别的影响（之前用 2*/3* 时，可用宽度差几像素就会让后面几列整体偏移）
    private const string AggCols = "16,48,220,72,52,84,*";

    private static Control AggHeader()
    {
        // Margin = 外层(BorderThickness 1 + Padding 10/8) + 卡片(BorderThickness 1 + Padding 12/8) = 24/22，
        // 少算任何一项表头都会与数据行错开（踩过两次：先漏卡片 padding、再漏两侧 BorderThickness）
        var g = new Grid { ColumnDefinitions = new Avalonia.Controls.ColumnDefinitions(AggCols + ",Auto"), Margin = new Thickness(24, 0, 22, 0) };
        void H(int col, string text, bool right = false)
        {
            var tb = new TextBlock
            {
                Text = text, FontSize = 11, FontFamily = Mono, Foreground = Muted,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
            };
            if (right) { tb.TextAlignment = TextAlignment.Right; }
            Avalonia.Controls.Grid.SetColumn(tb, col);
            g.Children.Add(tb);
        }
        H(0, "");
        H(1, "#ID");
        H(2, "包类型");
        H(3, "次数", true);
        H(4, "长度", true);
        H(5, "首次");
        H(6, "内容 / 解码字段");
        return g;
    }

    // ★ 命令处理器跑在线程池线程（PluginHost.cs:159 用 Task.Run），而 Avalonia 控件有线程亲和性：
    //   原来这里直接改 Children/Text，与 UI 线程的 AppendLines 并发 → 轻则丢行，重则设 AvaloniaProperty 时抛异常。
    private void ClearList() => Dispatcher.UIThread.Post(() =>
    {
        if (_listHost is not null) { _listHost.Children.Clear(); }
        if (_stats is not null) { _stats.Text = "—"; }
    });

    private void AppendLines(List<string> lines, List<IBrush> brushes)
    {
        if (_listHost is null) { return; }
        int n = Math.Min(lines.Count, MaxAppendPerPoll);
        for (int i = 0; i < n; i++)
        {
            _listHost.Children.Add(new TextBlock
            {
                Text = lines[i], FontSize = 11, FontFamily = Mono,
                Foreground = brushes[i], TextWrapping = TextWrapping.NoWrap,
            });
        }
        while (_listHost.Children.Count > MaxRows) { _listHost.Children.RemoveAt(0); }
        if (_autoScroll && _listScroll is not null) { _listScroll.ScrollToEnd(); }
        if (_stats is not null)
        {
            string hint = Volatile.Read(ref _polling) == 1 ? "" : "（未接收：点「▶ 开始抓包」）  ";
            _stats.Text = hint + "显示最近 " + _listHost.Children.Count + " 行（上限 " + MaxRows + "）；完整数据用「⇩ 导出 JSONL」";
        }
    }

    private void SetStatus(string text, IBrush brush) => Dispatcher.UIThread.Post(() => SetStatusText(text, brush));

    private void SetStatusText(string text, IBrush brush)
    {
        if (_status is null) { return; }
        _status.Text = text;
        _status.Foreground = brush;
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    private static string FormatLine(JsonElement p)
    {
        string time = "?";
        try
        {
            long ms = p.GetProperty("ms").GetInt64();
            time = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("HH:mm:ss.fff");
        }
        catch { }
        string world = Str(p, "world");
        string player = Str(p, "player");
        int id = p.TryGetProperty("id", out JsonElement ie) ? ie.GetInt32() : -1;
        string type = Str(p, "type");
        int len = p.TryGetProperty("len", out JsonElement le) ? le.GetInt32() : 0;
        string hex = Str(p, "hex");
        string dirMark = p.TryGetProperty("dir", out JsonElement de2) && de2.GetInt32() == 1 ? "↑" : "↓";
        return dirMark + " " + time + "  " + Fit(world.Length == 0 ? "-" : world, 8) + " " + Fit(player.Length == 0 ? "-" : player, 10)
               + " #" + id.ToString("D3") + " " + Fit(type, 24) + " " + len.ToString("D5") + "  " + hex;
    }

    // ==== 显示宽度工具：等宽字体下 CJK/emoji 占 2 列，按「字符数」补齐必然错位 ====
    private static bool IsWide(char c) =>
        (c >= 0x1100 && c <= 0x115F) || (c >= 0x2E80 && c <= 0xA4CF) || (c >= 0xAC00 && c <= 0xD7A3) ||
        (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFE30 && c <= 0xFE6F) || (c >= 0xFF00 && c <= 0xFF60) ||
        (c >= 0xFFE0 && c <= 0xFFE6) || (c >= 0x3000 && c <= 0x303E);

    private static int DispWidth(string s)
    {
        int w = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { w += 2; i++; continue; }
            w += IsWide(c) ? 2 : 1;
        }
        return w;
    }

    private static string PadDisp(string s, int width)
    {
        int w = DispWidth(s);
        return w >= width ? s : s + new string(' ', width - w);
    }

    private static string PadLeftDisp(string s, int width)
    {
        int w = DispWidth(s);
        return w >= width ? s : new string(' ', width - w) + s;
    }

    /// <summary>按显示宽度补齐；超宽时按显示宽度截断（不劈开代理对）。</summary>
    private static string Fit(string s, int width)
    {
        int w = DispWidth(s);
        if (w <= width) { return s + new string(' ', width - w); }
        var sb = new StringBuilder();
        int acc = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                if (acc + 2 > width - 1) { break; }
                sb.Append(c).Append(s[i + 1]);
                i++;
                acc += 2;
                continue;
            }
            int cw = IsWide(c) ? 2 : 1;
            if (acc + cw > width - 1) { break; }
            sb.Append(c);
            acc += cw;
        }
        sb.Append('…');
        acc++;
        if (acc < width) { sb.Append(new string(' ', width - acc)); }
        return sb.ToString();
    }

    private static IBrush BrushFor(JsonElement p)
    {
        string type = Str(p, "type");
        if (type.Contains("Request", StringComparison.OrdinalIgnoreCase)) { return Warn; }
        if (type.Contains("Sync", StringComparison.OrdinalIgnoreCase) || type.Contains("Player", StringComparison.OrdinalIgnoreCase)) { return Green; }
        return Body;
    }

    private static Border Sep() => new()
    {
        Width = 1, Height = 18, Margin = new Thickness(2, 0),
        Background = new SolidColorBrush(Color.Parse("#2A2A30")),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static Button Tip(Button b, string tip) { ToolTip.SetTip(b, tip); return b; }

    private static Control BuildHelp()
    {
        var sp = new StackPanel { Spacing = 6, Margin = new Thickness(0, 6, 0, 0) };
        void Add(string s) => sp.Children.Add(new TextBlock { Text = s, FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap });
        Add("聚合：同一个「玩家 × 包类型」只登记一次（含首次长度/前几字节），之后只加计数 —— 刷屏包不会把列表刷爆。玩家下线即移除；超过 TTL 且不在线的槽位也会清掉。");
        Add("方向：C→S = 客户端发来的包（有字节，可解码）；S→C = 服务端发出的包（NetMessage 层，仅类型/计数/目标，无字节）。");
        Add("标记：⚠ 已限流 · ⊘ 已封包 · ✗ 已开解码但该类型解析失败（TrProtocol 定义与真实载荷不符） · ↓/↑ 方向 · ●/○ 在线/离线");
Add("「解码」「拦截」按钮高亮 = 该项已开启（蓝=解码中，黄=解码失败，红=已拦截），再点一次即关闭。");
        Add("游戏内：/npk on|off|players|rules|limit|block|unblock|decode|watch|top");
        Add("REST：/packets/{players,rules,decode,block,feed,scan,config}（服务端插件 NetPacketChannel）");
        return new Expander { Header = "说明 / 命令 / 标记", IsExpanded = false, FontSize = 11.5, Content = sp };
    }

    private static TextBlock Lbl(string t) => new()
    {
        Text = t, FontSize = 11.5, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center,
    };

    private static TextBox Tb(double w, string placeholder) => new()
    {
        Width = w, FontSize = 11.5, PlaceholderText = placeholder,
    };

    private static Button BtnAsync(string text, Func<Task> onClick)
    {
        var b = new Button { Content = text, FontSize = 11.5, Padding = new Thickness(10, 5), CornerRadius = new CornerRadius(8) };
        b.Click += async (_, _) =>
        {
            try { await onClick(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[数据包查看器] " + ex.Message); }
        };
        return b;
    }
}
