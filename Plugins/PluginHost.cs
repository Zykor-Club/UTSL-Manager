using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;   // OnUi：把宿主界面回调切回 UI 线程（加载/重载已改到后台线程执行）

namespace UTSLManager.Plugins;

/// <summary>管理器侧注入给插件宿主的回调（由 MainWindow 提供）。</summary>
public sealed class PluginHostOptions
{
    public required string PluginDir { get; init; }
    /// <summary>插件数据根目录（每个插件一个子目录）。</summary>
    public required string DataRoot { get; init; }
    public required Func<string> DeployRoot { get; init; }
    public required Func<string> RestBase { get; init; }
    public required Func<string> Token { get; init; }
    public required Func<IReadOnlyList<WorldInfo>> Worlds { get; init; }
    public required Action<string> Log { get; init; }
    public required Func<string> Language { get; init; }

    // ===== v3：由管理器注入的「统一部署目录访问」回调（远端走 SSH、本机直接读盘）=====
    /// <summary>当前实际部署目录（远端=远端目录，本机=本机目录）。</summary>
    public Func<string>? FilesRoot { get; init; }
    /// <summary>列目录：入参为「相对 FilesRoot 的路径」，返回条目。</summary>
    public Func<string, IReadOnlyList<PluginFileInfo>>? ListFiles { get; init; }
    public Func<string, bool>? FileExists { get; init; }
    public Func<string, string>? ReadText { get; init; }
    public Func<string, string, bool>? WriteText { get; init; }
    public Func<string, bool>? CreateDirectory { get; init; }
    public Func<string, string, bool>? Rename { get; init; }
    public Func<string, bool>? Delete { get; init; }
    public Func<string, string, bool>? Upload { get; init; }
    public Func<string, string, bool>? Download { get; init; }
    /// <summary>清空宿主的文件缓存（插件写文件后调用）。</summary>
    public Action? InvalidateFileCache { get; init; }
    /// <summary>在部署主机上执行 PowerShell（远端走 ssh）。</summary>
    public Func<string, int, string>? RunCommand { get; init; }
    /// <summary>加载完成后通知 UI 重建「扩展」菜单 / 刷新「插件管理」页。</summary>
    public Action? AfterLoad { get; init; }
    /// <summary>卸载前通知 UI 把插件界面从可视树摘掉（否则 ALC 无法回收）。</summary>
    public Action? BeforeUnload { get; init; }
}

/// <summary>插件未声明所需权限时调用受限 API 抛出。</summary>
public sealed class PluginPermissionException : Exception
{
    public PluginPermissionException(string message) : base(message) { }
}

/// <summary>
/// 管理器插件宿主：负责加载/卸载/重载（P0/P1-8）、命令（P1-6）、指标（P1-10）、
/// 事件总线（P1-5）、配置存储（P1-7）、权限（P2-13）、i18n（P2-14）。
/// </summary>
public sealed class PluginHost
{
    internal PluginHostOptions Options { get; }
    internal HttpClient Http { get; } = new() { Timeout = TimeSpan.FromSeconds(15) };
    internal IReadOnlyCollection<string> HostAssemblies { get; }

    private readonly List<LoadedPlugin> _plugins = [];
    private readonly List<PluginContext> _contexts = [];
    private readonly List<PluginLoadContext> _alcs = [];
    private CancellationTokenSource _shutdown = new();   // ★ 修 bug#14：热重载要取消并重建，故不可 readonly

    public PluginHost(PluginHostOptions options)
    {
        Options = options;
        Events = new ManagerEvents(options.Log);
        HostAssemblies = BuildHostAssemblies();
    }

    /// <summary>当前事件总线（每次重载都会换成新实例，旧订阅随之丢弃）。</summary>
    public ManagerEvents Events { get; private set; }

    public CancellationToken ShutdownToken => _shutdown.Token;
    public IReadOnlyList<LoadedPlugin> Plugins => _plugins;
    public PluginLoadReport Report { get; private set; } = new();

    /// <summary>所有插件注册的命令：(名字, 说明, 所属插件)。</summary>
    public IReadOnlyList<(string Name, string Description, string Plugin)> Commands =>
        _contexts.SelectMany(c => c.Commands.Values.Select(x => (x.Name, x.Description, c.PluginName))).ToList();

