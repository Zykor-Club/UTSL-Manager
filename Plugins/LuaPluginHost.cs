using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MoonSharp.Interpreter;

namespace UTSLManager.Plugins;

/// <summary>
/// Lua 插件适配器（v6）：把 <c>.lua</c> 脚本包装成一个普通的 <see cref="IManagerPlugin"/>。
///
/// 为什么用"适配器"而不是另写一套加载器：这样加载报告 / 「插件管理」页 / 启用停用 / 热重载 /
/// 依赖排序 / 权限模型（PluginContext）全部【原样复用】，Lua 与 C# 插件在宿主看来没有区别。
///
/// 发现方式：plugins\\*.lua，或 plugins\\&lt;名&gt;\\plugin.lua（同目录可放 &lt;名&gt;.manifest.json，与 dll 插件同一套）。
/// </summary>
internal sealed class LuaPluginAdapter : IManagerPlugin
{
    private readonly PluginDescriptor _d;
    private LuaScriptBridge? _bridge;
    private string _name;
    private string _icon = "🎐";
    private string _description;
    private string _author;

    public LuaPluginAdapter(PluginDescriptor d)
    {
        _d = d;
        var mf = PluginManifest.TryLoad(d.DllPath);
        _name = string.IsNullOrWhiteSpace(mf?.Name) ? Path.GetFileNameWithoutExtension(d.DllPath) : mf!.Name!;
        _description = mf?.Description ?? "Lua 插件";
        _author = string.IsNullOrWhiteSpace(mf?.Author) ? (d.Author ?? "?") : mf!.Author!;
    }

    /// <summary>是否由 Lua 运行时处理（.lua / plugin.lua）。</summary>
    public static bool IsLuaFile(string path) => path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase);

    public string Name => _name;
    public string Icon => _icon;
    public string Description => _description;
    public string Author => _author;
    // 版本门禁由 manifest 的 apiVersion 负责（加载器已判过），这里不再二次卡住
    public int RequiredApiVersion => ManagerApi.Version;

    public void OnLoad(IManagerContext ctx)
    {
        _bridge = new LuaScriptBridge(this, _d, ctx);
        _bridge.Start();      // 建 Lua 引擎、注入 ctx、执行脚本、启动计时器
    }

    public void OnUnload()
    {
        try { _bridge?.Stop(); } catch { }
        _bridge = null;
    }

    public Control? CreateView(IManagerContext ctx) => _bridge?.CreatePanelView();

    // 脚本可用 ctx:meta{...} 覆盖元数据（OnLoad 之后菜单才重建，所以这里改来得及）
    internal void SetMeta(string? name, string? icon, string? description, string? author)
    {
        if (!string.IsNullOrWhiteSpace(name)) { _name = name!; }
        if (!string.IsNullOrWhiteSpace(icon)) { _icon = icon!; }
        if (!string.IsNullOrWhiteSpace(description)) { _description = description!; }
        if (!string.IsNullOrWhiteSpace(author)) { _author = author!; }
    }
}

/// <summary>一个 Lua 插件实例的运行时：引擎、ctx 桥、计时器、声明式面板。</summary>
internal sealed class LuaScriptBridge
{
    private readonly LuaPluginAdapter _adapter;
    private readonly PluginDescriptor _d;
    private readonly IManagerContext _ctx;
    private readonly Script _lua;
    private readonly object _gate = new();          // ★ MoonSharp 的 Script 不是线程安全的：所有 Lua 调用串行
    private readonly List<LuaTimer> _timers = [];
    private readonly Dictionary<string, Func<object?>> _getters = [];
    private Timer? _ticker;
    private int _timerSeq;
    private int _stopped;
    private string _panelTitle = "Lua 插件";
    private DynValue? _panelSpec;
    private DynValue? _onAction;
    private Panel? _panelHost;

    public LuaScriptBridge(LuaPluginAdapter adapter, PluginDescriptor d, IManagerContext ctx)
    {
        _adapter = adapter;
        _d = d;
        _ctx = ctx;
        // ★ 安全：官方软沙箱 + 实测它仍保留 os/package，这里显式剔除。
        //   io/load/loadstring/dofile/require/debug 软沙箱已经拿掉了（探针 LuaSpike 实测）。
        _lua = new Script(CoreModules.Preset_SoftSandbox);
        try { _lua.Globals["os"] = DynValue.Nil; _lua.Globals["package"] = DynValue.Nil; } catch { }
    }

