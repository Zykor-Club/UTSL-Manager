using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;

namespace UTSLManager;

/// <summary>
/// 文件编辑器（P2）：类 MCSManager —— 一次编辑一个文件，点保存写回。
///
/// 设计要点（都是踩过坑之后的决定）：
///  1) **不加载大文件**：超过 MaxEditorBytes 直接拒绝并说明原因（列表里已有 size，不必先读一遍）。
///  2) **二进制拒绝**：读回来做嗅探（NUL 字节 / 大量替换字符 U+FFFD），拒绝而不是把二进制编坏。
///  3) **保留 BOM 与行尾**：读写都不许悄悄改变文件格式 —— 本工程自己就被 BOM 坑过（PowerShell 脚本必须带 BOM）。
///     · IFileBackend.WriteText 是 UTF-8 无 BOM；所以"原来有 BOM"时在文本开头补 U+FEFF（它编码出来就是 EF BB BF）。
///     · AvaloniaEdit 的文档内部统一用 \n，所以保存时按原文件的风格还原成 CRLF 或 LF。
///  4) **保存前 JSON 校验**：一个语法错的 TShock 配置能让服务端起不来，必须拦。
///  5) **外部修改检测**：打开时记 大小/修改时间，保存前重列目录比对，不一致先确认（两步确认，与本程序其它操作一致）。
///  6) **首次保存自动 .bak**：沿用服务端生态的命名习惯（xxx.json.20261010_195652.bak）。
///  7) **未保存不许切页**：由 ShowView 拦截（见 MainWindow.axaml.cs）。
/// </summary>
public partial class MainWindow
{
    private const long MaxEditorBytes = 2 * 1024 * 1024;   // 2MB：再大就不适合在线编辑（读过来也要等）

    private TextEditor? editor;
    private string editorPath = "";
    private string editorName = "";
    private string editorDir = "";
    private long editorOrigSize;
    private DateTime editorOrigMtime;
    private bool editorDirty;
    private bool editorLeaveOk;
    private bool editorHadBom;
    private string editorEol = "\r\n";
    private bool editorSaving;
    private bool editorBackedUp;
    private string editorLoadedText = "";

    /// <summary>可编辑的文本扩展名（其余一律当二进制/不支持，点击时给出明确原因）。</summary>
    private static readonly string[] TextExts =
    {
        ".json", ".txt", ".log", ".md", ".markdown", ".yml", ".yaml", ".ini", ".cfg", ".conf", ".config",
        ".xml", ".axaml", ".xaml", ".cs", ".csx", ".ps1", ".psm1", ".bat", ".cmd", ".sh",
        ".html", ".htm", ".css", ".js", ".mjs", ".ts", ".toml", ".sql", ".properties", ".env", ".csv",
    };

    private static string ExtOf(string name)
    {
        int i = name.LastIndexOf('.');
        return i < 0 ? "" : name[i..].ToLowerInvariant();
    }

    private static bool LooksTextByName(string name)
        => TextExts.Contains(ExtOf(name)) || ExtOf(name).Length == 0;   // 无扩展名（脚本/配置很常见）也允许尝试

    /// <summary>按扩展名挑语法高亮（AvaloniaEdit 内置 21 种；Markdown/YAML 没有 → 纯文本）。</summary>
    private static IHighlightingDefinition? HighlightFor(string name)
    {
        string ext = ExtOf(name);
        string? def = ext switch
        {
            ".json" => "Json",
            ".cs" or ".csx" => "C#",
            ".xml" or ".axaml" or ".xaml" or ".config" or ".csproj" or ".props" or ".targets" => "XML",
            ".js" or ".mjs" or ".ts" => "JavaScript",
            ".ps1" or ".psm1" => "PowerShell",
            ".php" => "PHP",
            ".html" or ".htm" => "HTML",
            _ => null,
        };
        if (def is null) { return null; }
        try { return HighlightingManager.Instance.GetDefinition(def); } catch { return null; }
    }

