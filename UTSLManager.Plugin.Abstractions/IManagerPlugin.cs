using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace UTSLManager.Plugins;

/// <summary>
/// 管理器插件契约。放在 &lt;UTSLManager.exe 目录&gt;\plugins\ 下（扁平 dll 或「一插件一目录」均可），
/// 管理器启动时自动加载。一个插件 = 一个实现 IManagerPlugin 的类（可无 UI，仅提供动作/服务）。
/// </summary>
public interface IManagerPlugin
{
    /// <summary>显示名（会出现在右侧「扩展」菜单）。</summary>
    string Name { get; }

    /// <summary>菜单图标（emoji 即可）。</summary>
    string Icon { get; }

    /// <summary>一句话说明（插件页副标题 / 管理页）。</summary>
    string Description { get; }

    /// <summary>作者。</summary>
    string Author { get; }

    /// <summary>
    /// 本插件要求的管理器 API 版本。宿主低于该值时会跳过加载并给出提示。
    /// 默认 1（v1 契约），用新能力（事件/命令/指标/配置/i18n）请覆写为 <see cref="ManagerApi.Version"/>。
    /// </summary>
    int RequiredApiVersion => ManagerApi.V1;

    /// <summary>加载时调用一次。抛异常只会禁用本插件，不影响管理器和其他插件。</summary>
    void OnLoad(IManagerContext ctx);

    /// <summary>卸载/重载/关闭时调用。用于保存状态、停止后台任务、释放资源。默认空实现。</summary>
    void OnUnload() { }

    /// <summary>返回要放进中间区域的界面；纯动作/服务型插件返回 null。默认 null。</summary>
    Control? CreateView(IManagerContext ctx) => null;
}

/// <summary>管理器提供给单个插件的能力（按插件隔离：配置目录、命令、指标、权限各自独立）。</summary>
public interface IManagerContext
{
    // ===== 基本信息 =====
    /// <summary>插件显示名。</summary>
    string PluginName { get; }
    /// <summary>插件 dll 所在目录。</summary>
    string PluginDir { get; }
    /// <summary>插件专属数据目录（自动创建，可读写）。</summary>
    string DataDir { get; }
    /// <summary>utsl-deploy 根目录。</summary>
    string DeployRoot { get; }
    /// <summary>当前 REST 基地址，例如 http://127.0.0.1:7878。</summary>
    string RestBase { get; }
    /// <summary>当前 REST 令牌。</summary>
    string Token { get; }
    /// <summary>管理器界面语言（默认 zh-CN）。</summary>
    string Language { get; }
    /// <summary>本插件声明的权限（未声明=全部允许，此时返回空集合）。</summary>
    IReadOnlyCollection<string> Permissions { get; }

    // ===== 日志 =====
    /// <summary>写一行到管理器终端（自动按级别着色）。</summary>
    void Log(string message);

    // ===== REST（需 rest 权限）=====
    /// <summary>GET 管理器当前连接的 UTSL REST（自动带令牌）。relativeUrl 例如 "/world/list"。</summary>
    Task<string> RestGet(string relativeUrl);
    /// <summary>POST 管理器当前连接的 UTSL REST（自动带令牌）。</summary>
    Task<string> RestPost(string relativeUrl);

    // ===== 世界 =====
    /// <summary>当前世界列表快照。</summary>
    IReadOnlyList<WorldInfo> Worlds { get; }

    // ===== 事件 / 生命周期 =====
    /// <summary>管理器事件总线。</summary>
    ManagerEvents Events { get; }
    /// <summary>管理器关闭时取消。</summary>
    CancellationToken ShutdownToken { get; }
    /// <summary>跑一个后台任务：异常会被捕获并写进终端，关闭时自动取消。</summary>
    void RunBackground(Func<CancellationToken, Task> work, string? name = null);

    // ===== 终端命令 =====
    /// <summary>注册一个管理器终端命令：在输入框里用 "!名字 参数" 触发。</summary>
    void RegisterCommand(string name, string description, Func<PluginCommandArgs, Task> handler);
    /// <summary>注销命令。</summary>
    void UnregisterCommand(string name);

    // ===== 指标 =====
    /// <summary>注册一个监控指标（显示在右侧「插件指标」区）。</summary>
    void RegisterMetric(string name, string unit, Func<double?> provider);
    /// <summary>注销指标。</summary>
    void UnregisterMetric(string name);

