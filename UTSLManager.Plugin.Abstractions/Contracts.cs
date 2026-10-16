using System;
using System.Collections.Generic;

namespace UTSLManager.Plugins;

// 插件契约里的**数据/信息类型**（从原 Plugins\ManagerApi.cs 切分出来）。
// 与宿主内部类型（PluginDescriptor / LoadedPlugin / FailedPlugin / PluginLoadReport / PluginManifest）分开：
// 后者留在管理器里，插件看不到，也就无法依赖宿主内部实现。

/// <summary>管理器插件 API 的版本信息。插件用 manifest.apiVersion 或 IManagerPlugin.RequiredApiVersion 声明所需版本。</summary>
public static class ManagerApi
{
    /// <summary>宿主当前实现的插件 API 版本。</summary>
    public const int Version = 6;

    /// <summary>v6：新增**服务端进程控制** —— ServerOnline / StartServerAsync / StopServerAsync
    /// （起停需 exec 权限；走的是与管理器「启动/停止服务端」按钮同一条路径，远端=SSH/控制台启动器）。</summary>
    public const int V6 = 6;

    /// <summary>v1：Name / Icon / Description / Author / OnLoad / CreateView。</summary>
    public const int V1 = 1;

    /// <summary>v5：新增**文件页贡献点** —— 插件（额外实现 <see cref="IFileActionPlugin"/>）可以往内置
    /// 「文件管理」页注册**右键动作**与**工具栏按钮**，拿到当前条目的相对路径/目录/大小
    /// （<see cref="FileActionContext"/>，纯数据）。此前只有地图页有这类上下文挂载点。</summary>
    public const int V5 = 5;

    /// <summary>v3：新增统一远端文件 API（FilesRoot/ListFiles/ReadText/WriteText/CreateDirectory/Rename/Delete/Upload/Download，
    /// 远端模式走 SSH、本机模式直接读盘）、RunCommand（远端命令，exec 权限）、HttpGet/HttpPost（net 权限）、InvalidateFileCache。</summary>
    public const int V3 = 3;

    /// <summary>v4：新增**地图页贡献点** —— 插件（额外实现 <see cref="IMapContributionPlugin"/>）可以往内置
    /// 「地图预览」页注入工具栏按钮，并读取当前视图状态（<see cref="MapViewState"/>，纯数据）与图格选区
    /// （<see cref="MapSelection"/>）。地图页本身仍是管理器内置的；插件只在其上挂按钮，
    /// 需要渲染/裁切/模板编码时直接调用 UTSLManager.Maps.* 的公开 API（宿主已带好 TEdit/SkiaSharp）。</summary>
    // （原来还有 V2/V4 常量，但全仓零引用 —— 版本只有 Version 一个入口，已删。）
}

/// <summary>部署目录里的一个文件/文件夹（v3 起提供给插件）。</summary>
public sealed record PluginFileInfo(string Name, bool IsDirectory, long Size, DateTime Modified)
{
    /// <summary>大小文本（KB/MB），便于直接显示。</summary>
    public string SizeText => IsDirectory ? "" : (Size >= 1048576 ? (Size / 1048576.0).ToString("F2") + " MB" : (Size / 1024) + " KB");
}

/// <summary>一个世界的信息快照。</summary>
public sealed record WorldInfo(string Name, string Size, bool Running);

/// <summary>一次监控采样。</summary>
public sealed record MetricsSample(double CpuPercent, double MemoryGb, double UpKbPerSec, double DownKbPerSec, DateTime At);

/// <summary>终端新增一行。</summary>
public sealed record TerminalLineEvent(string Text, string Level);

/// <summary>插件命令被触发时的参数。</summary>
public sealed class PluginCommandArgs
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> Args { get; init; }
    public required string RawArguments { get; init; }
    /// <summary>当前选中的世界（「全部世界」为 null）。</summary>
    public string? TargetWorld { get; init; }
    public required IManagerContext Context { get; init; }

    /// <summary>把结果写回管理器终端。</summary>
    public void Reply(string text) => Context.Log(text);
}

/// <summary>插件贡献的一个监控指标。</summary>
public sealed class PluginMetricInfo
{
    public required string PluginName { get; init; }
    public required string Name { get; init; }
    public required string Unit { get; init; }
    /// <summary>
    /// 取当前值。★ 宿主会【从后台线程】调用它（每 2 秒一次），所以：
    ///   · 不要在这里碰 UI/控件（需要更新界面请用 ctx 提供的 Dispatcher 或事件）；
    ///   · 必须是线程安全的（通常是读一个 volatile 字段或做一次快照）；
    ///   · 不要在这里做慢操作 —— 它每 2 秒被调用一次，慢操作会拖住采样（需要 ssh/REST 请自己后台轮询后缓存）。
    /// </summary>
    public required Func<double?> Provider { get; init; }
    public double? Value { get; set; }

    public string Display => Value is null ? "—" : (Value.Value >= 100 ? Value.Value.ToString("F0") : Value.Value.ToString("F2")) + (Unit.Length == 0 ? "" : " " + Unit);
}

/// <summary>插件贡献的一个文本状态项。</summary>
public sealed class PluginStatusInfo
{
    public required string PluginName { get; init; }
    public required string Name { get; init; }
    /// <summary>取当前状态文本。约束同 PluginMetricInfo.Provider：可能从后台线程调用、必须线程安全、不要碰 UI。</summary>
    public required Func<string> Provider { get; init; }
    public string Value { get; set; } = "";
}
