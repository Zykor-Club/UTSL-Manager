using System;
using System.Threading.Tasks;

namespace UTSLManager.Plugins;

/// <summary>
/// 一次文件动作的上下文（v5）。宿主保证：<see cref="RelativePath"/> 与 <see cref="Directory"/> 用正斜杠且相对
/// <see cref="IManagerContext.FilesRoot"/>（远端模式=远端部署目录，本机模式=本机部署目录）。
/// </summary>
public sealed record FileActionContext(
    string RelativePath,   // 例："config/TShockAPI/config.json"
    string FullPath,       // 宿主实际使用的完整路径（远端模式是远端路径，形如 C:\utsl-deploy\...）
    string Directory,      // 所在目录的相对路径（根目录为 ""）
    bool IsDirectory,
    long Size);

/// <summary>
/// 插件往【文件页】注入入口的宿主句柄（v5）。
/// 设计刻意与地图页的 <see cref="IMapContributionHost"/> 保持一致的风格：
/// 宿主拥有界面，插件只"挂"动作；卸载/重载时宿主会自动清空，插件不必手动反注册。
/// </summary>
public interface IFileActionHost
{
    /// <summary>
    /// 注册一个**文件右键动作**（在文件列表的任意行上点右键可见）。
    /// <paramref name="visible"/> 传 null 表示总是显示；否则每次弹出菜单时按当前条目求值。
    /// 返回的函数可用来单独移除该动作（插件重载时宿主会整体清空）。
    /// </summary>
    Action RegisterFileAction(string text, Func<FileActionContext, Task> onClick, Func<FileActionContext, bool>? visible = null);
}

/// <summary>
/// 想往内置「文件管理」页注入动作的插件，除 IManagerPlugin 外再实现本接口即可（v5）。
/// 与地图页同样做成**独立可选接口**：老插件完全不受影响，不逼所有插件实现用不到的方法。
/// </summary>
public interface IFileActionPlugin
{
    /// <summary>插件加载完成后调用一次（UI 线程）。</summary>
    void ContributeFileActions(IFileActionHost host);
}
