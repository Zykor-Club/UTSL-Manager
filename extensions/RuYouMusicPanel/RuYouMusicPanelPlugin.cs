using System;
using System.Collections.Generic;
using System.Linq;
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

namespace RuYouMusicPanel;

/// <summary>
/// RuYouMusicPanel —— 管理器侧「点歌机面板」。
///   数据来源：服务端插件 RuYouMusic 的 REST（/music/status /clients /queue /lyrics /search /enqueue ...）
///   能力：现在播放 + 进度、装了客户端插件的玩家清单、滚动歌词（网易云那种居中高亮）、点歌搜索、队列、切歌/停止/音量。
/// </summary>
public sealed class RuYouMusicPanelPlugin : IManagerPlugin
{
    public string Name => "点歌机面板";
    public string Description => "点歌队列 / 已装客户端插件的玩家 / 滚动歌词（配套服务端插件 RuYouMusic）";
    public string Author => "星梦";
    public string Icon => "♪";   // 单色音符（与管理器其它插件的几何符号风格一致）

    // 调色板（沿用管理器风格）
    private static readonly IBrush Fg = new SolidColorBrush(Color.Parse("#F2F2F5"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#7C7C82"));
    private static readonly IBrush Body = new SolidColorBrush(Color.Parse("#B9B9C0"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#66CCFF"));
    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#7EE787"));
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#E3B341"));
    private static readonly IBrush Err = new SolidColorBrush(Color.Parse("#FF7B72"));
    private static readonly FontFamily Mono = new("Cascadia Mono,Consolas,monospace");

    private IManagerContext? _ctx;
    private readonly object _sync = new();

    // ===== 后台线程写、UI 线程读的数据 =====
    private bool _playing;
    private long _trackId = -1;
    private string _trackName = "";
    private string _trackArtist = "";
    private string _trackBy = "";
    private long _durationMs;
    private long _serverPosMs;
    private DateTime _serverPosAt = DateTime.MinValue;
    private int _volume = 80;
    private int _queueCount;
    private int _capableCount;
    private int _onlineCount;
    private List<KeyValuePair<double, string>> _lines = new();
    private List<ClientRow> _clients = new();
    private List<QueueRow> _queue = new();
    private string _status = "等待服务端…";
    private string _searchHint = "";
    private List<SearchRow> _search = new();

    private sealed record ClientRow(string Name, bool Has, int Version, long LastSeenMs, long PositionMs, string LastEvent, bool Failed, long LastEventMs, string LastMessage);
    private sealed record QueueRow(int Index, long Id, string Name, string Artist, string By);
    private sealed record SearchRow(long Id, string Name, string Artist, bool Playable);
    private sealed record FailRow(long AgoMs, string Player, string Song, string Stage, string Reason);
    private List<FailRow> _fails = new();
    private string _lastFailSig = "\u0000";

    // ===== 控件 =====
    private TextBlock? _nowTitle;
    private TextBlock? _nowSub;
    private TextBlock? _posText;
    private ProgressBar? _posBar;
    private TextBlock? _clientsText;
    private StackPanel? _clientsHost;
    private ScrollViewer? _lyricScroll;
    private StackPanel? _lyricHost;
    private StackPanel? _queueHost;
    private StackPanel? _failHost;
    private TextBlock? _queueTitle;
    private TextBox? _searchBox;
    private TextBlock? _busy;
    private DispatcherTimer? _busyTimer;
    private DispatcherTimer? _lyricTimer;      // 歌词插值定时器（原来存局部变量，OnUnload 时无法停止）
    private Control? _root;      // ★ 用于判断面板是否可见
    private int _spinFrame;
    private static readonly string[] Spin = { "◐", "◓", "◑", "◒" };   // 圆形 spinner（Segoe UI Symbol 里有这些字形）
    private StackPanel? _searchHost;
    private TextBlock? _statusText;
    private Slider? _vol;
    private TextBlock? _volText;
    private readonly List<TextBlock> _lyricCtl = new();
    private int _lastLyricIndex = -2;
    private int tickLyric;
    private string _lastClientSig = "\u0000";
    private string _lastQueueSig = "\u0000";
    private string _lastSearchSig = "\u0000";

    // ============================================================
    // 生命周期
    // ============================================================

    public void OnLoad(IManagerContext ctx)
    {
        _ctx = ctx;
        ctx.RegisterMetric("点歌 · 队列", "首", () => _queueCount);
        ctx.RegisterMetric("点歌 · 装插件玩家", "人", () => _capableCount);
        ctx.RegisterMetric("点歌 · 在线", "人", () => _onlineCount);
        ctx.RegisterStatus("点歌机", () => _playing ? ("播放中: " + _trackName) : "空闲");
        ctx.RunBackground(PollLoopAsync, "点歌机轮询");
    }

    // ★ 两个 UI 定时器必须停：DispatcherTimer 强引用 Tick 委托 → 委托指向插件实例 →
    //   alc.Unload() 收不回 ALC（热重载内存泄漏），而且卸载后还每 250ms 空转。
    public void OnUnload() { _ctx = null; _lyricTimer?.Stop(); _busyTimer?.Stop(); }

    public Control? CreateView(IManagerContext ctx)
    {
        _ctx = ctx;
        var root = new StackPanel { Spacing = 10 };
        _root = root;   // ★ 记录根视图

        // ---------- ① 点歌（搜索） ----------
        var seCard = Card();
        var seSp = new StackPanel { Spacing = 6 };
        var seRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        _searchBox = new TextBox { PlaceholderText = "歌曲名或歌手", Width = 320, FontSize = 12 };
        seRow.Children.Add(_searchBox);
        seRow.Children.Add(Btn("搜索", () => DoSearch()));
        // ★ 忙碌指示：圆形转圈（搜索/点歌这类要等接口的操作给个可见反馈）
        _busy = new TextBlock
        {
            Text = Spin[0], FontSize = 15, Width = 20, IsVisible = false,
            Foreground = Accent, FontFamily = new FontFamily("Segoe UI Symbol,Segoe UI"),
            VerticalAlignment = VerticalAlignment.Center
        };
        seRow.Children.Add(_busy);
        seSp.Children.Add(seRow);
        _searchHost = new StackPanel { Spacing = 2 };
        seSp.Children.Add(_searchHost);
        seCard.Child = seSp;
        root.Children.Add(seCard);

        // ---------- ② 待播放队列 ----------
        var qCard = Card();
        var qSp = new StackPanel { Spacing = 6 };
        _queueTitle = new TextBlock { Text = "待播放", FontSize = 12.5, Foreground = Fg };
        qSp.Children.Add(_queueTitle);
        var qHead = new Grid { ColumnDefinitions = new ColumnDefinitions("30,*,150,110,Auto") };
        AddCell(qHead, 1, "歌曲", Muted, true);
        AddCell(qHead, 2, "歌手", Muted, true);
        AddCell(qHead, 3, "点歌人", Muted, true);
        qSp.Children.Add(qHead);
        _queueHost = new StackPanel { Spacing = 2 };
        qSp.Children.Add(_queueHost);
        qCard.Child = qSp;
        root.Children.Add(qCard);

        // ---------- ③ 播放进度 ----------
        var nowCard = Card();
        var nowSp = new StackPanel { Spacing = 6 };
        _nowTitle = new TextBlock { Text = "暂无播放", FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Fg, TextTrimming = TextTrimming.CharacterEllipsis };
        _nowSub = new TextBlock { Text = "空闲", FontSize = 11.5, Foreground = Muted };
        nowSp.Children.Add(_nowTitle);
        nowSp.Children.Add(_nowSub);
        var posRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        _posText = new TextBlock { Text = "00:00 / 00:00", FontSize = 11.5, FontFamily = Mono, Foreground = Body, VerticalAlignment = VerticalAlignment.Center };
        _posBar = new ProgressBar { Minimum = 0, Maximum = 1000, Value = 0, Height = 6, Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        var ctl = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        ctl.Children.Add(Btn("切歌", () => Act("/music/skip")));
        ctl.Children.Add(Btn("停止", () => Act("/music/stop")));
        ctl.Children.Add(Btn("刷新", () => RefreshNow()));
        Grid.SetColumn(_posText, 0); Grid.SetColumn(_posBar, 1); Grid.SetColumn(ctl, 2);
        posRow.Children.Add(_posText); posRow.Children.Add(_posBar); posRow.Children.Add(ctl);
        nowSp.Children.Add(posRow);
        var volRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        volRow.Children.Add(new TextBlock { Text = "音量", FontSize = 11.5, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
        _vol = new Slider { Minimum = 0, Maximum = 100, Value = _volume, Width = 160, VerticalAlignment = VerticalAlignment.Center };
        _volText = new TextBlock { Text = _volume + "%", FontSize = 11.5, FontFamily = Mono, Foreground = Body, VerticalAlignment = VerticalAlignment.Center };
        _vol.PropertyChanged += (_, ev) =>
        {
            if (ev.Property == RangeBase.ValueProperty && _volText is not null) { _volText.Text = (int)_vol.Value + "%"; }
        };
        _vol.PointerCaptureLost += (_, _) => Act("/music/volume?pct=" + (int)(_vol?.Value ?? 80));
        volRow.Children.Add(_vol);
        volRow.Children.Add(_volText);
        nowSp.Children.Add(volRow);
        nowCard.Child = nowSp;
        root.Children.Add(nowCard);

        // ---------- ④ 歌词 ----------
        var lyCard = Card();
        var lySp = new StackPanel { Spacing = 6 };
        lySp.Children.Add(new TextBlock { Text = "歌词", FontSize = 12.5, Foreground = Fg });
        _lyricHost = new StackPanel { Spacing = 2 };
        _lyricScroll = new ScrollViewer { Height = 280, Content = _lyricHost, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, AllowAutoHide = false, Padding = new Thickness(0, 0, 13, 0), Background = null };
        _lyricScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;   // 歌词不需要横向滚动条
        lySp.Children.Add(_lyricScroll);
        lyCard.Child = lySp;   // 槽位交给 App 级 ScrollBar 样式（不再自绘，避免与主题槽位并存）
        root.Children.Add(lyCard);

        // ---------- ⑤ 装了客户端插件的玩家 ----------
        var clCard = Card();
        var clSp = new StackPanel { Spacing = 6 };
        _clientsText = new TextBlock { Text = "客户端插件：—", FontSize = 12.5, Foreground = Fg };
        clSp.Children.Add(_clientsText);
        var clHead = new Grid { ColumnDefinitions = new ColumnDefinitions("26,*,60,2*") };
        AddCell(clHead, 1, "玩家", Muted, true);
        AddCell(clHead, 2, "版本", Muted, true);
        AddCell(clHead, 3, "状态（含失败原因）", Muted, true);
        clSp.Children.Add(clHead);
        _clientsHost = new StackPanel { Spacing = 2 };
        clSp.Children.Add(_clientsHost);
        clSp.Children.Add(new TextBlock { Text = "最近失败", FontSize = 12, Foreground = Muted, Margin = new Thickness(0, 8, 0, 0) });
        _failHost = new StackPanel { Spacing = 2 };
        clSp.Children.Add(_failHost);
        clCard.Child = clSp;
        root.Children.Add(clCard);

        _statusText = new TextBlock { Text = "", FontSize = 11, Foreground = Muted };
        root.Children.Add(_statusText);

        // 歌词高亮用的插值定时器（UI 线程）
        _lyricTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        // ★ 性能：面板不可见就不做歌词插值（原来隐藏时也每秒 4 次重排 UI）
        _lyricTimer.Tick += (_, _) => { if (_root?.IsEffectivelyVisible == true) { UpdateHighlight(); } };
        _lyricTimer.Start();

        return root;
    }

    // ============================================================
    // 轮询
    // ============================================================

    private async Task PollLoopAsync(CancellationToken ct)
    {
        int tick = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }
            tick++;
            try
            {
                await PollStatusAsync();
                if (tick % 2 == 0) { await PollClientsAsync(); }
                if (tick % 4 == 1) { await PollQueueAsync(); }
                if (tick % 4 == 3) { await PollFailuresAsync(); }
                await Dispatcher.UIThread.InvokeAsync(Render);
            }
            catch (Exception ex) { SetStatus("轮询失败: " + ex.Message); }
        }
    }

    private async Task<string> Rest(string path)
    {
        IManagerContext? c = _ctx;
        if (c is null) { return ""; }
        return await c.RestGet(path);
    }

    private async Task PollStatusAsync()
    {
        string t = await Rest("/music/status");
        if (t.Length == 0) { SetStatus("服务端插件 RuYouMusic 无响应（/music/status 为空）"); return; }
        using JsonDocument md = JsonDocument.Parse(t);
        JsonElement r = md.RootElement;
        long prevId;
        lock (_sync) { prevId = _trackId; }
        long id = -1;
        string name = "", artist = "", by = "";
        long dur = 0;
        bool playing = Bool(r, "playing");
        if (r.TryGetProperty("track", out JsonElement tr) && tr.ValueKind == JsonValueKind.Object)
        {
            id = Long(tr, "id");
            name = Str(tr, "name");
            artist = Str(tr, "artist");
            by = Str(tr, "by");
            dur = Long(tr, "durationMs");
        }
        lock (_sync)
        {
            _playing = playing;
            _trackId = id; _trackName = name; _trackArtist = artist; _trackBy = by;
            _durationMs = dur;
            _serverPosMs = Long(r, "positionMs");
            _serverPosAt = DateTime.UtcNow;
            _volume = (int)Long(r, "volumePercent");
            _queueCount = (int)Long(r, "queueCount");
        }
        bool needLyric;
        lock (_sync) { needLyric = id != prevId || (playing && _lines.Count == 0 && _trackId == id); }
        if (needLyric && (id != prevId || tickLyric++ % 3 == 0))
        {
            string ly = await Rest("/music/lyrics");
            var lines = new List<KeyValuePair<double, string>>();
            if (ly.Length > 0)
            {
                try
                {
                    using JsonDocument lm = JsonDocument.Parse(ly);
                    if (lm.RootElement.TryGetProperty("lines", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement e in arr.EnumerateArray())
                        {
                            lines.Add(new KeyValuePair<double, string>(Long(e, "ms"), Str(e, "text")));
                        }
                    }
                }
                catch { }
            }
            lock (_sync) { _lines = lines; }
            ClearStatus();
        }
    }

    private async Task PollClientsAsync()
    {
        string t = await Rest("/music/clients");
        if (t.Length == 0) { return; }
        using JsonDocument md = JsonDocument.Parse(t);
        JsonElement r = md.RootElement;
        var list = new List<ClientRow>();
        if (r.TryGetProperty("clients", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement e in arr.EnumerateArray())
            {
                list.Add(new ClientRow(Str(e, "name"), Bool(e, "hasPlugin"), (int)Long(e, "version"), Long(e, "lastSeenMs"), Long(e, "positionMs"),
                    Str(e, "lastEvent"), Bool(e, "lastEventFailed"), Long(e, "lastEventMs"), Str(e, "lastMessage")));
            }
        }
        lock (_sync)
        {
            _clients = list;
            _onlineCount = (int)Long(r, "total");
            // ★ 指标与表格保持一致：只算「在线且已装」的人数（服务端的 capable 含已离线的历史握手）
            _capableCount = list.Count(x => x.Has);
        }
    }

    private async Task PollQueueAsync()
    {
        string t = await Rest("/music/queue");
        if (t.Length == 0) { return; }
        using JsonDocument md = JsonDocument.Parse(t);
        JsonElement r = md.RootElement;
        var list = new List<QueueRow>();
        if (r.TryGetProperty("queue", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement e in arr.EnumerateArray())
            {
                list.Add(new QueueRow((int)Long(e, "index"), Long(e, "id"), Str(e, "name"), Str(e, "artist"), Str(e, "by")));
            }
        }
        lock (_sync) { _queue = list; }
    }

    private async Task PollFailuresAsync()
    {
        string t = await Rest("/music/failures?limit=6");
        if (t.Length == 0) { return; }
        using JsonDocument md = JsonDocument.Parse(t);
        var list = new List<FailRow>();
        if (md.RootElement.TryGetProperty("failures", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement e in arr.EnumerateArray())
            {
                list.Add(new FailRow(Long(e, "agoMs"), Str(e, "player"), Str(e, "song"), Str(e, "stage"), Str(e, "reason")));
            }
        }
        lock (_sync) { _fails = list; }
    }

    private void RefreshNow() { SetStatus("手动刷新…"); _ = Task.Run(async () => { try { await PollStatusAsync(); await PollClientsAsync(); await PollQueueAsync(); await Dispatcher.UIThread.InvokeAsync(Render); } catch { } }); }

    // ============================================================
    // 渲染（UI 线程）
    // ============================================================

    private void Render()
    {
        bool playing;
        long id, dur, pos, posAgeMs;
        string name, artist, by;
        int vol, online, capable;
        List<ClientRow> clients;
        List<QueueRow> queue;
        List<KeyValuePair<double, string>> lines;
        lock (_sync)
        {
            playing = _playing; id = _trackId; name = _trackName; artist = _trackArtist; by = _trackBy;
            dur = _durationMs; pos = _serverPosMs; posAgeMs = (long)(DateTime.UtcNow - _serverPosAt).TotalMilliseconds;
            vol = _volume; online = _onlineCount; capable = _capableCount; clients = _clients; queue = _queue; lines = _lines;
        }

        if (_nowTitle is not null)
        {
            _nowTitle.Text = playing && name.Length > 0 ? (name + "  —  " + artist) : (playing ? "播放中" : "暂无播放");
            _nowSub!.Text = playing
                ? ("点歌人: " + (by.Length == 0 ? "—" : by) + "   ·   队列 " + queue.Count + " 首")
                : "空闲";
        }
        if (_vol is not null && Math.Abs(_vol.Value - vol) > 0.5) { _vol.Value = vol; }
        if (_volText is not null) { _volText.Text = vol + "%"; }

        // 客户端安装情况
        if (_clientsText is not null)
        {
            int has = clients.Count(c => c.Has);
            int fails = clients.Count(x => x.Failed);
            _clientsText.Text = "客户端插件：已装 " + has + " / 在线 " + clients.Count + " 人" + (fails > 0 ? "    ⚠ " + fails + " 人播放异常" : "");
        }
        string csig = string.Join("|", clients.Select(c => c.Name + c.Has + c.Version + c.LastEvent + c.LastMessage));
        if (_clientsHost is not null && csig != _lastClientSig)
        {
            _lastClientSig = csig;
            _clientsHost.Children.Clear();
            if (clients.Count == 0) { _clientsHost.Children.Add(Txt("（无人在线）", Muted, 11.5)); }
            foreach (ClientRow c in clients.OrderByDescending(x => x.Has).ThenBy(x => x.Name))
            {
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("26,*,60,2*") };
                var dot = new TextBlock { Text = c.Has ? (c.Failed ? "!" : "✓") : "✗", FontSize = 12, Foreground = c.Has ? (c.Failed ? Warn : Green) : Muted, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(dot, 0); g.Children.Add(dot);
                var nm = new TextBlock { Text = c.Name, FontSize = 11.5, FontFamily = Mono, Foreground = c.Has ? Fg : Body, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(nm, 1); g.Children.Add(nm);
                var ver = new TextBlock { Text = c.Has ? ("v" + c.Version) : "—", FontSize = 11, FontFamily = Mono, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(ver, 2); g.Children.Add(ver);
                string ev;
                if (!c.Has) { ev = "未握手"; }
                else if (c.LastEvent.Length == 0) { ev = c.LastSeenMs < 0 ? "—" : ("已连接 " + (c.LastSeenMs / 1000) + "s"); }
                else { ev = c.LastEvent + (c.Failed && c.LastMessage.Length > 0 ? ": " + c.LastMessage : "") + (c.LastEventMs > 0 ? "  " + (c.LastEventMs / 1000) + "s前" : ""); }
                var seen = new TextBlock { Text = ev, FontSize = 11, FontFamily = Mono, Foreground = c.Failed ? Warn : Muted, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) };
                Grid.SetColumn(seen, 3); g.Children.Add(seen);
                _clientsHost.Children.Add(g);
            }
        }

        // 歌词重建（歌变了才重建控件）
        string lsig = id + "|" + lines.Count;
        if (_lyricHost is not null && _lyricHost.Tag as string != lsig)
        {
            _lyricHost.Tag = lsig;
            _lyricHost.Children.Clear();
            _lyricCtl.Clear();
            _lastLyricIndex = -2;
            if (lines.Count == 0)
            {
                _lyricHost.Children.Add(Txt(playing ? "（这首歌没有歌词，或还没取到）" : "（没有播放）", Muted, 11.5));
            }
            foreach (KeyValuePair<double, string> l in lines)
            {
                var tb = new TextBlock { Text = l.Value, FontSize = 11.5, Foreground = Body, TextWrapping = TextWrapping.Wrap };
                _lyricCtl.Add(tb);
                _lyricHost.Children.Add(tb);
            }
        }

        // 队列
        string qsig = string.Join("|", queue.Select(q => q.Index + q.Name + q.By));
        if (_queueTitle is not null) { _queueTitle.Text = "待播放（" + queue.Count + " 首）"; }
        if (_queueHost is not null && qsig != _lastQueueSig)
        {
            _lastQueueSig = qsig;
            _queueHost.Children.Clear();
            if (queue.Count == 0) { _queueHost.Children.Add(Txt("（队列空）", Muted, 11.5)); }
            foreach (QueueRow q in queue)
            {
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("30,*,150,90,Auto") };
                AddCell(g, 0, (q.Index + 1).ToString("D2"), Muted, true, Mono);
                AddCell(g, 1, q.Name, Body, false, Mono);
                AddCell(g, 2, q.Artist, Muted, false, Mono);
                AddCell(g, 3, "by " + (q.By.Length == 0 ? "—" : q.By), Muted, false, Mono);
                int idx = q.Index;
                var del = Btn("移除", () => Act("/music/remove?index=" + idx));
                del.FontSize = 10.5;
                Grid.SetColumn(del, 4);
                g.Children.Add(del);
                _queueHost.Children.Add(g);
            }
        }

        // 搜索结果
        List<SearchRow> search;
        string hint;
        lock (_sync) { search = _search; hint = _searchHint; }
        string ssig = hint + "|" + string.Join("|", search.Select(s => s.Id + s.Name));
        if (_searchHost is not null && ssig != _lastSearchSig)
        {
            _lastSearchSig = ssig;
            _searchHost.Children.Clear();
            if (hint.Length > 0) { _searchHost.Children.Add(Txt(hint, Muted, 11)); }
            foreach (SearchRow s in search)
            {
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,150,70,Auto") };
                AddCell(g, 0, s.Name, Body, false, Mono);
                AddCell(g, 1, s.Artist, Muted, false, Mono);
                AddCell(g, 2, s.Playable ? "可播放" : "受限", s.Playable ? Green : Warn, false, Mono);
                var add = Btn("点歌", () => Act("/music/enqueue?id=" + s.Id + "&name=" + Uri.EscapeDataString((s.Name + " " + s.Artist).Trim()) + "&by=" + Uri.EscapeDataString("管理器")));
                add.FontSize = 10.5;
                Grid.SetColumn(add, 3);
                g.Children.Add(add);
                _searchHost.Children.Add(g);
            }
        }

        // 最近失败
        List<FailRow> failList;
        lock (_sync) { failList = _fails; }
        string fsig = string.Join("|", failList.Select(f => f.AgoMs / 5000 + f.Player + f.Song + f.Reason));
        if (_failHost is not null && fsig != _lastFailSig)
        {
            _lastFailSig = fsig;
            _failHost.Children.Clear();
            if (failList.Count == 0) { _failHost.Children.Add(Txt("（无）", Muted, 11.5)); }
            foreach (FailRow f in failList)
            {
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("60,90,2*,3*") };
                AddCell(g, 0, (f.AgoMs / 1000) + "s前", Muted, false, Mono);
                AddCell(g, 1, f.Player.Length == 0 ? "—" : f.Player, Body, false, Mono);
                AddCell(g, 2, f.Song, Body, false, Mono);
                string why = f.Reason.Length == 0 ? f.Stage : (f.Reason.StartsWith(f.Stage) ? f.Reason : f.Stage + ": " + f.Reason);
                AddCell(g, 3, why, Warn, false, Mono);
                _failHost.Children.Add(g);
            }
        }

        UpdateHighlight();
    }

    /// <summary>按服务端进度 + 本地插值，居中高亮当前歌词行。</summary>
    private void UpdateHighlight()
    {
        bool playing;
        long dur, pos;
        lock (_sync)
        {
            playing = _playing; dur = _durationMs; pos = _serverPosMs;
            if (playing) { pos += (long)(DateTime.UtcNow - _serverPosAt).TotalMilliseconds; }
        }
        if (pos < 0) { pos = 0; }
        // ★ 进度必须在歌词判空之前更新（原先歌词为空时直接 return，导致进度条永远不动）
        if (_posText is not null) { _posText.Text = Fmt(pos) + " / " + (dur > 0 ? Fmt(dur) : "--:--"); }
        if (_posBar is not null) { _posBar.Value = dur > 0 ? Math.Clamp(pos * 1000.0 / dur, 0, 1000) : 0; }
        if (_lyricScroll is null || _lyricCtl.Count == 0) { return; }

        List<KeyValuePair<double, string>> lines;
        lock (_sync) { lines = _lines; }
        int idx = -1;
        for (int i = 0; i < lines.Count; i++) { if (pos >= lines[i].Key) { idx = i; } else { break; } }
        if (idx == _lastLyricIndex) { return; }
        _lastLyricIndex = idx;
        for (int i = 0; i < _lyricCtl.Count && i < lines.Count; i++)
        {
            TextBlock tb = _lyricCtl[i];
            if (i == idx) { tb.Foreground = Accent; tb.FontSize = 13.5; tb.FontWeight = FontWeight.SemiBold; }
            else { tb.Foreground = Body; tb.FontSize = 11.5; tb.FontWeight = FontWeight.Normal; }
        }
        if (idx >= 0 && idx < _lyricCtl.Count)
        {
            double lineH = 22;
            double target = idx * lineH - _lyricScroll.Viewport.Height / 2 + lineH;
            _lyricScroll.Offset = new Vector(0, Math.Max(0, target));
        }
    }

    // ============================================================
    // 交互
    // ============================================================

    private async void DoSearch()
    {
        string kw = _searchBox?.Text?.Trim() ?? "";
        if (kw.Length == 0) { return; }
        SetBusy(true);
        SetStatus("搜索中: " + kw + " …（正在逐首确认可播放性，请稍候）");
        try
        {
            string t = await Rest("/music/search?q=" + Uri.EscapeDataString(kw) + "&count=8&check=1");
            var list = new List<SearchRow>();
            if (t.Length > 0)
            {
                using JsonDocument md = JsonDocument.Parse(t);
                if (md.RootElement.TryGetProperty("results", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement e in arr.EnumerateArray())
                    {
                        list.Add(new SearchRow(Long(e, "id"), Str(e, "name"), Str(e, "artist"), Bool(e, "playable")));
                    }
                }
            }
            int okN = list.Count(x => x.Playable);
            lock (_sync) { _search = list; _searchHint = list.Count == 0 ? ("没有搜到: " + kw) : ("搜索 “" + kw + "” 共 " + list.Count + " 条（可直接播放 " + okN + " 条；受限的会自动换同名可播放版本）："); }
            SetStatus("搜索完成: " + list.Count + " 条");
            Render();
        }
        catch (Exception ex) { SetStatus("搜索失败: " + ex.Message); }
        finally { SetBusy(false); }
    }

    private async void Act(string path)
    {
        SetBusy(true);
        SetStatus("正在处理…");
        try
        {
            string t = await Rest(path);
            // ★ 显示服务端返回的 message（点歌失败时用户才知道原因，而不是只看到「已执行」）
            string msg = "";
            try { if (t.Length > 0) { using JsonDocument md = JsonDocument.Parse(t); msg = Str(md.RootElement, "message"); } } catch { }
            SetStatus(msg.Length > 0 ? msg : (t.Length > 0 ? ("已执行 " + path) : (path + " 无响应")));
            await PollStatusAsync();
            await PollQueueAsync();
            Render();
        }
        catch (Exception ex) { SetStatus("操作失败: " + ex.Message); }
        finally { SetBusy(false); }
    }

    // ============================================================
    // 小工具
    // ============================================================

    private static Border Card() => new()
    {
        Background = new SolidColorBrush(Color.Parse("#101013")),
        BorderBrush = new SolidColorBrush(Color.Parse("#2A2A30")),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(12, 10)
    };

    private static Button Btn(string text, Action onClick)
    {
        var b = new Button { Content = text, FontSize = 11.5, Padding = new Thickness(10, 4), CornerRadius = new CornerRadius(8) };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static TextBlock Txt(string text, IBrush brush, double size) => new() { Text = text, FontSize = size, Foreground = brush, TextWrapping = TextWrapping.Wrap };

    private static void AddCell(Grid g, int col, string text, IBrush brush, bool header, FontFamily? mono = null)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = header ? 11 : 11.5,
            Foreground = brush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 8, 0)
        };
        if (mono is not null) { tb.FontFamily = mono; }
        Grid.SetColumn(tb, col);
        g.Children.Add(tb);
    }

    private void SetStatus(string s)
    {
        lock (_sync) { _status = s; }
        try { Dispatcher.UIThread.Post(() => { if (_statusText is not null) { _statusText.Text = s; } }); } catch { }
    }

    private void ClearStatus() { }

    /// <summary>显示/隐藏圆形转圈（可见时每 140ms 换一帧）。</summary>
    private void SetBusy(bool on)
    {
        if (_busy is null) { return; }
        _busy.IsVisible = on;
        if (!on) { _busyTimer?.Stop(); return; }
        if (_busyTimer is null)
        {
            _busyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
            _busyTimer.Tick += (_, _) =>
            {
                if (_busy is null) { return; }
                _spinFrame = (_spinFrame + 1) % Spin.Length;
                _busy.Text = Spin[_spinFrame];
            };
        }
        _busy.Text = Spin[_spinFrame];
        _busyTimer.Start();
    }

    private static string Fmt(long ms)
    {
        if (ms < 0) { ms = 0; }
        long s = ms / 1000;
        return (s / 60).ToString("D2") + ":" + (s % 60).ToString("D2");
    }

    private static string Str(JsonElement e, string key)
    {
        try { return e.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : ""; } catch { return ""; }
    }

    private static long Long(JsonElement e, string key)
    {
        try
        {
            if (!e.TryGetProperty(key, out JsonElement v)) { return 0; }
            if (v.ValueKind == JsonValueKind.Number) { return v.GetInt64(); }
            return long.TryParse(v.ToString(), out long n) ? n : 0;
        }
        catch { return 0; }
    }

    private static bool Bool(JsonElement e, string key)
    {
        try { return e.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.True; } catch { return false; }
    }
}