    /// <summary>所有插件注册的指标。</summary>
    public IReadOnlyList<PluginMetricInfo> Metrics => _contexts.SelectMany(c => c.Metrics).ToList();

    /// <summary>所有插件注册的文本状态项。</summary>
    public IReadOnlyList<PluginStatusInfo> Statuses => _contexts.SelectMany(c => c.Statuses).ToList();

    // ===== 生命周期 =====

    /// <summary>
    /// 把宿主的界面回调切到 UI 线程执行（同步，保证"清旧界面 → 再注入新界面"的顺序）。
    /// ★ 加载/重载会被放到后台线程（Roslyn 编译 + 插件 OnLoad 可能很慢，原来整段占着 UI 线程），
    ///   而 AfterLoad/BeforeUnload 会碰 Avalonia 控件 —— 不切回去就是跨线程改可视树。
    /// </summary>
    private void OnUi(Action? a)
    {
        if (a is null) { return; }
        if (Dispatcher.UIThread.CheckAccess()) { a(); }
        else { Dispatcher.UIThread.Invoke(a); }   // 调用方是 await 状态（UI 线程空闲），不会死锁
    }

    public PluginLoadReport Load()
    {
        UnloadAll();
        Report = PluginLoader.Load(this);
        OnUi(Options.AfterLoad);
        return Report;
    }

    /// <summary>热重载：卸载全部插件（含 ALC）后重新扫描加载。</summary>
    public PluginLoadReport Reload() => Load();

    /// <summary>管理器关闭：通知插件、取消后台任务、卸载所有 ALC。</summary>
    public void Shutdown()
    {
        try { Events.RaiseShuttingDown(); } catch { }
        try { _shutdown.Cancel(); } catch { }
        UnloadAll();
    }

    private void UnloadAll()
    {
        bool any = _plugins.Count > 0 || _contexts.Count > 0;
        if (any) { OnUi(Options.BeforeUnload); }

        // ★ 修 bug#14：热重载必须取消旧插件后台任务的令牌。原来只有 Shutdown() 取消，而 Reload() 走的是
        //   UnloadAll()（不取消）→ 旧插件的轮询循环永久存活（实测重载后新旧实例同时在轮询），
        //   既让 ALC 永远无法回收（内存泄漏），又会不断叠加重复的 REST 轮询。
        try { _shutdown.Cancel(); } catch { }

        foreach (var p in _plugins)
        {
            try { p.Instance.OnUnload(); }
            catch (Exception ex) { Options.Log($"[插件] {p.Name} OnUnload 异常: {ex.Message}"); }
        }
        _plugins.Clear();
        _contexts.Clear();
        foreach (var alc in _alcs) { try { alc.Unload(); } catch { } }
        _alcs.Clear();
        // 换一个新总线 → 所有旧订阅（含插件的 lambda）立即失效，不会把插件钉在内存里
        Events = new ManagerEvents(Options.Log);
        _shutdown = new CancellationTokenSource();   // 给重载后的新插件一个未被取消的令牌
    }

    internal void AddPlugin(LoadedPlugin plugin, PluginContext ctx)
    {
        _plugins.Add(plugin);
        _contexts.Add(ctx);
    }

    internal void RegisterAlc(PluginLoadContext alc) => _alcs.Add(alc);

    // ===== 终端命令（P1-6）=====

    /// <summary>派发一个插件命令（输入形如 "!名字 参数"）。返回 false 表示没有插件认领。</summary>
    public bool TryExecuteCommand(string name, IReadOnlyList<string> args, string rawArgs, string? world, out string? error)
    {
        error = null;
        var ctx = _contexts.FirstOrDefault(c => c.Commands.ContainsKey(name));
        if (ctx is null) { return false; }
        var reg = ctx.Commands[name];
        var cargs = new PluginCommandArgs
        {
            Name = reg.Name, Args = args, RawArguments = rawArgs, TargetWorld = world, Context = ctx,
        };
        _ = Task.Run(async () =>
        {
            try { await reg.Handler(cargs); }
            catch (Exception ex) { Options.Log($"[插件命令] !{reg.Name}（{ctx.PluginName}）执行失败: {ex.Message}"); }
        });
        return true;
    }

    // ===== 启用 / 停用（改名，不删除）=====