    private sealed class LuaTimer
    {
        public int Id; public double EverySec; public bool Repeat; public DateTime Next; public DynValue Fn = DynValue.Nil;
    }

    public void Start()
    {
        var t = new Table(_lua);
        t["log"] = (Action<string>)(m => _ctx.Log(m));
        t["now"] = (Func<Table>)(NowTable);
        t["plugin_name"] = _ctx.PluginName;
        t["plugin_dir"] = _ctx.PluginDir;
        t["data_dir"] = _ctx.DataDir;
        t["files_root"] = _ctx.FilesRoot;
        t["language"] = _ctx.Language;
        t["meta"] = (Action<Table>)(tb => _adapter.SetMeta(Str(tb, "name"), Str(tb, "icon"), Str(tb, "description"), Str(tb, "author")));
        t["every"] = (Func<double, DynValue, int>)((sec, fn) => AddTimer(sec, fn, true));
        t["after"] = (Func<double, DynValue, int>)((sec, fn) => AddTimer(sec, fn, false));
        t["cancel"] = (Action<int>)(id => { lock (_timers) { _timers.RemoveAll(x => x.Id == id); } });
        t["get_config"] = (Func<Table>)(LoadConfigTable);
        t["save_config"] = (Action<Table>)(SaveConfigTable);
        t["register_command"] = (Action<string, string, DynValue>)((n, d, fn) =>
            _ctx.RegisterCommand(n, d, args =>
            {
                try { CallLua(fn, DynValue.NewString(args.RawArguments), DynValue.NewString(args.TargetWorld ?? "")); }
                catch (Exception ex) { _ctx.Log("[Lua] 命令 " + n + " 出错: " + ex.Message); }
                return Task.CompletedTask;
            }));
        t["register_metric"] = (Action<string, string, DynValue>)((n, u, fn) =>
            _ctx.RegisterMetric(n, u, () => Number(fn)));
        t["register_status"] = (Action<string, DynValue>)((n, fn) =>
            _ctx.RegisterStatus(n, () => Text(fn)));
        t["rest_get"] = (Func<string, string>)(u => Block(_ctx.RestGet(u)));
        t["rest_post"] = (Func<string, string>)(u => Block(_ctx.RestPost(u)));
        t["run_command"] = (Func<string, double, string>)((ps, to) => Block(_ctx.RunCommand(ps, (int)(to <= 0 ? 30000 : to))));
        t["read_text"] = (Func<string, string>)(p => Safe(() => _ctx.ReadText(p), ""));
        t["write_text"] = (Func<string, string, bool>)((p, c) => Safe(() => _ctx.WriteText(p, c), false));
        t["exists"] = (Func<string, bool>)(p => Safe(() => _ctx.FileExists(p), false));
        t["list_files"] = (Func<string, Table>)(d => ListFilesTable(d));
        t["worlds"] = (Func<Table>)(WorldsTable);
        t["L"] = (Func<string, string, string>)((k, f) => _ctx.L(k, f));
        t["register_panel"] = (Action<string, DynValue>)((title, fn) => { _panelTitle = title; _panelSpec = fn; });
        t["on_action"] = (Action<DynValue>)(fn => _onAction = fn);
        _lua.Globals["ctx"] = t;

        // 执行脚本本体（顶层直接写 ctx:xxx 即可，不需要约定入口函数）
        lock (_gate) { _lua.DoString(File.ReadAllText(_d.DllPath, System.Text.Encoding.UTF8), null, "plugin.lua"); }

        // 计时器：1 秒粒度，回调在后台线程串行执行（探针实测：这样 Lua 死循环也不会冻结管理器界面）
        _ticker = new Timer(_ => Tick(), null, 1000, 1000);
        _ctx.Log("[Lua] " + _ctx.PluginName + " 已加载（" + _d.DllPath + "）");
    }

    public void Stop()
    {
        Interlocked.Exchange(ref _stopped, 1);
        try { _ticker?.Dispose(); } catch { }
        _ticker = null;
        lock (_timers) { _timers.Clear(); }
        try
        {
            lock (_gate)
            {
                var fn = _lua.Globals.Get("on_unload");
                if (!fn.IsNil() && fn.Type == DataType.Function) { CallLua(fn); }
            }
        }
        catch { }
    }

