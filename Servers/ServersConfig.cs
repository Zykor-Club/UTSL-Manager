using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UTSLManager.Servers;

/// <summary>
/// 一台被管服务器的"连接档"。
/// ★ 字段与原 connection.json 一一对应（迁移零损失），额外多一个 <see cref="AgentPort"/> ——
///   原来它是 MainWindow 里的 const 17879，多档时端口不分开会让两台的日志/指标连到同一台 agent（必然串台）。
/// 运行时字段（TunnelPid / WorldCount / Status）不落盘。
/// </summary>
public sealed class ServerProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>管理器访问 REST 的地址（远端档固定 127.0.0.1，走本机隧道入口）。</summary>
    public string Host { get; set; } = "127.0.0.1";
    /// <summary>管理器访问 REST 的端口（远端档 = 该档自己的隧道本地端口）。</summary>
    public int Port { get; set; } = 7878;

    /// <summary>REST 令牌（只在内存里是明文；落盘一律 DPAPI 加密成 tokenEnc）。</summary>
    public string Token { get; set; } = "";

    // ===== ssh 段（与旧 connection.json 的 "ssh" 段同名同义）=====
    public string SshHost { get; set; } = "";
    public int SshPort { get; set; } = 22;
    public string SshUser { get; set; } = "Administrator";
    public string SshKey { get; set; } = "";
    public string RemoteDir { get; set; } = @"C:\utsl-deploy";
    public string RemoteDotnet { get; set; } = @"C:\dotnet\dotnet.exe";
    public string RemoteLauncher { get; set; } = @"C:\utsl-launch\utsl-launch.dll";
    /// <summary>远端 REST 端口（隧道另一端）。</summary>
    public int RemoteRestPort { get; set; } = 7878;
    /// <summary>★ 该档自己的本地 agent 隧道端口（原来硬编码 17879）。多档必须互不相同。</summary>
    public int AgentPort { get; set; } = 17879;

    // 直连（面板）模式
    public bool Direct { get; set; }
    public string DirectHost { get; set; } = "";
    public int DirectPort { get; set; } = 7879;
    public string DirectToken { get; set; } = "";

    // ===== paths 段 =====
    public string LocalDir { get; set; } = "";
    public bool Watchdog { get; set; } = true;
    public bool ForceLocal { get; set; }
    public bool WorldConsoles { get; set; }

    /// <summary>迁移出来的第一档：插件数据目录继续用旧的全局 plugin-data（否则老插件配置会被搬走）。</summary>
    public bool LegacyDataRoot { get; set; }

    // ===== 运行时（不落盘）=====
    public int TunnelPid;
    /// <summary>上次拉隧道的时间（Environment.TickCount64）。用于冷却，避免"进程还没绑上端口"时被重复拉起。</summary>
    public long LastSpawnMs;
    /// <summary>建这条隧道时用的 ssh 参数指纹。参数被改过（改主机/密钥/端口）时必须重建，否则会复用一条连到旧目标的隧道。</summary>
    public string TunnelKey = "";
    /// <summary>最近一次被激活的时间（TickCount64）。隧道保活超出上限时按它做 LRU 淘汰。</summary>
    public long LastUsedMs;
    /// <summary>上次刷新到的世界数（-1 = 未知）。</summary>
    public int WorldCount = -1;
    /// <summary>0=未知 1=在线 2=隧道通但 REST 不通 3=进程/接口都没有。</summary>
    public int Status;
    /// <summary>该档读到过明文令牌（加载后要立刻重新加密落盘）。</summary>
    public bool PlainToken;
    /// <summary>非激活档的轻量探活结果（只探 SSH/本地端口，不开隧道）。</summary>
    public bool ProbeOk;
    /// <summary>上次探活时间（Environment.TickCount64，0 = 没探过）。</summary>
    public long ProbeAtMs;
    /// <summary>探活文案（卡片 tooltip 用）。</summary>
    public string ProbeText = "";

    // ===== 派生 =====
    public bool HasSsh => SshHost.Length > 0 && SshKey.Length > 0;
    public bool DirectReady => Direct && DirectHost.Length > 0 && DirectToken.Length > 0;
    /// <summary>这条档是否按"远端"跑（远端档 = 有 ssh 或直连，且没被强制本机）。</summary>
    public bool IsRemote => (HasSsh || DirectReady) && !ForceLocal;

    /// <summary>卡片上第二行的地址文案。</summary>
    public string AddressText => IsRemote
        ? (DirectReady ? DirectHost + ":" + DirectPort : SshHost + ":" + SshPort)
        : (Host + ":" + Port);

    /// <summary>插件数据根目录：迁移档沿用全局 plugin-data，新档按 id 分开（定时任务/配置不会跨服务器混）。</summary>
    public string PluginDataRoot(string baseDir)
        => LegacyDataRoot ? Path.Combine(baseDir, "plugin-data")
                          : Path.Combine(baseDir, "plugin-data", SafeId);

    /// <summary>id 会被拼进目录名，先消毒（手改过 servers.json 也不能穿越）。</summary>
    public string SafeId
    {
        get
        {
            var bad = Path.GetInvalidFileNameChars();
            var s = new string((Id ?? "").Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
            if (s.Length == 0) { s = "server"; }
            if (s is "." or "..") { s = "server"; }
            return s.Length > 40 ? s[..40] : s;
        }
    }
}