    public static bool SetEnabled(string dllPath, bool enabled)
    {
        string disabled = dllPath + ".disabled";
        if (enabled)
        {
            if (!File.Exists(disabled)) { return false; }
            File.Move(disabled, dllPath, overwrite: true);
            return true;
        }
        if (!File.Exists(dllPath)) { return false; }
        File.Move(dllPath, disabled, overwrite: true);
        return true;
    }

    // ===== 事件转发（MainWindow 调用）=====

    public void RaiseServerStateChanged(bool up) => Events.RaiseServerStateChanged(up);
    public void RaiseWorldChanged(string world) => Events.RaiseWorldChanged(world);
    public void RaiseWorldListRefreshed(IReadOnlyList<WorldInfo> worlds) => Events.RaiseWorldListRefreshed(worlds);
    public void RaiseMetricsSampled(MetricsSample sample) => Events.RaiseMetricsSampled(sample);
    public void RaiseTerminalLine(string text, string level) => Events.RaiseTerminalLine(new TerminalLineEvent(text, level));

    private static HashSet<string> BuildHostAssemblies()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "UTSLManager" };
        try
        {
            foreach (var f in Directory.GetFiles(AppContext.BaseDirectory, "*.dll"))
            {
                set.Add(Path.GetFileNameWithoutExtension(f));
            }
        }
        catch { }
        return set;
    }
}

internal sealed record CommandRegistration(string Name, string Description, Func<PluginCommandArgs, Task> Handler);

/// <summary>单个插件的受限上下文（配置目录 / 命令 / 指标 / 权限 / i18n 各自独立）。</summary>
internal sealed class PluginContext : IManagerContext
{
    private readonly PluginHost _host;
    private readonly IManagerPlugin _instance;
    private readonly string[]? _permissions;
    private readonly Dictionary<string, Dictionary<string, string>> _strings = new(StringComparer.OrdinalIgnoreCase);
    private string? _dataDir;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    internal PluginContext(PluginHost host, PluginDescriptor descriptor, IManagerPlugin instance)
    {
        _host = host;
        Descriptor = descriptor;
        _instance = instance;
        // null = 未声明（全部允许，向后兼容）；空数组 = 显式不授权
        _permissions = descriptor.Permissions;
    }

