using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UTSLManager.Plugins;

// 宿主的**内部模型**（从原 Plugins\ManagerApi.cs 切分而来）。
// 这些类型插件看不到、也不该依赖 —— 契约类型已迁到 UTSLManager.Plugin.Abstractions（见 Plugins\TypeForwards.cs）。

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
