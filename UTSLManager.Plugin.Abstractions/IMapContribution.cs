using Avalonia.Controls;

namespace UTSLManager.Plugins;

/// <summary>
/// 内置「地图预览」页的当前状态快照。
/// ★ 刻意只用**纯数据**（字符串/布尔/整数）—— 插件跑在独立 ALC 里，
///   契约里绝不能出现 Avalonia 控件类型，否则会踩类型身份分裂。
/// </summary>
public sealed record MapViewState(
    string Title,          // 显示名：世界名 / 「区块模板 xxx」/「全部（拼接图）」
    bool IsWorld,          // 单个世界（可以在上面框选、导出模板）
    bool IsStitched,       // 拼接图（跨多个世界，没有统一点坐标系）
    bool IsTemplate,       // 区块模板预览（.utpl）
    int TilesWide,         // 图格尺寸（非世界/未加载时为 0）
    int TilesHigh,
    string WorldRelPath);  // 世界文件相对 Worlds 目录的路径；非世界为空串


/// <summary>图格坐标的矩形选区（相对当前视图左上角）。</summary>
public sealed record MapSelection(int X, int Y, int Width, int Height);

/// <summary>
/// 插件往内置地图页注入界面、并读取当前视图/选区的入口（v4 新增）。
/// 地图页是管理器**内置**的（取代了原来的 GenerateMap 查看），插件只在其上"挂按钮"，
/// 不重复实现地图查看器；需要渲染/裁切/模板编码时可直接调用宿主里 UTSLManager.Maps.* 的公开 API。
/// </summary>
public interface IMapContributionHost
{
    /// <summary>
    /// 往地图页工具栏加一个按钮。enabled 传 null 表示始终可用；否则每次视图变化都会重新求值。
    /// 返回的函数可用来移除该按钮（插件卸载时会自动清空，一般不用手动调）。
    /// </summary>
    Action AddToolbarButton(string text, string tooltip, Func<Task> onClick, Func<MapViewState, bool>? enabled = null);

    /// <summary>
    /// 往地图页工具栏加**任意控件**（下拉框、文本框…），返回移除函数。
    /// ★ 控件类型能安全跨 ALC：插件不自己带 Avalonia（ExcludeAssets=runtime），拿到的是宿主的同名类型 ——
    ///   这与既有的 <c>IManagerPlugin.CreateView</c> 返回 <c>Control</c> 是同一套前提。
    /// </summary>
    Action AddToolbarControl(Control control);

    /// <summary>
    /// 打开/关闭地图页的**框选模式**。选区交互（拖矩形、覆盖层、坐标换算）由宿主的
    /// 地图页自己做，插件只需要开关它、然后读 <see cref="CurrentSelection"/>。
    /// </summary>
    bool SelectionMode { get; set; }

    /// <summary>
    /// 让地图页显示某个区块模板（.utpl）。名字是 <c>lib\Saves\Worlds\_utpl_templates</c> 下的文件名（不带扩展名）。
    /// ★「显示」这件事留在宿主（它才有地图页），插件只提供列表并触发。
    /// 返回 false = 没找到/解析失败。
    /// </summary>
    Task<bool> ShowRegionTemplateAsync(string templateName);

    /// <summary>当前视图状态（可能在后台线程读取，实现内部返回快照）。</summary>
    MapViewState CurrentView { get; }

    /// <summary>当前选区（图格坐标）；没有选区时为 null。</summary>
    MapSelection? CurrentSelection { get; }

    /// <summary>视图或选区变化时触发（在 UI 线程）。用它来刷新按钮 enabled 状态或自己的显示。</summary>
    event Action? Changed;


    /// <summary>往地图页状态行写一句话。</summary>
    void SetStatus(string text);
}

/// <summary>
/// 想往内置地图页注入界面的插件，除 IManagerPlugin 外再实现本接口即可（v4）。
/// 做成**独立接口**而不是往 IManagerPlugin 里加默认方法：这样老插件（v1~v3）完全不受影响，
/// 宿主用 "is IMapContributionPlugin" 就能判断，也不逼所有插件去实现一个用不到的方法。
/// </summary>
public interface IMapContributionPlugin
{
    /// <summary>在插件加载完成后调用一次（UI 线程）。</summary>
    void ContributeMap(IMapContributionHost host);
}