    internal PluginDescriptor Descriptor { get; }
    internal Dictionary<string, CommandRegistration> Commands { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal List<PluginMetricInfo> Metrics { get; } = [];
    internal List<PluginStatusInfo> Statuses { get; } = [];

    public string PluginName => _instance.Name;
    public string PluginDir => Descriptor.Directory;
    public string DataDir
    {
        get
        {
            if (_dataDir is null)
            {
                string d = Path.Combine(_host.Options.DataRoot, Sanitize(PluginName));
                Directory.CreateDirectory(d);
                _dataDir = d;
            }
            return _dataDir;
        }
    }
    public string DeployRoot => _host.Options.DeployRoot();
    public string RestBase => _host.Options.RestBase();
    public string Token => _host.Options.Token();
    public string Language => _host.Options.Language();
    public IReadOnlyCollection<string> Permissions => _permissions is null ? Array.Empty<string>() : _permissions;

    public void Log(string message) => _host.Options.Log(message);

    public Task<string> RestGet(string relativeUrl)
    {
        Require("rest");
        return _host.Http.GetStringAsync(BuildUrl(relativeUrl));
    }

    public async Task<string> RestPost(string relativeUrl)
    {
        Require("rest");
        using var resp = await _host.Http.PostAsync(BuildUrl(relativeUrl), null);
        return await resp.Content.ReadAsStringAsync();
    }

    public IReadOnlyList<WorldInfo> Worlds => _host.Options.Worlds();

    public ManagerEvents Events => _host.Events;
    public CancellationToken ShutdownToken => _host.ShutdownToken;

    public void RunBackground(Func<CancellationToken, Task> work, string? name = null)
    {
        _ = Task.Run(async () =>
        {
            try { await work(_host.ShutdownToken); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                string who = name is null ? "" : "（" + name + "）";
                _host.Options.Log($"[{PluginName}] 后台任务{who}异常: {ex.Message}");
            }
        });
    }

    public void RegisterCommand(string name, string description, Func<PluginCommandArgs, Task> handler)
    {
        if (string.IsNullOrWhiteSpace(name)) { throw new ArgumentException("命令名不能为空", nameof(name)); }
        string n = name.TrimStart('!').Trim();
        Commands[n] = new CommandRegistration(n, description ?? "", handler);
    }

    public void UnregisterCommand(string name) => Commands.Remove(name.TrimStart('!').Trim());

    public void RegisterMetric(string name, string unit, Func<double?> provider)
    {
        Metrics.RemoveAll(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        Metrics.Add(new PluginMetricInfo { PluginName = PluginName, Name = name, Unit = unit ?? "", Provider = provider });
    }

    public void UnregisterMetric(string name) =>
        Metrics.RemoveAll(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

    public void RegisterStatus(string name, Func<string> provider)
    {
        Statuses.RemoveAll(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        Statuses.Add(new PluginStatusInfo { PluginName = PluginName, Name = name, Provider = provider });
    }

    public void UnregisterStatus(string name) =>
        Statuses.RemoveAll(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public T GetConfig<T>(string? fileName = null) where T : new()
    {
        Require("config");
        string path = ResolveDataFile(fileName);
        try
        {
            if (!File.Exists(path)) { return new T(); }
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOpts) ?? new T();
        }
        catch (Exception ex)
        {
            Log($"[{PluginName}] 读取配置 {Path.GetFileName(path)} 失败（改用默认值）: {ex.Message}");
            return new T();
        }
    }

    public void SaveConfig<T>(T value, string? fileName = null)
    {
        Require("config");
        string path = ResolveDataFile(fileName);
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOpts));
    }

    /// <summary>★ v3 修 bug：原来用 File.ReadAllText 直接读本机，远端模式下会读到本机目录（错的目标）。
    /// 现在统一走宿主的部署目录访问回调（远端走 SSH、本机直接读盘）。</summary>
    public string ReadDeployText(string relativePath)
    {
        Require("fs");
        return ReadText(relativePath);
    }

    public void WriteDeployText(string relativePath, string content)
    {
        Require("fs");
        WriteText(relativePath, content);
    }

    // ===== v3：文件（统一部署目录，全部经沙箱解析）=====

    public string FilesRoot => _host.Options.FilesRoot?.Invoke() ?? DeployRoot;

    public IReadOnlyList<PluginFileInfo>? ListFiles(string relativeDir)
    {
        Require("fs");
        if (_host.Options.ListFiles is null) { return null; }
        return _host.Options.ListFiles(ResolveFiles(relativeDir));
    }

    public bool FileExists(string relativePath)
    {
        Require("fs");
        return _host.Options.FileExists?.Invoke(ResolveFiles(relativePath)) ?? false;
    }

    public string ReadText(string relativePath)
    {
        Require("fs");
        return _host.Options.ReadText?.Invoke(ResolveFiles(relativePath)) ?? "";
    }

    public bool WriteText(string relativePath, string content)
    {
        Require("fs");
        bool ok = _host.Options.WriteText?.Invoke(ResolveFiles(relativePath), content) ?? false;
        _host.Options.InvalidateFileCache?.Invoke();
        return ok;
    }

    public bool CreateDirectory(string relativeDir)
    {
        Require("fs");
        bool ok = _host.Options.CreateDirectory?.Invoke(ResolveFiles(relativeDir)) ?? false;
        _host.Options.InvalidateFileCache?.Invoke();
        return ok;
    }

    public bool Rename(string relativePath, string newName)
    {
        Require("fs");
        if (string.IsNullOrWhiteSpace(newName) || newName.Contains('/') || newName.Contains('\\'))
        {
            throw new IOException("newName 只能是名字，不能带路径分隔符: " + newName);
        }
        string from = ResolveFiles(relativePath);
        string dir = Path.GetDirectoryName(from) ?? FilesRoot;
        string to = Path.Combine(dir, newName);
        bool ok = _host.Options.Rename?.Invoke(from, to) ?? false;
        _host.Options.InvalidateFileCache?.Invoke();
        return ok;
    }

    public bool Delete(string relativePath)
    {
        Require("fs");
        bool ok = _host.Options.Delete?.Invoke(ResolveFiles(relativePath)) ?? false;
        _host.Options.InvalidateFileCache?.Invoke();
        return ok;
    }

    public bool Upload(string localPath, string relativePath)
    {
        Require("fs");
        bool ok = _host.Options.Upload?.Invoke(localPath, ResolveFiles(relativePath)) ?? false;
        _host.Options.InvalidateFileCache?.Invoke();
        return ok;
    }

    public bool Download(string relativePath, string localPath)
    {
        Require("fs");
        return _host.Options.Download?.Invoke(ResolveFiles(relativePath), localPath) ?? false;
    }

    public void InvalidateFileCache()
    {
        Require("fs");
        _host.Options.InvalidateFileCache?.Invoke();
    }

    public Task<string> RunCommand(string powerShell, int timeoutMs = 30000)
    {
        Require("exec");
        return Task.Run(() => _host.Options.RunCommand?.Invoke(powerShell, timeoutMs) ?? "");
    }

    public Task<string> HttpGet(string url)
    {
        Require("net");
        return _host.Http.GetStringAsync(url);
    }

    public async Task<string> HttpPost(string url, string? jsonBody = null)
    {
        Require("net");
        using var content = jsonBody is null ? null : new System.Net.Http.StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
        using var resp = await _host.Http.PostAsync(url, content);
        return await resp.Content.ReadAsStringAsync();
    }

    /// <summary>把「相对 FilesRoot 的路径」解析成绝对路径，并确保仍在 FilesRoot 内（防越界）。</summary>
    private string ResolveFiles(string relativePath)
    {
        string root = Path.GetFullPath(FilesRoot);
        string full = Path.GetFullPath(Path.Combine(root, relativePath ?? ""));
        // ★ 探针发现的 bug：relativePath 为空（=根目录本身）或恰为根时，full 等于 root（不带尾分隔符），
        //   只做 StartsWith(root + sep) 会把它误判成"越界"。这里额外放行"就是根目录本身"。
        bool inside = IsInside(WithSep(root), full)
                   || string.Equals(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                                    root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                                    StringComparison.OrdinalIgnoreCase);
        if (!inside)
        {
            throw new IOException("路径越界（必须位于部署目录内）: " + relativePath);
        }
        return full;
    }

    public void RegisterStrings(string language, IReadOnlyDictionary<string, string> strings)
    {
        if (string.IsNullOrWhiteSpace(language) || strings is null) { return; }
        if (!_strings.TryGetValue(language, out var d))
        {
            d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _strings[language] = d;
        }
        foreach (var kv in strings) { d[kv.Key] = kv.Value; }
    }

    public string L(string key, string? fallback = null)
    {
        if (_strings.TryGetValue(Language, out var d) && d.TryGetValue(key, out var s)) { return s; }
        foreach (var kv in _strings) { if (kv.Value.TryGetValue(key, out var s2)) { return s2; } }
        return fallback ?? key;
    }

    // ===== 内部 =====

    private string BuildUrl(string relativeUrl) =>
        RestBase + relativeUrl + (relativeUrl.Contains('?') ? "&" : "?") + "token=" + Uri.EscapeDataString(Token);

    private void Require(string permission)
    {
        if (_permissions is null) { return; }   // 未声明 = 全部允许（向后兼容）
        if (!_permissions.Contains(permission, StringComparer.OrdinalIgnoreCase))
        {
            throw new PluginPermissionException($"[{PluginName}] 未在 manifest.json 的 permissions 里声明权限 {permission}，调用被拒绝");
        }
    }

    private static bool IsInside(string rootWithSep, string full) =>
        full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);

    private static string WithSep(string dir) =>
        dir.EndsWith(Path.DirectorySeparatorChar) ? dir : dir + Path.DirectorySeparatorChar;

    /// <summary>★ 修 bug#9：插件配置文件同样必须限制在 DataDir 内。</summary>
    private string ResolveDataFile(string? fileName)
    {
        string dir = Path.GetFullPath(DataDir);
        string full = Path.GetFullPath(Path.Combine(dir, NormalizeFileName(fileName)));
        if (!IsInside(WithSep(dir), full))
        {
            throw new IOException("配置文件名越界（必须位于插件数据目录内）: " + fileName);
        }
        return full;
    }

    private static string NormalizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) { return "config.json"; }
        return Path.HasExtension(fileName) ? fileName! : fileName + ".json";
    }

    private static string Sanitize(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string s = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return s.Length == 0 ? "plugin" : s;
    }
}
