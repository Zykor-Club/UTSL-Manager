using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using TEdit.Terraria;
using UTSLManager.Maps;
using UTSLManager.Plugins;

namespace MapTemplates;

/// <summary>
/// 建筑区块模板扩展：往管理器**内置的地图预览页**挂三样东西 —— 框选开关、模板名+导出、区块模板浏览。
/// 渲染/裁切/编解码都调宿主里的 UTSLManager.Maps.*（宿主已带好 TEdit/SkiaSharp），本插件不重复带依赖。
/// 服务端没有这套能力（没装 VirtualWorldCore 也没有任何 .utpl）时不挂任何按钮。
/// </summary>
public sealed class MapTemplatesPlugin : IManagerPlugin, IMapContributionPlugin
{
    private IManagerContext? _ctx;
    private IMapContributionHost? _host;

    private ToggleButton? _selBtn;
    private TextBox? _nameBox;
    private Button? _exportBtn;
    private ComboBox? _tplBox;
    private string _armedName = "";

    public string Name => "建筑模板";
    public string Icon => "🏗";
    public string Description => "地图上框选并导出 .utpl 建筑模板；浏览服务端已有模板（服务端需装 VirtualWorldCore）";
    public string Author => "Zykor-Club";
    public int RequiredApiVersion => ManagerApi.Version;      // v4：地图页贡献点

    public void OnLoad(IManagerContext ctx)
    {
        _ctx = ctx;
        ctx.Log("[建筑模板] 已加载：地图页上的「框选 / 导出模板 / 区块模板」由本插件提供。");
    }

    // ==================== 地图页贡献 ====================

    public void ContributeMap(IMapContributionHost host)
    {
        // ★ 幂等：宿主每次进地图页/切档都会调一次，重复调用会把「框选/导出模板/浏览区块模板」
        //   再追加一套（实测界面上出现两排一样的控件）。
        if (_selBtn is not null) { return; }
        _host = host;
        IManagerContext? ctx = _ctx;
        if (ctx is null) { return; }

        TemplateCapability cap = Detect(ctx);
        if (!cap.Supported)
        {
            ctx.Log("[建筑模板] 服务端没有区块模板能力（没装 VirtualWorldCore，也没有任何 .utpl）→ 不挂按钮。");
            return;
        }

        _selBtn = new ToggleButton
        {
            Content = "框选", FontSize = 11.5, Padding = new Thickness(10, 5), CornerRadius = new CornerRadius(9),
        };
        ToolTip.SetTip(_selBtn, "打开后在地图上按住左键拖出矩形；再点一次关闭");
        _selBtn.IsCheckedChanged += (_, _) =>
        {
            host.SelectionMode = _selBtn.IsChecked == true;
            if (_selBtn.IsChecked != true) { UpdateEnabled(); }
        };

        _nameBox = new TextBox
        {
            Width = 118, FontSize = 11.5, Padding = new Thickness(8, 4), CornerRadius = new CornerRadius(9),
            VerticalAlignment = VerticalAlignment.Center, PlaceholderText = "模板名（可留空）",
        };

        _exportBtn = new Button
        {
            Content = "导出模板", FontSize = 11.5, Padding = new Thickness(10, 5), CornerRadius = new CornerRadius(9),
            IsEnabled = false,
        };
        ToolTip.SetTip(_exportBtn, "把当前框选范围导出成 .utpl，并上传到服务端模板目录");
        _exportBtn.Click += async (_, _) => await ExportAsync();

        _tplBox = new ComboBox { MinWidth = 150, FontSize = 11.5, PlaceholderText = "浏览区块模板" };
        ToolTip.SetTip(_tplBox, "选择后在地图页显示该 .utpl");
        _tplBox.SelectionChanged += async (_, _) =>
        {
            if (_tplBox.SelectedItem is string name && name.Length > 0) { await host.ShowRegionTemplateAsync(name); }
        };

        host.AddToolbarControl(_selBtn);
        host.AddToolbarControl(_nameBox);
        host.AddToolbarControl(_exportBtn);
        host.AddToolbarControl(_tplBox);

        // 视图/选区变化 → 刷新按钮可用性（选区为空就不能导出；模板预览不能导出）
        host.Changed += UpdateEnabled;
        UpdateEnabled();
        RefreshTemplateList();

        ctx.Log($"[建筑模板] 已挂载：核心模块={cap.CoreModule} 消费端玩法={cap.Consumer} 已有模板={cap.AnyTemplates}"
              + (cap.NeedsConsumer ? "（提示：服务端还没有能消费模板的玩法，如尘歌壶的 /cg paste）" : ""));
    }

