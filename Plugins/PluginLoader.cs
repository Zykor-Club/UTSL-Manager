using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace UTSLManager.Plugins;

/// <summary>
/// 插件发现 + 依赖排序 + 程序集加载。加载结果（成功/失败/原因）全部写进 <see cref="PluginLoadReport"/>，
/// 供「插件管理」页展示（P0-3），而不是只打一行日志。
/// </summary>
internal static class PluginLoader
{
    public static PluginLoadReport Load(PluginHost host)
    {
        string dir = host.Options.PluginDir;
        var report = new PluginLoadReport { Directory = dir };
        try { Directory.CreateDirectory(dir); } catch { }

        var files = DiscoverFiles(dir, report.Scanned);
        if (files.Count == 0)
        {
            host.Options.Log("[插件] " + dir + " 下没有管理器插件（放 .dll 进去即可扩展功能）");
            return report;
        }

        var entries = new List<(PluginDescriptor d, PluginManifest? m)>();
        foreach (var f in files) { var mf = PluginManifest.TryLoad(f); entries.Add((Describe(f, mf), mf)); }
        // ★ 修 bug#13：依赖解析一旦抛异常，不能让它把整轮插件都废掉 —— 兜底退化为按文件顺序加载
        List<FailedPlugin> depFailures = new();
        List<PluginDescriptor> ordered;
        try
        {
            (ordered, depFailures) = TopoSort(entries.Select(e => e.d).ToList());
        }
        catch (Exception ex)
        {
            host.Options.Log("[插件] 依赖解析失败（" + ex.GetType().Name + ": " + ex.Message + "），改为按文件顺序加载");
            ordered = entries.Select(e => e.d).ToList();
        }
        report.Failed.AddRange(depFailures);

        // 记录"已判定失败"的插件 key/dll 名，供依赖者跳过
        var failedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in depFailures)
        {
            failedKeys.Add(Path.GetFileNameWithoutExtension(f.DllPath));
        }
        foreach (var d in entries.Select(e => e.d))
        {
            if (depFailures.Any(f => f.DllPath == d.DllPath)) { failedKeys.Add(d.Key); }
        }

        foreach (var d in ordered)
        {
            if (d.ApiVersion > ManagerApi.Version)
            {
                report.Failed.Add(new FailedPlugin(d.DllPath, $"需要 API v{d.ApiVersion}，本管理器支持到 v{ManagerApi.Version}（请更新管理器）"));
                failedKeys.Add(d.Key); failedKeys.Add(Path.GetFileNameWithoutExtension(d.DllPath));
                continue;
            }

            string? badDep = d.Dependencies.Select(StripVersion).FirstOrDefault(dep => failedKeys.Contains(dep));
            if (badDep is not null)
            {
                report.Failed.Add(new FailedPlugin(d.DllPath, $"依赖的插件 {badDep} 未成功加载（已跳过）"));
                failedKeys.Add(d.Key); failedKeys.Add(Path.GetFileNameWithoutExtension(d.DllPath));
                continue;
            }

            PluginLoadContext? alc = null;
            try
            {
                string dllName = Path.GetFileName(d.DllPath);
                var (asm, ctx) = IsScript(d.DllPath)
                    ? CompileScript(d, host.HostAssemblies)
                    : LoadAssembly(d, host.HostAssemblies);
                alc = ctx;

                var types = FindTypes(asm, d.Entry, out string? entryNote);
                if (entryNote is not null) { host.Options.Log("[插件] " + dllName + "：" + entryNote); }
                if (types.Count == 0)
                {
                    report.Failed.Add(new FailedPlugin(d.DllPath, "没有 IManagerPlugin 实现"));
                    try { alc.Unload(); } catch { }
                    failedKeys.Add(d.Key); failedKeys.Add(Path.GetFileNameWithoutExtension(d.DllPath));
                    continue;
                }

                int ok = 0;
                foreach (var t in types)
                {
                    try
                    {
                        if (Activator.CreateInstance(t) is not IManagerPlugin inst) { continue; }
                        if (inst.RequiredApiVersion > ManagerApi.Version)
                        {
                            report.Failed.Add(new FailedPlugin(d.DllPath, $"{t.Name} 需要 API v{inst.RequiredApiVersion} > v{ManagerApi.Version}"));
                            continue;
                        }

                        var pctx = new PluginContext(host, d, inst);
                        var lp = new LoadedPlugin { Instance = inst, Descriptor = d, Ctx = pctx };
                        inst.OnLoad(pctx);
                        foreach (var kv in pctx.Commands) { lp.Commands[kv.Key] = kv.Value.Description; }
                        host.AddPlugin(lp, pctx);
                        report.Loaded.Add(lp);
                        ok++;

                        string author = string.IsNullOrWhiteSpace(inst.Author) ? (d.Author ?? "?") : inst.Author;
                        string kind = string.IsNullOrWhiteSpace(d.Type) ? "" : " [" + d.Type + "]";
                        host.Options.Log($"[插件] 已加载 {inst.Icon} {inst.Name} v{lp.Version}{kind}（{dllName}，作者 {author}）");
                    }
                    catch (Exception ex)
                    {
                        report.Failed.Add(new FailedPlugin(d.DllPath, $"{t.Name} 初始化失败: {ex.Message}"));
                    }
                }

                if (ok > 0) { host.RegisterAlc(alc); }
                else
                {
                    try { alc.Unload(); } catch { }
                    failedKeys.Add(d.Key); failedKeys.Add(Path.GetFileNameWithoutExtension(d.DllPath));
                }
            }
            catch (Exception ex)
            {
                report.Failed.Add(new FailedPlugin(d.DllPath, "加载失败: " + ex.Message));
                try { alc?.Unload(); } catch { }
                failedKeys.Add(d.Key); failedKeys.Add(Path.GetFileNameWithoutExtension(d.DllPath));
            }
        }