    // ===== 状态贡献（P2-15）=====
    /// <summary>注册一个文本状态项（显示在右侧「插件指标」区，可做状态胶囊）。</summary>
    void RegisterStatus(string name, Func<string> provider);
    /// <summary>注销状态项。</summary>
    void UnregisterStatus(string name);

    // ===== 配置 / 存储（配置需 config 权限）=====
    /// <summary>读插件配置（缺省文件 config.json），不存在时返回默认值。</summary>
    T GetConfig<T>(string? fileName = null) where T : new();
    /// <summary>写插件配置。</summary>
    void SaveConfig<T>(T value, string? fileName = null);
    /// <summary>读 utsl-deploy 下的文本文件（需 fs 权限）。</summary>
    string ReadDeployText(string relativePath);
    /// <summary>写 utsl-deploy 下的文本文件（需 fs 权限）。</summary>
    void WriteDeployText(string relativePath, string content);

    // ===== 文件（v3；远端/本机统一，需 fs 权限）=====
    /// <summary>当前实际的部署目录：远端模式=远端目录，本机模式=本机目录。下面所有相对路径都相对它。</summary>
    string FilesRoot { get; }
    /// <summary>列目录（relativeDir 为空=根）。返回 null 表示读取失败。</summary>
    IReadOnlyList<PluginFileInfo>? ListFiles(string relativeDir);
    /// <summary>判断文件/目录是否存在。</summary>
    bool FileExists(string relativePath);
    /// <summary>读文本文件；失败返回空串。</summary>
    string ReadText(string relativePath);
    /// <summary>写文本文件（远端为原子写：先 .tmp 再改名）；返回是否成功。</summary>
    bool WriteText(string relativePath, string content);
    /// <summary>新建目录。</summary>
    bool CreateDirectory(string relativeDir);
    /// <summary>改名/移动（同目录内），newName 只给新名字。</summary>
    bool Rename(string relativePath, string newName);
    /// <summary>删除文件或目录（目录递归）。</summary>
    bool Delete(string relativePath);
    /// <summary>本机文件 → 部署目录（远端走 scp）；返回是否成功。</summary>
    bool Upload(string localPath, string relativePath);
    /// <summary>部署目录 → 本机文件（远端走 scp）；返回是否成功。</summary>
    bool Download(string relativePath, string localPath);
    /// <summary>通知宿主清空文件缓存（插件改过文件后调用，否则宿主 90 秒内仍返回旧数据）。</summary>
    void InvalidateFileCache();

    // ===== 远端命令（v3；需 exec 权限）=====
    /// <summary>在部署主机上执行一段 PowerShell（远端模式走 ssh，本机模式本地执行）。<b>能力很强，请谨慎使用</b>。</summary>
    Task<string> RunCommand(string powerShell, int timeoutMs = 30000);

    // ===== HTTP（v3；需 net 权限）=====
    /// <summary>GET 任意 http(s) 地址（带 15 秒超时）。</summary>
    Task<string> HttpGet(string url);
    /// <summary>POST 任意 http(s) 地址；jsonBody 为 null 则不发送正文。</summary>
    Task<string> HttpPost(string url, string? jsonBody = null);

    // ===== 服务端进程（v6；起停需 exec 权限）=====
    /// <summary>
    /// 服务端当前是否在线 —— 管理器**心跳探测**的结果（本机/远端都适用）。后台线程可读。
    /// ★ 语义是"能不能连上服务端"，不是"本机有没有这个进程"。
    /// </summary>
    bool ServerOnline { get; }
    /// <summary>
    /// 启动服务端进程（本机直接起；远端走 SSH/控制台启动器）。reason 会写进管理器终端，便于事后审计。
    /// 返回是否成功**下发**（注意：启动成功 ≠ 世界加载完成，通常还要等几十秒到几分钟）。
    /// </summary>
    Task<bool> StartServerAsync(string reason = "");
    /// <summary>停止服务端进程。reason 会写进管理器终端。</summary>
    Task<bool> StopServerAsync(string reason = "");

    // ===== i18n =====
    /// <summary>注册某语言的字符串表（通常在 OnLoad 里调用）。</summary>
    void RegisterStrings(string language, IReadOnlyDictionary<string, string> strings);
    /// <summary>取当前语言的字符串；没有则回退 fallback，再回退 key 本身。</summary>
    string L(string key, string? fallback = null);
}