    private static TemplateCapability Detect(IManagerContext ctx)
    {
        bool anyTemplates;
        try { anyTemplates = ListTemplates(ctx).Count > 0; } catch { anyTemplates = false; }
        return ServerCapabilities.DetectTemplate(rel => ctx.FileExists(rel), anyTemplates);
    }

    private static List<string> ListTemplates(IManagerContext ctx)
        => (ctx.ListFiles("lib/Saves/Worlds/_utpl_templates") ?? Array.Empty<PluginFileInfo>())
            .Where(f => !f.IsDirectory && f.Name.EndsWith(UtplCodec.Extension, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => Path.GetFileNameWithoutExtension(f.Name))
            .ToList();

    private void RefreshTemplateList()
    {
        if (_tplBox is null || _ctx is null) { return; }
        string keep = _tplBox.SelectedItem as string ?? "";
        List<string> items = ListTemplates(_ctx);
        _tplBox.ItemsSource = items;
        if (keep.Length > 0 && items.Contains(keep)) { _tplBox.SelectedItem = keep; }
    }

    private void UpdateEnabled()
    {
        if (_exportBtn is null || _host is null) { return; }
        MapSelection? sel = _host.CurrentSelection;
        MapViewState view = _host.CurrentView;
        _exportBtn.IsEnabled = sel is not null && view.IsWorld && view.WorldRelPath.Length > 0;
    }

    // ==================== 导出 ====================

    private async Task ExportAsync()
    {
        IManagerContext? ctx = _ctx;
        IMapContributionHost? host = _host;
        if (ctx is null || host is null) { return; }

        MapSelection? sel = host.CurrentSelection;
        MapViewState view = host.CurrentView;
        if (sel is null) { host.SetStatus("先在「框选」模式下拖出一个矩形"); return; }
        if (!view.IsWorld || view.WorldRelPath.Length == 0)
        {
            host.SetStatus("请先在地图页选一个世界（拼接图与模板预览不能导出）");
            return;
        }

        int w = sel.Width, h = sel.Height;
        string name = UtplCodec.SanitizeName(_nameBox?.Text);
        if (name.Length == 0)
        {
            name = UtplCodec.SanitizeName(Path.GetFileNameWithoutExtension(view.WorldRelPath) + "_" + w + "x" + h);
            if (_nameBox is not null) { _nameBox.Text = name; }
        }
        if (name.Length == 0) { host.SetStatus("模板名不合法"); return; }

        string rel = "lib/Saves/Worlds/_utpl_templates/" + name + UtplCodec.Extension;
        if (ctx.FileExists(rel) && _armedName != name)
        {
            _armedName = name;      // ★ 同名模板绝不静默覆盖
            host.SetStatus(name + UtplCodec.Extension + " 已存在 —— 再点一次「导出模板」就覆盖它");
            return;
        }

        if (_exportBtn is not null) { _exportBtn.IsEnabled = false; }
        host.SetStatus("导出中：" + name + " " + w + "×" + h + " …");
        try
        {
            string worldRel = view.WorldRelPath;
            int sx = sel.X, sy = sel.Y;
            var r = await Task.Run(() =>
            {
                string? worldPath = ResolveWorldFile(ctx, worldRel);
                if (worldPath is null) { return (Ok: false, Msg: "找不到世界文件 " + worldRel); }

                (World? world, string err) = WorldMapRenderer.LoadWorld(worldPath);
                if (world is null) { return (Ok: false, Msg: "读世界失败：" + err); }

                WorldCropper.CropResult crop = WorldCropper.Crop(world, sx, sy, w, h);
                string outFile = Path.Combine(ctx.DataDir, name + UtplCodec.Extension);
                UtplCodec.Write(outFile, crop.Region);
                if (!ctx.Upload(outFile, rel)) { return (Ok: false, Msg: "上传失败：" + rel); }

                long size = 0; try { size = new FileInfo(outFile).Length; } catch { }
                return (Ok: true,
                        Msg: $"{crop.Region.W}×{crop.Region.H} · 箱 {crop.Region.Chests.Count} · 牌 {crop.Region.Signs.Count}"
                           + $" · 实体 {crop.Region.Entities.Count}{crop.Warning} · {size} 字节");
            });

            if (r.Ok) { _armedName = ""; RefreshTemplateList(); }
            host.SetStatus(r.Ok ? $"已导出 {name}{UtplCodec.Extension} → _utpl_templates · {r.Msg}" : r.Msg);
            ctx.Log("[建筑模板] " + (r.Ok ? "导出成功 " : "导出失败 ") + name + " · " + r.Msg);
        }
        catch (Exception ex)
        {
            host.SetStatus("导出失败：" + ex.Message);
            ctx.Log("[建筑模板] 导出异常: " + ex.Message);
        }
        finally
        {
            UpdateEnabled();
        }
    }

    /// <summary>拿世界文件的本地可读路径（本机直读；远端下载到插件数据目录 —— 不用 %TEMP%，它可能不可写）。</summary>
    private static string? ResolveWorldFile(IManagerContext ctx, string worldRel)
    {
        // ★ worldRel 是相对【世界目录】的（lib/Saves/Worlds），而 ctx.FilesRoot 是部署根。
        //   原来直接 Path.Combine(ctx.FilesRoot, worldRel) → 少了一层，本机和远端都"找不到世界文件"。
        try
        {
            string localRel = $"lib{Path.DirectorySeparatorChar}Saves{Path.DirectorySeparatorChar}Worlds{Path.DirectorySeparatorChar}";
            string full = Path.Combine(ctx.FilesRoot, localRel + worldRel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full)) { return full; }
            ctx.Log("[建筑模板] 本地世界路径不存在: " + full);
        }
        catch (Exception ex) { ctx.Log("[建筑模板] 本地路径异常: " + ex.Message); }

        // 远端：下载时给"相对部署根"的路径
        string staging = Path.Combine(ctx.DataDir, "worlds", Path.GetFileName(worldRel));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
            string relRoot = "lib/Saves/Worlds/" + worldRel.Replace('\\', '/');
            ctx.Log("[建筑模板] 远端下载世界: " + relRoot);
            if (ctx.Download(relRoot, staging) || File.Exists(staging)) { return staging; }
            ctx.Log("[建筑模板] 远端下载失败: " + relRoot);
        }
        catch (Exception ex) { ctx.Log("[建筑模板] 远端下载异常: " + ex.Message); }
        return null;
    }
    // ==================== 插件自带的说明页（诊断用，不放操作提示） ====================

    public Control? CreateView(IManagerContext ctx)
    {
        var sb = new StringBuilder();
        try
        {
            TemplateCapability cap = Detect(ctx);
            sb.AppendLine($"服务端能力：核心模块 {Yes(cap.CoreModule)} · 消费端玩法 {Yes(cap.Consumer)} · 已有模板 {Yes(cap.AnyTemplates)}");
            sb.AppendLine(cap.Supported ? "→ 地图页上已挂：框选 / 导出模板 / 区块模板浏览" : "→ 服务端没有这套能力，地图页上不会出现相关按钮");
            sb.AppendLine();
            List<string> list = ListTemplates(ctx);
            sb.AppendLine($"区块模板目录（lib\\Saves\\Worlds\\_utpl_templates）：{list.Count} 个");
            foreach (string n in list) { sb.AppendLine("  · " + n + UtplCodec.Extension); }
            sb.AppendLine();
            sb.AppendLine("插件数据目录：" + ctx.DataDir);
        }
        catch (Exception ex) { sb.AppendLine("探测失败：" + ex.Message); }

        return new ScrollViewer
        {
            Padding = new Thickness(12),
            Content = new TextBlock
            {
                Text = sb.ToString(),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.Parse("#C9C9CF")),
            },
        };
    }

    private static string Yes(bool b) => b ? "有" : "无";
}