        host.Options.Log(report.Loaded.Count == 0
            ? $"[插件] 未加载任何管理器插件（失败 {report.Failed.Count} 个，详见「插件管理」）"
            : $"[插件] 共加载 {report.Loaded.Count} 个管理器插件" + (report.Failed.Count == 0 ? "" : $"（失败 {report.Failed.Count} 个，详见「插件管理」）"));
        return report;
    }

    // ===== 发现 =====

    /// <summary>支持两种布局：扁平 plugins/*.dll，以及「一插件一目录」plugins/&lt;名&gt;/&lt;名&gt;.dll（带私有依赖时推荐）。</summary>
    private static List<string> DiscoverFiles(string dir, List<string> scanned)
    {
        var list = new List<string>();
        try
        {
            foreach (var f in Directory.GetFiles(dir).Where(IsPluginFile).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) { list.Add(f); }

            foreach (var sub in Directory.GetDirectories(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(sub);
                string preferredDll = Path.Combine(sub, name + ".dll");
                string preferredCsx = Path.Combine(sub, name + ".csx");
                if (File.Exists(preferredDll)) { list.Add(preferredDll); continue; }
                if (File.Exists(preferredCsx)) { list.Add(preferredCsx); continue; }

                // 退而求其次：目录里有清单的插件文件；再没有就取第一个
                var withManifest = Directory.GetFiles(sub)
                    .Where(IsPluginFile)
                    .Where(f => File.Exists(Path.Combine(sub, Path.GetFileNameWithoutExtension(f) + ".manifest.json")))
                    .OrderBy(x => x).ToList();
                if (withManifest.Count > 0) { list.AddRange(withManifest); }
                else
                {
                    var any = Directory.GetFiles(sub).Where(IsPluginFile).OrderBy(x => x).ToList();
                    if (any.Count > 0) { list.Add(any[0]); }
                }
            }
        }
        catch (Exception ex) { scanned.Add("(扫描异常: " + ex.Message + ")"); }
        scanned.AddRange(list);
        return list;
    }

    private static PluginDescriptor Describe(string dll, PluginManifest? m)
    {
        string dirName = Path.GetDirectoryName(dll) ?? ".";
        string baseName = Path.GetFileNameWithoutExtension(dll);
        string key = string.IsNullOrWhiteSpace(m?.Name) ? baseName : m!.Name!.Trim();
        return new PluginDescriptor(
            dll, dirName, key, m?.Version, m?.Author, m?.Description,
            m?.ApiVersion ?? ManagerApi.V1,
            m?.Dependencies ?? Array.Empty<string>(),
            m?.Permissions, m?.Entry, m?.Type);
    }

    // ===== 依赖排序（P1-9）=====

    private static (List<PluginDescriptor> ordered, List<FailedPlugin> failed) TopoSort(List<PluginDescriptor> all)
    {
        var byKey = new Dictionary<string, PluginDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in all)
        {
            byKey[d.Key] = d;
            byKey[Path.GetFileNameWithoutExtension(d.DllPath)] = d;
        }

        var state = new Dictionary<PluginDescriptor, int>();   // 0 未访问 / 1 访问中 / 2 完成 / 3 失败
        foreach (var d in all) { state[d] = 0; }
        var ordered = new List<PluginDescriptor>();
        var failed = new List<FailedPlugin>();

        void Fail(PluginDescriptor d, string reason)
        {
            if (state[d] == 3) { return; }
            state[d] = 3;
            failed.Add(new FailedPlugin(d.DllPath, reason));
        }

        void Visit(PluginDescriptor d)
        {
            if (state[d] is 2 or 3) { return; }
            if (state[d] == 1) { Fail(d, "依赖成环（循环依赖）"); return; }
            state[d] = 1;
            foreach (var raw in d.Dependencies)
            {
                string dep = StripVersion(raw);
                // ★ 修 bug#13：manifest 写成 [null] / [""] 时只判「该插件」失败并给出原因，不能整轮挂掉
                if (dep.Length == 0) { Fail(d, "依赖项为空或为 null（manifest 写错了）"); return; }
                if (!byKey.TryGetValue(dep, out var dd)) { Fail(d, "缺少依赖插件 " + dep); return; }
                if (ReferenceEquals(dd, d)) { Fail(d, "依赖自己"); return; }
                Visit(dd);
                if (state[dd] == 3) { Fail(d, "依赖的插件 " + dd.Key + " 加载失败"); return; }
            }
            state[d] = 2;
            ordered.Add(d);
        }

        foreach (var d in all) { Visit(d); }
        return (ordered, failed);
    }

    private static string StripVersion(string? dep)
    {
        if (string.IsNullOrWhiteSpace(dep)) { return string.Empty; }   // ★ 修 bug#13：manifest 里可能是 null
        int at = dep.IndexOf('@');
        return (at >= 0 ? dep[..at] : dep).Trim();
    }

    // ===== 加载 =====

    private static bool IsPluginFile(string path) =>
        path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".csx", StringComparison.OrdinalIgnoreCase);

    private static bool IsScript(string path) => path.EndsWith(".csx", StringComparison.OrdinalIgnoreCase);

    private static readonly Lazy<Microsoft.CodeAnalysis.Scripting.ScriptOptions> ScriptOpts = new(() =>
        Microsoft.CodeAnalysis.Scripting.ScriptOptions.Default
            .WithReferences(
                typeof(IManagerPlugin).Assembly,
                typeof(Avalonia.Controls.Control).Assembly,
                typeof(Avalonia.Media.SolidColorBrush).Assembly,
                typeof(Avalonia.Layout.Orientation).Assembly)
            .WithImports(
                "UTSLManager.Plugins", "Avalonia", "Avalonia.Controls", "Avalonia.Layout", "Avalonia.Media",
                "System", "System.Linq", "System.Collections.Generic", "System.Threading.Tasks"));

    /// <summary>P2-12：把 .csx 用 Roslyn 编译成程序集，再装进可回收 ALC（和 dll 插件走同一套发现/校验/隔离）。</summary>
    private static (Assembly asm, PluginLoadContext ctx) CompileScript(PluginDescriptor d, IReadOnlyCollection<string> hostAssemblies)
    {
        string code = File.ReadAllText(d.DllPath);
        var compilation = Microsoft.CodeAnalysis.CSharp.Scripting.CSharpScript.Create(code, ScriptOpts.Value).GetCompilation();
        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        if (!result.Success)
        {
            var errs = result.Diagnostics
                .Where(x => x.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .Take(3)
                .Select(x => x.Id + ": " + x.GetMessage());
            throw new InvalidOperationException("脚本编译失败 -> " + string.Join(" | ", errs));
        }
        ms.Position = 0;
        var ctx = new PluginLoadContext(d.DllPath, d.Directory, hostAssemblies);
        try { return (ctx.LoadFromStream(ms), ctx); }
        catch { try { ctx.Unload(); } catch { } throw; }
    }

    private static (Assembly asm, PluginLoadContext ctx) LoadAssembly(PluginDescriptor d, IReadOnlyCollection<string> hostAssemblies)
    {
        var ctx = new PluginLoadContext(d.DllPath, d.Directory, hostAssemblies);
        try { return (ctx.LoadFromFile(d.DllPath), ctx); }
        catch { try { ctx.Unload(); } catch { } throw; }
    }

    private static List<Type> FindTypes(Assembly asm, string? entry, out string? note)
    {
        note = null;
        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).ToArray()!; }

        var cands = types.Where(t => t is not null
                                     && typeof(IManagerPlugin).IsAssignableFrom(t)
                                     && !t.IsAbstract && !t.IsInterface)
                         .Cast<Type>().ToList();

        if (!string.IsNullOrWhiteSpace(entry))
        {
            var picked = cands.Where(t => t.FullName == entry || t.Name == entry).ToList();
            if (picked.Count > 0) { return picked; }
            note = "manifest.entry='" + entry + "' 未找到，回退为扫描全部类型";
        }
        return cands;
    }
}
