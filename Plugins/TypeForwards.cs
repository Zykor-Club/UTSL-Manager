using System.Runtime.CompilerServices;

// ============================================================================
//  P0：插件契约已迁到独立程序集 UTSLManager.Plugin.Abstractions。
//  这里做【类型转发】，好处是双向零破坏：
//    · 已编译的旧插件 dll（按旧版 UTSLManager.dll 编译）仍能正常加载 —— 运行时顺着转发找到新程序集；
//    · 现有插件源码一行都不用改 —— 编译期 Roslyn 同样会跟随类型转发。
//  ★ 新插件请直接引用 UTSLManager.Plugin.Abstractions（约 30KB），不要再引用整个 UTSLManager.dll。
//  ★ 契约的命名空间保持 UTSLManager.Plugins 不变，所以 using 也无需改动。
// ============================================================================

[assembly: TypeForwardedTo(typeof(UTSLManager.Plugins.IManagerPlugin))]
[assembly: TypeForwardedTo(typeof(UTSLManager.Plugins.IManagerContext))]
[assembly: TypeForwardedTo(typeof(UTSLManager.Plugins.ManagerApi))]
[assembly: TypeForwardedTo(typeof(UTSLManager.Plugins.ManagerEvents))]
[assembly: TypeForwardedTo(typeof(UTSLManager.Plugins.PluginFileInfo))]
[assembly: TypeForwardedTo(typeof(UTSLManager.Plugins.WorldInfo))]
[assembly: TypeForwardedTo(typeof(UTSLManager.Plugins.MetricsSample))]
[assembly: TypeForwardedTo(typeof(UTSLManager.Plugins.TerminalLineEvent))]
[assembly: TypeForwardedTo(typeof(UTSLManager.Plugins.PluginCommandArgs))]
[assembly: TypeForwardedTo(typeof(UTSLManager.Plugins.PluginMetricInfo))]
[assembly: TypeForwardedTo(typeof(UTSLManager.Plugins.PluginStatusInfo))]
[assembly: TypeForwardedTo(typeof(UTSLManager.Plugins.MapViewState))]
[assembly: TypeForwardedTo(typeof(UTSLManager.Plugins.MapSelection))]
[assembly: TypeForwardedTo(typeof(UTSLManager.Plugins.IMapContributionHost))]
[assembly: TypeForwardedTo(typeof(UTSLManager.Plugins.IMapContributionPlugin))]
[assembly: TypeForwardedTo(typeof(UTSLManager.Plugins.FileActionContext))]
[assembly: TypeForwardedTo(typeof(UTSLManager.Plugins.IFileActionHost))]
[assembly: TypeForwardedTo(typeof(UTSLManager.Plugins.IFileActionPlugin))]