    // ===== 计时器 =====
    private int AddTimer(double sec, DynValue fn, bool repeat)
    {
        if (sec < 0.05) { sec = 0.05; }
        var lt = new LuaTimer { Id = Interlocked.Increment(ref _timerSeq), EverySec = sec, Repeat = repeat, Next = DateTime.Now.AddSeconds(sec), Fn = fn };
        lock (_timers) { _timers.Add(lt); }
        return lt.Id;
    }

    private void Tick()
    {
        if (Volatile.Read(ref _stopped) == 1) { return; }
        List<LuaTimer> due;
        lock (_timers)
        {
            var now = DateTime.Now;
            due = _timers.Where(x => x.Next <= now).ToList();
            foreach (var x in due)
            {
                if (x.Repeat) { x.Next = now.AddSeconds(x.EverySec); }
                else { _timers.Remove(x); }
            }
        }
        if (due.Count == 0) { return; }
        foreach (var x in due)
        {
            try { CallLua(x.Fn); }
            catch (Exception ex) { try { _ctx.Log("[Lua] 定时任务出错: " + ex.Message); } catch { } }
        }
    }

    // ===== 数据桥 =====
    private Table NowTable()
    {
        var n = DateTime.Now;
        return new Table(_lua)
        {
            ["year"] = n.Year, ["month"] = n.Month, ["day"] = n.Day,
            ["hour"] = n.Hour, ["min"] = n.Minute, ["sec"] = n.Second,
            ["wday"] = (int)n.DayOfWeek == 0 ? 7 : (int)n.DayOfWeek,   // 1=周一 … 7=周日（与 Lua os.date 的习惯一致）
            ["str"] = n.ToString("yyyy-MM-dd HH:mm:ss"),
        };
    }

    private static string Str(Table t, string k) { var v = t.Get(k); return v.IsNil() ? "" : (v.String ?? ""); }

    private string ConfigPath => Path.Combine(_ctx.DataDir, "config.json");

    private Table LoadConfigTable()
    {
        try
        {
            if (!File.Exists(ConfigPath)) { return new Table(_lua); }
            var node = JsonNode.Parse(File.ReadAllText(ConfigPath, System.Text.Encoding.UTF8));
            return JsonToTable(node);
        }
        catch (Exception ex) { _ctx.Log("[Lua] 读配置失败: " + ex.Message); return new Table(_lua); }
    }

