using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace UTSLManager.Plugins;

/// <summary>
/// 每个插件一个【可回收】AssemblyLoadContext（P1-8）：
///   1) 宿主已有的程序集（UTSLManager / Avalonia* / 框架）→ 交给默认 ALC，保证类型统一；
///   2) 插件私有依赖 → 先查自己的 .deps.json，再查插件目录，并在本 ALC 内加载；
///   3) 一律【从内存流加载】→ 不锁 dll 文件，允许管理器运行时重新编译/热重载插件。
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver? _resolver;
    private readonly string _pluginDir;
    private readonly HashSet<string> _hostAssemblies;

    public PluginLoadContext(string mainAssemblyPath, string pluginDir, IReadOnlyCollection<string> hostAssemblies)
        : base("ManagerPlugin:" + Path.GetFileNameWithoutExtension(mainAssemblyPath), isCollectible: true)
    {
        _pluginDir = pluginDir;
        _hostAssemblies = new HashSet<string>(hostAssemblies, StringComparer.OrdinalIgnoreCase);
        try { _resolver = new AssemblyDependencyResolver(mainAssemblyPath); } catch { _resolver = null; }
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        string simple = assemblyName.Name ?? "";
        if (simple.Length == 0) { return null; }

        // 1) 宿主已有 → 返回 null，让默认 ALC 解析（共享类型必须走这条路）
        if (_hostAssemblies.Contains(simple)) { return null; }

        // 2) 插件私有依赖
        string? path = null;
        try { path = _resolver?.ResolveAssemblyToPath(assemblyName); } catch { path = null; }
        if (path is null || !File.Exists(path))
        {
            string probe = Path.Combine(_pluginDir, simple + ".dll");
            path = File.Exists(probe) ? probe : null;
        }
        if (path is null) { return null; }   // 交回默认 ALC 兜底

        return LoadFromFile(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        try
        {
            string? p = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (p is not null && File.Exists(p)) { return LoadUnmanagedDllFromPath(p); }
        }
        catch { }
        return IntPtr.Zero;
    }

    /// <summary>从内存加载：dll 文件【不会被锁】→ 管理器运行时也能重新编译/覆盖插件。</summary>
    public Assembly LoadFromFile(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var ms = new MemoryStream(bytes, writable: false);
        return LoadFromStream(ms);
    }
}