/// <summary>servers.json 的根：{ version, language, active, servers:[…] }。</summary>
public sealed class ServersConfig
{
    public int Version { get; set; } = 1;
    /// <summary>界面语言是全局偏好，不属于任何一台服务器。</summary>
    public string Language { get; set; } = "zh-CN";
    public string ActiveId { get; set; } = "";

    /// <summary>后台日志流上限（非激活档各一条 /stream/log）。档数多、嫌占连接就调小；0=不订阅。</summary>
    public int BgLogSubs { get; set; } = 4;

    /// <summary>切档时是否重载插件（默认开：插件内存态不跨档；关掉可省 4~26 秒的后台开销，但 Lua 定时任务等状态会跨档共用）。</summary>
    public bool ReloadPluginsOnSwitch { get; set; } = true;
    public List<ServerProfile> Servers { get; set; } = [];

    /// <summary>本次加载是否刚从 connection.json 迁移过来（只在内存里用）。</summary>
    public bool Migrated { get; set; }
    /// <summary>是否存在明文令牌（迁移后立刻重新加密落盘）。</summary>
    public bool HadPlainToken { get; set; }

    public ServerProfile? Find(string? id)
        => id is null ? null : Servers.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));

    /// <summary>取激活档；active 失效时退回第一条；一条都没有就建一条本机档。</summary>
    public ServerProfile ActiveOrFirst()
    {
        var a = Find(ActiveId);
        if (a is not null) { return a; }
        if (Servers.Count == 0) { Servers.Add(NewLocal()); }
        ActiveId = Servers[0].Id;
        return Servers[0];
    }

    public static string NewId() => "srv-" + Guid.NewGuid().ToString("N")[..8];

    public static ServerProfile NewLocal()
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return new ServerProfile
        {
            Id = NewId(), Name = "本机",
            Host = "127.0.0.1", Port = 7878,
            LocalDir = Path.Combine(desktop, "utsl-deploy"),
            ForceLocal = true, Watchdog = true,
        };
    }

    /// <summary>复制一条档做"再添加一台"的起点（ssh/路径全带走，只换 id/名字/端口）。</summary>
    public static ServerProfile Clone(ServerProfile src, string name, int restPort, int agentPort)
        => new()
        {
            Id = NewId(), Name = name,
            Host = "127.0.0.1", Port = restPort, Token = src.Token,
            SshHost = src.SshHost, SshPort = src.SshPort, SshUser = src.SshUser, SshKey = src.SshKey,
            RemoteDir = src.RemoteDir, RemoteDotnet = src.RemoteDotnet, RemoteLauncher = src.RemoteLauncher,
            RemoteRestPort = src.RemoteRestPort, AgentPort = agentPort,
            Direct = src.Direct, DirectHost = src.DirectHost, DirectPort = src.DirectPort, DirectToken = src.DirectToken,
            LocalDir = src.LocalDir, Watchdog = src.Watchdog, ForceLocal = src.ForceLocal, WorldConsoles = src.WorldConsoles,
        };

    // ===== 读写 =====

    /// <summary>
    /// 读 servers.json；不存在就把旧 connection.json 迁移成第一条档并立刻落盘。
    /// ★ 旧 connection.json 原样保留（不删不改），回滚时把它拷回去就回到单档世界。
    /// </summary>
    public static ServersConfig LoadOrMigrate(string file, string legacyFile)
    {
        if (File.Exists(file))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(file)) is JsonObject o) { return FromJson(o); }
            }
            catch { /* 坏文件：走下面的迁移/兜底 */ }
        }

        var cfg = new ServersConfig();
        if (File.Exists(legacyFile))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(legacyFile)) is JsonObject o)
                {
                    cfg.Servers.Add(FromLegacy(o));
                    cfg.Language = o["language"]?.ToString() ?? cfg.Language;
                    cfg.Migrated = true;
                }
            }
            catch { }
        }
        if (cfg.Servers.Count == 0)
        {
            var p = NewLocal();
            p.Id = NewId();
            cfg.Servers.Add(p);
        }
        cfg.ActiveId = cfg.Servers[0].Id;
        cfg.Save(file);          // 迁移只发生一次
        return cfg;
    }

    /// <summary>旧 connection.json（单台扁平结构）→ 一条档。字段解析顺序与旧 LoadConn 完全一致。</summary>
    public static ServerProfile FromLegacy(JsonObject o)
    {
        var p = NewLocal();
        p.Id = NewId();
        p.Name = "本机";
        p.LegacyDataRoot = true;
        p.Host = o["host"]?.ToString() ?? p.Host;
        if (int.TryParse(o["port"]?.ToString(), out int port) && port > 0) { p.Port = port; }

        string encTok = o["tokenEnc"]?.ToString() ?? "";
        if (encTok.Length > 0) { p.Token = SecretBox.Unprotect(encTok); }
        if (p.Token.Length == 0 && o["token"] is not null)
        {
            p.Token = o["token"]?.ToString() ?? "";
            // 明文令牌：标记一下，调用方会立刻 Save 把它换成 tokenEnc
        }
        string plainTok = o["token"]?.ToString() ?? "";
        if (plainTok.Length > 0) { p.Token = p.Token.Length > 0 ? p.Token : plainTok; p.PlainToken = true; }

        if (o["ssh"] is JsonObject s)
        {
            p.SshHost = s["host"]?.ToString() ?? p.SshHost;
            if (int.TryParse(s["port"]?.ToString(), out int sp) && sp > 0) { p.SshPort = sp; }
            p.SshUser = s["user"]?.ToString() ?? p.SshUser;
            p.SshKey = s["key"]?.ToString() ?? p.SshKey;
            p.RemoteDir = s["dir"]?.ToString() ?? p.RemoteDir;
            p.RemoteDotnet = s["dotnet"]?.ToString() ?? p.RemoteDotnet;
            p.RemoteLauncher = s["launcher"]?.ToString() ?? p.RemoteLauncher;
            if (int.TryParse(s["restPort"]?.ToString(), out int rp) && rp > 0) { p.RemoteRestPort = rp; }
            if (int.TryParse(s["agentPort"]?.ToString(), out int ap) && ap > 0) { p.AgentPort = ap; }
            p.Direct = s["direct"]?.GetValue<bool>() ?? false;
            p.DirectHost = s["directHost"]?.ToString() ?? p.DirectHost;
            if (int.TryParse(s["directPort"]?.ToString(), out int dp) && dp > 0) { p.DirectPort = dp; }
            string encDT = s["directTokenEnc"]?.ToString() ?? "";
            if (encDT.Length > 0) { string d = SecretBox.Unprotect(encDT); if (d.Length > 0) { p.DirectToken = d; } }
            else { p.DirectToken = s["directToken"]?.ToString() ?? p.DirectToken; }
        }
        if (o["paths"] is JsonObject pj)
        {
            p.LocalDir = pj["localDir"]?.ToString() ?? p.LocalDir;
            p.Watchdog = pj["watchdog"] is not { } wv || wv.GetValue<bool>();
            p.ForceLocal = pj["forceLocal"]?.GetValue<bool>() ?? false;
            p.WorldConsoles = pj["worldConsoles"]?.GetValue<bool>() ?? false;
        }
        if (p.SshHost.Length > 0) { p.Name = p.SshHost; }
        return p;
    }

    public static ServersConfig FromJson(JsonObject o)
    {
        var cfg = new ServersConfig
        {
            Version = int.TryParse(o["version"]?.ToString(), out int v) && v > 0 ? v : 1,
            Language = o["language"]?.ToString() ?? "zh-CN",
            ActiveId = o["active"]?.ToString() ?? "",
            BgLogSubs = int.TryParse(o["bgLogSubs"]?.ToString(), out int bl) && bl >= 0 ? bl : 4,
            ReloadPluginsOnSwitch = o["reloadPluginsOnSwitch"]?.GetValue<bool>() ?? true,
        };
        if (o["servers"] is JsonArray arr)
        {
            foreach (var n in arr)
            {
                if (n is not JsonObject so) { continue; }
                var p = new ServerProfile
                {
                    Id = so["id"]?.ToString() ?? NewId(),
                    Name = so["name"]?.ToString() ?? "",
                    Host = so["host"]?.ToString() ?? "127.0.0.1",
                    LocalDir = so["paths"]?["localDir"]?.ToString() ?? "",
                    Watchdog = so["paths"]?["watchdog"]?.GetValue<bool>() ?? true,
                    ForceLocal = so["paths"]?["forceLocal"]?.GetValue<bool>() ?? false,
                    WorldConsoles = so["paths"]?["worldConsoles"]?.GetValue<bool>() ?? false,
                    // 老文件没有这个键：把数组第一档当迁移档（现有配置就是"第一条=迁移来的"），下一次 Save 就补上
                    LegacyDataRoot = so["legacyDataRoot"]?.GetValue<bool>() ?? (cfg.Servers.Count == 0),
                };
                if (int.TryParse(so["port"]?.ToString(), out int port) && port > 0) { p.Port = port; }

                string encTok = so["tokenEnc"]?.ToString() ?? "";
                if (encTok.Length > 0) { p.Token = SecretBox.Unprotect(encTok); }
                string plainTok = so["token"]?.ToString() ?? "";
                if (plainTok.Length > 0)
                {
                    if (p.Token.Length == 0) { p.Token = plainTok; }
                    p.PlainToken = true;
                    cfg.HadPlainToken = true;
                }

                if (so["ssh"] is JsonObject s)
                {
                    p.SshHost = s["host"]?.ToString() ?? p.SshHost;
                    if (int.TryParse(s["port"]?.ToString(), out int sp) && sp > 0) { p.SshPort = sp; }
                    p.SshUser = s["user"]?.ToString() ?? p.SshUser;
                    p.SshKey = s["key"]?.ToString() ?? p.SshKey;
                    p.RemoteDir = s["dir"]?.ToString() ?? p.RemoteDir;
                    p.RemoteDotnet = s["dotnet"]?.ToString() ?? p.RemoteDotnet;
                    p.RemoteLauncher = s["launcher"]?.ToString() ?? p.RemoteLauncher;
                    if (int.TryParse(s["restPort"]?.ToString(), out int rp) && rp > 0) { p.RemoteRestPort = rp; }
                    if (int.TryParse(s["agentPort"]?.ToString(), out int ap) && ap > 0) { p.AgentPort = ap; }
                    p.Direct = s["direct"]?.GetValue<bool>() ?? false;
                    p.DirectHost = s["directHost"]?.ToString() ?? p.DirectHost;
                    if (int.TryParse(s["directPort"]?.ToString(), out int dp) && dp > 0) { p.DirectPort = dp; }
                    string encDT = s["directTokenEnc"]?.ToString() ?? "";
                    if (encDT.Length > 0) { string d = SecretBox.Unprotect(encDT); if (d.Length > 0) { p.DirectToken = d; } }
                    else
                    {
                        p.DirectToken = s["directToken"]?.ToString() ?? p.DirectToken;
                        if (p.DirectToken.Length > 0) { cfg.HadPlainToken = true; }
                    }
                }
                cfg.Servers.Add(p);
            }
        }
        return cfg;
    }

    public JsonObject ToJson()
    {
        var arr = new JsonArray();
        foreach (var p in Servers)
        {
            var so = new JsonObject
            {
                ["id"] = p.Id,
                ["name"] = p.Name,
                // ★ 必须落盘：它决定插件数据用「全局 plugin-data」还是「plugin-data\<档id>」。
                //   漏写会让迁移档在第二次启动时换目录（老插件配置静默"丢失"）。
                ["legacyDataRoot"] = p.LegacyDataRoot,
                ["host"] = p.Host,
                ["port"] = p.Port,
                ["ssh"] = new JsonObject
                {
                    ["host"] = p.SshHost, ["port"] = p.SshPort, ["user"] = p.SshUser, ["key"] = p.SshKey,
                    ["dir"] = p.RemoteDir, ["dotnet"] = p.RemoteDotnet, ["launcher"] = p.RemoteLauncher,
                    ["restPort"] = p.RemoteRestPort, ["agentPort"] = p.AgentPort,
                    ["direct"] = p.Direct, ["directHost"] = p.DirectHost, ["directPort"] = p.DirectPort,
                    ["directTokenEnc"] = SecretBox.Protect(p.DirectToken),
                },
                ["paths"] = new JsonObject
                {
                    ["localDir"] = p.LocalDir, ["watchdog"] = p.Watchdog,
                    ["forceLocal"] = p.ForceLocal, ["worldConsoles"] = p.WorldConsoles,
                },
            };
            // ★ 安全：令牌一律 DPAPI 加密后落盘（同机的其他用户读到也解不开）；加密不可用才退回明文。
            string enc = SecretBox.Protect(p.Token);
            if (enc.Length > 0) { so["tokenEnc"] = enc; }
            else { so["token"] = p.Token; }
            arr.Add(so);
        }
        return new JsonObject
        {
            ["version"] = Version,
            ["language"] = Language,
            ["active"] = ActiveId,
            ["bgLogSubs"] = BgLogSubs,
            ["reloadPluginsOnSwitch"] = ReloadPluginsOnSwitch,
            ["servers"] = arr,
        };
    }

    public void Save(string file)
    {
        try
        {
            File.WriteAllText(file, ToJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            HadPlainToken = false;
            foreach (var p in Servers) { p.PlainToken = false; }
        }
        catch { }
    }
}