    private void SaveConfigTable(Table t)
    {
        try
        {
            Directory.CreateDirectory(_ctx.DataDir);
            var node = TableToJson(t);
            File.WriteAllText(ConfigPath, node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "{}", new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex) { _ctx.Log("[Lua] 写配置失败: " + ex.Message); }
    }

    private Table JsonToTable(JsonNode? n)
    {
        var t = new Table(_lua);
        switch (n)
        {
            case JsonObject o:
                foreach (var kv in o) { t[kv.Key] = JsonToDyn(kv.Value); }
                break;
            case JsonArray a:
                for (int i = 0; i < a.Count; i++) { t[i + 1] = JsonToDyn(a[i]); }
                break;
        }
        return t;
    }

    private DynValue JsonToDyn(JsonNode? n) => n switch
    {
        null => DynValue.Nil,
        JsonObject o => DynValue.NewTable(JsonToTable(o)),
        JsonArray a => DynValue.NewTable(JsonToTable(a)),
        JsonValue v => v.TryGetValue<bool>(out var b) ? DynValue.NewBoolean(b)
            : v.TryGetValue<double>(out var d) ? DynValue.NewNumber(d)
            : DynValue.NewString(v.ToString()),
        _ => DynValue.Nil,
    };

    private JsonNode? TableToJson(Table t)
    {
        bool isArray = t.Keys.All(k => k.Type == DataType.Number);
        if (isArray && t.Length > 0)
        {
            var arr = new JsonArray();
            foreach (var v in t.Values) { arr.Add(DynToJson(v)); }
            return arr;
        }
        var obj = new JsonObject();
        foreach (var kv in t.Pairs) { obj[kv.Key.String ?? kv.Key.ToString()] = DynToJson(kv.Value); }
        return obj;
    }

    private JsonNode? DynToJson(DynValue v) => v.Type switch
    {
        DataType.Nil or DataType.Void => null,
        DataType.Boolean => JsonValue.Create(v.Boolean),
        DataType.Number => JsonValue.Create(v.Number),
        DataType.String => JsonValue.Create(v.String),
        DataType.Table => TableToJson(v.Table!),
        _ => null,
    };

    private Table ListFilesTable(string dir)
    {
        var t = new Table(_lua);
        try
        {
            var list = _ctx.ListFiles(dir);
            if (list is null) { return t; }
            int i = 1;
            foreach (var f in list)
            {
                t[i++] = new Table(_lua) { ["name"] = f.Name, ["dir"] = f.IsDirectory, ["size"] = (double)f.Size, ["modified"] = f.Modified.ToString("yyyy-MM-dd HH:mm:ss") };
            }
        }
        catch (Exception ex) { _ctx.Log("[Lua] list_files 失败: " + ex.Message); }
        return t;
    }

    private Table WorldsTable()
    {
        var t = new Table(_lua);
        try
        {
            int i = 1;
            foreach (var w in _ctx.Worlds) { t[i++] = new Table(_lua) { ["name"] = w.Name, ["size"] = w.Size, ["running"] = w.Running }; }
        }
        catch { }
        return t;
    }

    // ===== 面板（声明式：Lua 返回控件描述，宿主渲染真控件）=====
    public Control CreatePanelView()
    {
        _panelHost = new StackPanel { Spacing = 8 };
        var title = new TextBlock { Text = _panelTitle, FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#F2F2F5")) };
        _panelHost.Children.Add(title);
        var body = new StackPanel { Spacing = 8 };
        _panelHost.Children.Add(body);
        _ = Task.Run(() => RenderPanel(body, title));
        return _panelHost;
    }

    private void RenderPanel(StackPanel body, TextBlock title)
    {
        Table? spec = null;
        if (_panelSpec is not null)
        {
            try { var r = CallLua(_panelSpec); spec = r.Table; }
            catch (Exception ex) { try { _ctx.Log("[Lua] 面板函数出错: " + ex.Message); } catch { } }
        }
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                body.Children.Clear();
                _getters.Clear();
                if (_panelSpec is not null) { title.Text = _panelTitle; }
                if (spec is null) { body.Children.Add(Hint("此 Lua 插件没有界面（可以只用命令/定时任务）。")); return; }
                int n = spec.Length;
                for (int i = 1; i <= n; i++)
                {
                    var row = spec.Get(i).Table;
                    if (row is null) { continue; }
                    body.Children.Add(BuildControl(row));
                }
            }
            catch (Exception ex) { body.Children.Add(Hint("渲染面板失败: " + ex.Message)); }
        });
    }

    private static TextBlock Hint(string s) => new() { Text = s, FontSize = 11.5, Foreground = new SolidColorBrush(Color.Parse("#7C7C82")), TextWrapping = TextWrapping.Wrap };

    private Control BuildControl(Table row)
    {
        string kind = Str(row, "kind");
        string label = Str(row, "label");
        string id = Str(row, "id");
        switch (kind)
        {
            case "divider":
                return new Border { Height = 1, Background = new SolidColorBrush(Color.Parse("#26262B")), Margin = new Thickness(0, 4, 0, 4) };
            case "text":
                {
                    var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                    var l = new TextBlock { Text = label, FontSize = 11.5, Foreground = new SolidColorBrush(Color.Parse("#8A8A90")), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
                    var v = new TextBlock { Text = Str(row, "value"), FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#C7D2E0")), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(l, 0); Grid.SetColumn(v, 1);
                    g.Children.Add(l); g.Children.Add(v);
                    return g;
                }
            case "select":
                {
                    var sp = new StackPanel { Spacing = 4 };
                    sp.Children.Add(new TextBlock { Text = label, FontSize = 11.5, Foreground = new SolidColorBrush(Color.Parse("#8A8A90")) });
                    var cb = new ComboBox { FontSize = 12, MinWidth = 180 };
                    var opts = row.Get("options").Table;
                    string cur = Str(row, "value");
                    int sel = 0;
                    if (opts is not null)
                    {
                        for (int i = 1; i <= opts.Length; i++)
                        {
                            string s = opts.Get(i).String ?? "";
                            cb.Items.Add(s);
                            if (s == cur) { sel = i - 1; }
                        }
                    }
                    if (cb.ItemCount > 0) { cb.SelectedIndex = sel; }
                    sp.Children.Add(cb);
                    if (id.Length > 0) { _getters[id] = () => cb.SelectedItem?.ToString() ?? ""; }
                    return sp;
                }
            case "number":
                {
                    var sp = new StackPanel { Spacing = 4 };
                    sp.Children.Add(new TextBlock { Text = label, FontSize = 11.5, Foreground = new SolidColorBrush(Color.Parse("#8A8A90")) });
                    var nb = new NumericUpDown { FontSize = 12, Minimum = 0, Maximum = 1_000_000, Increment = 1, MinWidth = 120 };
                    try { nb.Value = (decimal)row.Get("value").Number; } catch { }
                    sp.Children.Add(nb);
                    if (id.Length > 0) { _getters[id] = () => nb.Value.HasValue ? (double)nb.Value.Value : 0d; }
                    return sp;
                }
            case "buttons":
                {
                    var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    var items = row.Get("items").Table;
                    if (items is not null)
                    {
                        for (int i = 1; i <= items.Length; i++)
                        {
                            var it = items.Get(i).Table;
                            if (it is null) { continue; }
                            string text = Str(it, "text");
                            string action = Str(it, "action");
                            var b = new Button { Content = text, FontSize = 11.5, Padding = new Thickness(12, 5, 12, 5), CornerRadius = new CornerRadius(9) };
                            b.Click += (_, _) => FireAction(action);
                            sp.Children.Add(b);
                        }
                    }
                    return sp;
                }
            default:
                {
                    var sp = new StackPanel { Spacing = 4 };
                    sp.Children.Add(new TextBlock { Text = label, FontSize = 11.5, Foreground = new SolidColorBrush(Color.Parse("#8A8A90")) });
                    var tb = new TextBox { Text = Str(row, "value"), FontSize = 12, MinWidth = 220 };
                    sp.Children.Add(tb);
                    if (id.Length > 0) { _getters[id] = () => tb.Text ?? ""; }
                    return sp;
                }
        }
    }

    private void FireAction(string action)
    {
        if (_onAction is null) { return; }
        var values = new Table(_lua);
        foreach (var kv in _getters.ToArray())
        {
            try { values[kv.Key] = DynValue.FromObject(_lua, kv.Value()); } catch { }
        }
        _ = Task.Run(() =>
        {
            try { CallLua(_onAction, DynValue.NewString(action), DynValue.NewTable(values)); }
            catch (Exception ex) { try { _ctx.Log("[Lua] 面板动作出错: " + ex.Message); } catch { } }
            // 动作后重新求值面板（Lua 侧改了状态就能立刻反映出来）
            if (_panelHost?.Children.Count > 1 && _panelHost.Children[1] is StackPanel b && _panelHost.Children[0] is TextBlock t) { RenderPanel(b, t); }
        });
    }

    // ===== 小工具 =====
    /// <summary>统一的 Lua 调用入口：MoonSharp 的 Script 不是线程安全的，所以一律走这把锁。</summary>
    private DynValue CallLua(DynValue fn, params DynValue[] args)
    {
        lock (_gate) { return _lua.Call(fn, args); }
    }
    private static string Block(Task<string> t) { try { return t.GetAwaiter().GetResult(); } catch (Exception ex) { return "[出错] " + ex.Message; } }
    private static T Safe<T>(Func<T> f, T fallback) { try { return f(); } catch { return fallback; } }

    private double Number(DynValue fn)
    {
        try { var r = CallLua(fn); return r.Type == DataType.Number ? r.Number : 0; }
        catch { return 0; }
    }

    private string Text(DynValue fn)
    {
        try { return CallLua(fn).String ?? ""; }
        catch { return ""; }
    }
}

/// <summary>给 Lua 运行时用的小扩展（避免到处写 Path.GetFileName）。</summary>
internal static class LuaPathExt
{
    public static string Path(this PluginDescriptor d) => d.DllPath;
}