    /// <summary>编辑器控件懒创建（只建一次，之后复用）。</summary>
    private TextEditor EnsureEditor()
    {
        if (editor is not null) { return editor; }
        var ed = new TextEditor
        {
            ShowLineNumbers = true,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei"),
            FontSize = 12.5,
            WordWrap = false,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        ed.TextChanged += (_, _) =>
        {
            if (editorSaving) { return; }
            if (!editorDirty) { editorDirty = true; UpdateEditorStatus(); }
        };
        // Ctrl+S 保存；Ctrl+F 打开查找面板
        ed.AddHandler(KeyDownEvent, async (s, e) =>
        {
            if (e.Key == Key.S && (e.KeyModifiers & KeyModifiers.Control) != 0)
            {
                e.Handled = true;
                await SaveEditorAsync();
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        try { AvaloniaEdit.Search.SearchPanel.Install(ed); } catch { }
        EditHost.Children.Add(ed);
        editor = ed;
        return ed;
    }

    /// <summary>从文件页点一个文件进来（full=绝对路径，size/name 来自列表，避免再查一次）。</summary>
    private async void OpenInEditor(string full, long size, string name)
    {
        try
        {
            if (size > MaxEditorBytes)
            {
                ERR($"{name} 有 {size / 1024.0 / 1024.0:F1} MB，超过在线编辑上限 2MB —— 请用「下载」在本地编辑后上传。");
                return;
            }
            if (!LooksTextByName(name))
            {
                ERR($"{name} 的扩展名不在可编辑文本列表里（{ExtOf(name)}）。如果你确认它是文本，请改个已知扩展名或下载后本地编辑。");
                return;
            }

            string text;
            try { text = await Task.Run(() => FsReadText(full)); }
            catch (Exception ex) { ERR("读取失败: " + ex.Message); return; }

            // ★ 二进制嗅探：NUL 字节、或大量无法解码的替换字符 U+FFFD（UTF-8 解码失败会产生它）
            if (text.Contains('\0') || text.Count(c => c == '\uFFFD') > Math.Max(8, text.Length / 100))
            {
                ERR($"{name} 看起来是二进制文件（含 NUL 或大量无法解码的字节），不能在线编辑。");
                return;
            }

            bool hadBom = text.Length > 0 && text[0] == '\uFEFF';
            string body = hadBom ? text[1..] : text;

            var ed = EnsureEditor();
            editorSaving = true;
            ed.Text = body;
            editorSaving = false;

            editorPath = full;
            editorName = name;
            editorDir = System.IO.Path.GetDirectoryName(full) ?? FsRoot;
            editorHadBom = hadBom;
            editorEol = DominantEol(text);
            editorDirty = false;
            editorLeaveOk = false;
            editorBackedUp = false;
            editorLoadedText = body;

            // 记录"打开时"的大小与修改时间（保存前比对，检测外部修改）
            editorOrigSize = size;
            editorOrigMtime = DateTime.MinValue;
            try
            {
                string dir = editorDir;
                var list = await Task.Run(() => FsList(dir));
                var e = list.FirstOrDefault(x => !x.IsDir && x.Name == name);
                if (e is not null) { editorOrigSize = e.Size; editorOrigMtime = e.Mtime; }
            }
            catch { }

            try { ed.SyntaxHighlighting = HighlightFor(name); } catch { }
            EditTitle.Text = "编辑 · " + name;
            EditPathText.Text = (FileBackend.IsRemote ? "远端 " : "") + full;
            EditHint.Text = "";
            ShowView("edit");
            ed.Focus();
            UpdateEditorStatus();
        }
        catch (Exception ex) { ERR("打开编辑器失败: " + ex.Message); }
    }

    /// <summary>文本里占主导的行尾风格（用于保存时还原）。</summary>
    private static string DominantEol(string text)
    {
        int crlf = 0, lf = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') { if (i > 0 && text[i - 1] == '\r') { crlf++; } else { lf++; } }
        }
        return crlf >= lf ? "\r\n" : "\n";
    }

    private void UpdateEditorStatus()
    {
        if (editor is null) { return; }
        string dirty = editorDirty ? "● 未保存" : "已保存";
        string enc = (editorHadBom ? "UTF-8 BOM" : "UTF-8") + " · " + (editorEol == "\r\n" ? "CRLF" : "LF");
        EditStatus.Text = $"{dirty} · {enc} · {editor.Document.LineCount} 行 · {editor.Text.Length} 字符 · {(editorSaving ? "保存中…" : (editorDirty ? "Ctrl+S 保存" : "—"))}";
        if (BtnEditSave is not null) { BtnEditSave.IsEnabled = !editorSaving; }
        if (EditTitle is not null && editorName.Length > 0) { EditTitle.Text = (editorDirty ? "● " : "") + "编辑 · " + editorName; }
    }

    private void OnEditBack(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ShowView("plugins");   // 会被未保存拦截（再点一次可放弃）
        if (PluginsView.IsVisible) { ShowFiles(); }
    }

    private async void OnEditReload(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (editor is null || editorPath.Length == 0) { return; }
        if (editorDirty)
        {
            ERR("有未保存的修改 —— 重新加载会丢弃它们。请先保存；确实要丢弃就再点一次「↻ 重新加载」。");
            if (!editorReloadArmed) { editorReloadArmed = true; return; }
        }
        editorReloadArmed = false;
        string full = editorPath, name = editorName;
        editorDirty = false;
        long size = editorOrigSize;
        try { string t = await Task.Run(() => FsReadText(full)); size = t.Length; } catch { }
        OpenInEditor(full, size, name);
    }
    private bool editorReloadArmed;

    private async void OnEditSave(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await SaveEditorAsync();

    /// <summary>保存：JSON 校验 → 外部修改检测 → 首次备份 → 写回（保留 BOM / 行尾）。</summary>
    private async Task SaveEditorAsync()
    {
        if (editor is null || editorPath.Length == 0 || editorSaving) { return; }
        string body = editor.Text ?? "";

        // 1) JSON 校验（.json 且非空）
        if (ExtOf(editorName) == ".json" && body.Trim().Length > 0)
        {
            try { using var _ = JsonDocument.Parse(body); }
            catch (JsonException jx)
            {
                ERR($"保存被拦下：{editorName} 不是合法 JSON —— {jx.Message}（行 {jx.LineNumber}，列 {jx.BytePositionInLine}）");
                EditHint.Text = $"⚠ JSON 语法错误：{jx.Message}（行 {jx.LineNumber + 1}，列 {jx.BytePositionInLine + 1}）—— 已阻止保存，避免服务端读取失败。";
                return;
            }
        }

        // 2) 外部修改检测
        try
        {
            string dir = editorDir, nm = editorName;
            var list = await Task.Run(() => FsList(dir));
            var cur = list.FirstOrDefault(x => !x.IsDir && x.Name == nm);
            bool changed = cur is null
                ? editorOrigMtime != DateTime.MinValue                    // 文件被删了
                : (cur.Size != editorOrigSize || (editorOrigMtime != DateTime.MinValue && cur.Mtime != editorOrigMtime));
            if (changed && !editorOverwriteArmed)
            {
                editorOverwriteArmed = true;
                ERR($"{editorName} 在你打开之后被外部修改过（大小或时间变了）。若确认要用编辑器里的内容覆盖，请再点一次「保存」。");
                EditHint.Text = "⚠ 这个文件被外部改过（服务端/别人/另一个工具）。确认要覆盖就再点一次「保存」。";
                return;
            }
        }
        catch { }
        editorOverwriteArmed = false;

        editorSaving = true;
        UpdateEditorStatus();
        EditHint.Text = "";
        try
        {
            // 3) 首次保存备份（沿用 xxx.json.yyyyMMdd_HHmmss.bak 的习惯；备份失败不阻止保存，但会提示）
            if (!editorBackedUp)
            {
                string bak = editorPath + "." + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".bak";
                bool okBak = await Task.Run(() => FsWriteText(bak, editorLoadedText));
                editorBackedUp = okBak;
                if (okBak) { ERR("已生成备份: " + System.IO.Path.GetFileName(bak)); }
                else { ERR("⚠ 备份失败（不影响本次保存），继续保存。"); }
            }

            // 4) 还原行尾 + BOM 后写回
            string body2 = editorEol == "\r\n"
                ? body.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n")
                : body.Replace("\r\n", "\n").Replace("\r", "\n");
            string payload = editorHadBom ? "\uFEFF" + body2 : body2;

            bool ok = await Task.Run(() => FsWriteText(editorPath, payload));
            if (ok)
            {
                editorDirty = false;
                editorLoadedText = body;
                editorOrigSize = payload.Length;
                editorOrigMtime = DateTime.Now;
                editorBackedUp = true;
                // 定点失效这个路径的缓存，否则文件页/快照还在用旧内容（写入后缓存必须失效）
                FsInvalidatePath(editorPath);
                ERR($"已保存 {editorName}（{payload.Length} 字符，{(editorHadBom ? "保留 BOM" : "无 BOM")}，{(editorEol == "\r\n" ? "CRLF" : "LF")}）");
                Term("→ 已保存 " + editorPath, "#7EE787");
            }
            else
            {
                ERR("保存失败：后端返回失败（远端权限/占用/链路？）—— 内容仍在编辑器里，未丢失。");
                EditHint.Text = "⚠ 保存失败 —— 你的修改还在这里，可稍后重试（或先「下载」备份）。";
            }
        }
        catch (Exception ex)
        {
            ERR("保存异常: " + ex.Message);
            EditHint.Text = "⚠ 保存异常：" + ex.Message;
        }
        finally
        {
            editorSaving = false;
            UpdateEditorStatus();
        }
    }
    private bool editorOverwriteArmed;
}
