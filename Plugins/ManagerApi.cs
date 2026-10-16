using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UTSLManager.Plugins;

/// <summary>管理器插件 API 的版本信息。插件用 manifest.apiVersion 或 IManagerPlugin.RequiredApiVersion 声明所需版本。</summary>
public static class ManagerApi
{
    /// <summary>宿主当前实现的插件 API 版本。</summary>
    public const int Version = 4;

    /// <summary>v1：Name / Icon / Description / Author / OnLoad / CreateView。</summary>
    public const int V1 = 1;

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

/// <summary>插件的静态描述（来自目录 + manifest）。</summary>
public sealed record PluginDescriptor(
    string DllPath,
    string Directory,
    string Key,
    string? Version,
    string? Author,
    string? Description,
    int ApiVersion,
    string[] Dependencies,
    string[]? Permissions,
    string? Entry,
    string? Type);

/// <summary>一个成功加载的插件。</summary>
public sealed class LoadedPlugin
{
    public required IManagerPlugin Instance { get; init; }
    public required PluginDescriptor Descriptor { get; init; }
    /// <summary>插件专属上下文（宿主内部使用）。</summary>
    internal PluginContext? Ctx { get; init; }
    /// <summary>该插件的上下文（供外部工具/测试调用 CreateView 等）。</summary>
    public IManagerContext? Context => Ctx;
    /// <summary>该插件的命令（名字 → 描述）。</summary>
    public Dictionary<string, string> Commands { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string Name => Instance.Name;
    public string Icon => Instance.Icon;
    public string Description => Instance.Description;
    public string Author => Instance.Author;
    public string Version => string.IsNullOrWhiteSpace(Descriptor.Version) ? "?" : Descriptor.Version!;
    public string SourceFile => Path.GetFileName(Descriptor.DllPath);
    public int ApiVersion => Descriptor.ApiVersion;
    public string? Type => Descriptor.Type;
    public string PermissionsText => Descriptor.Permissions is null or { Length: 0 } ? "全部（未声明）" : string.Join(", ", Descriptor.Permissions);
}

/// <summary>一个加载失败的插件文件及原因。</summary>
public sealed record FailedPlugin(string DllPath, string Reason);

/// <summary>一次加载的完整结果（供「插件管理」页展示）。</summary>
public sealed class PluginLoadReport
{
    public List<LoadedPlugin> Loaded { get; } = [];
    public List<FailedPlugin> Failed { get; } = [];
    public List<string> Scanned { get; } = [];
    public DateTime At { get; } = DateTime.Now;
    public string Directory { get; init; } = "";
}

/// <summary>
/// 可选清单（与插件 dll 同目录）：<c>&lt;插件名&gt;.manifest.json</c> 或 <c>manifest.json</c>。
/// 缺失或损坏都不影响加载。全部字段可选。
/// </summary>
public sealed class PluginManifest
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("author")] public string? Author { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }

    /// <summary>入口类型（全名或短名）。留空 = 扫描程序集里所有 IManagerPlugin 实现。</summary>
    [JsonPropertyName("entry")] public string? Entry { get; set; }

    /// <summary>用途标注：view / action / service。仅记录，不影响加载。</summary>
    [JsonPropertyName("type")] public string? Type { get; set; }

    /// <summary>要求的管理器 API 版本（缺省=1，即 v1 契约）。</summary>
    [JsonPropertyName("apiVersion")] public int? ApiVersion { get; set; }

    /// <summary>依赖的其他插件（按 name 或 dll 名匹配，可写 "Name@>=1.2.0"，版本段暂不校验）。</summary>
    [JsonPropertyName("dependencies")] public string[]? Dependencies { get; set; }

    /// <summary>声明的权限：rest / fs / config。不写 = 全部允许（向后兼容）。</summary>
    [JsonPropertyName("permissions")] public string[]? Permissions { get; set; }

    public static PluginManifest? TryLoad(string dllPath)
    {
        try
        {
            string dir = Path.GetDirectoryName(dllPath) ?? ".";
            string baseName = Path.GetFileNameWithoutExtension(dllPath);
            foreach (string f in new[] { Path.Combine(dir, baseName + ".manifest.json"), Path.Combine(dir, "manifest.json") })
            {
                if (!File.Exists(f)) { continue; }
                var m = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(f), JsonOptions);
                if (m is not null) { return m; }
            }
        }
        catch { /* 清单坏了不影响加载：按无清单处理 */ }
        return null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
