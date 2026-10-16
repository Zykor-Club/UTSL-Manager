using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

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

    /// <summary>编辑器背景（深藏青；用户明确要求"藏青色或深蓝色"）。</summary>
    private const string EditorBgHex = "#101B2E";

    /// <summary>
    /// 统一给高亮定义调色（内置 xshd 是按【浅色背景】写的，直接用在深色背景上会"标点看不见、红绿刺眼"）。
    /// 规则：① 低饱和度（多半是标点/分隔符）→ 换成柔和蓝灰 #8FA3BF，保证可见；
    ///       ② 其余颜色若相对亮度低于 0.55 → 按比例提亮（保留色相），把"太重的深红/深绿"变柔和。
    /// 这样 C#/XML/JS/PowerShell 也一起受益，不必逐个语言硬编码。
    /// </summary>
    private static void TuneHighlighting(IHighlightingDefinition def)
    {
        try
        {
            foreach (var c in def.NamedHighlightingColors)
            {
                if (c.Foreground is not SimpleHighlightingBrush sh) { continue; }
                // 该端口的 GetColor 需要 ITextRunConstructionContext；SimpleHighlightingBrush 会忽略它（此处只为拿颜色），
                // 且返回可空 Color —— 取不到就跳过这一类。
                Color col;
                try { Color? c0 = sh.GetColor(null!); if (c0 is null) { continue; } col = c0.Value; } catch { continue; }
                Color tuned = TuneColor(col);
                if (tuned != col) { c.Foreground = new SimpleHighlightingBrush(tuned); }
            }
        }
        catch { }   // 定义若被冻结（不可改）就保持原样，不影响功能
    }

    private static Color TuneColor(Color c)
    {
        int mx = Math.Max(c.R, Math.Max(c.G, c.B));
        int mn = Math.Min(c.R, Math.Min(c.G, c.B));
        // ① 近灰色 → 标点类，直接给蓝灰
        if (mx - mn <= 26) { return Color.FromArgb(c.A, 0x8F, 0xA3, 0xBF); }
        // ② 太暗 → 提亮到可读区间
        double lum = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
        if (lum >= 0.55) { return c; }
        double k = 0.55 / Math.Max(0.06, lum);
        byte Up(byte v) => (byte)Math.Clamp(v * k, 0, 255);
        return Color.FromArgb(c.A, Up(c.R), Up(c.G), Up(c.B));
    }

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
            // ★ 编辑器配色（用户反馈：内置高亮按浅色背景配色，深色下标点几乎看不见、数字红绿太刺眼）。
            //   背景改成深藏青；正文字色用柔和的蓝灰；行号用更暗一档的蓝灰。
            Foreground = new SolidColorBrush(Color.Parse("#C7D2E0")),
            Background = new SolidColorBrush(Color.Parse(EditorBgHex)),
            WordWrap = false,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        ed.TextChanged += (_, _) =>
        {
            if (editorSaving) { return; }
            if (!editorDirty) { editorDirty = true; UpdateEditorStatus(); }
            ScheduleLint();          // ★ P3：输入停下 400ms 后跑一次语法校验（性能：不能每敲一下就解析）
        };
        // ★ P3：波浪线渲染器（AvaloniaEdit 的 IBackgroundRenderer，画在文本层之上）
        try { ed.TextArea.TextView.BackgroundRenderers.Add(new SquiggleRenderer(this)); } catch { }
        // Ctrl+S 保存；Ctrl+F 打开查找面板
        ed.AddHandler(KeyDownEvent, async (s, e) =>
        {
            if (e.Key == Key.S && (e.KeyModifiers & KeyModifiers.Control) != 0)
            {
                e.Handled = true;
                await SaveEditorAsync();
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // 行号颜色（深色下才看得见但又不抢）
        try { ed.LineNumbersForeground = new SolidColorBrush(Color.Parse("#54688A")); } catch { }
        // 文本渲染：深色背景上用灰度抗锯齿比 ClearType 更干净（避免彩边/发虚）
        try { Avalonia.Media.RenderOptions.SetTextRenderingMode(ed, Avalonia.Media.TextRenderingMode.Antialias); } catch { }
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

            try
            {
                var def = HighlightFor(name);
                if (def is not null) { TuneHighlighting(def); }
                ed.SyntaxHighlighting = def;
            }
            catch { }
            EditTitle.Text = "编辑 · " + name;
            EditPathText.Text = (FileBackend.IsRemote ? "远端 " : "") + full;
            EditHint.Text = "";
            ShowView("edit");
            ed.Focus();
            UpdateEditorStatus();
            ScheduleLint();          // 打开即校验一次
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
        string lint = EditStatusLintText.Length > 0 ? " · " + EditStatusLintText : "";
        EditStatus.Text = $"{dirty} · {enc} · {editor.Document.LineCount} 行 · {editor.Text.Length} 字符 · {(editorSaving ? "保存中…" : (editorDirty ? "Ctrl+S 保存" : "—"))}{lint}";
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

    // ===================== P3：语法校验 + 红色波浪线 =====================
    //  设计取舍：
    //   · **防抖 400ms + 后台线程 + 可取消**：绝不每敲一下都解析（性能优先）；结果过期（又敲过键）就直接丢弃。
    //   · JSON 用 System.Text.Json（异常自带"行 + 该行字节列"，实测 flsae 会报 Expected the literal 'false'）；
    //     C# 用工程里本来就有的 Roslyn（只取 Error 级；语义错如"未定义变量"抓不到 —— 那需要完整编译引用）。
    //   · 波浪线用 AvaloniaEdit 的经典做法：IBackgroundRenderer + BackgroundGeometryBuilder 画折线。

    private sealed class EditorDiag
    {
        public int Offset; public int Length; public string Message = ""; public int Line; public int Col;
    }

    private readonly List<EditorDiag> editorDiags = [];
    private readonly object editorDiagLock = new();
    private DispatcherTimer? lintTimer;
    private CancellationTokenSource? lintCts;
    private int editorLintVersion;
    private string EditStatusLintText = "";

    /// <summary>输入停止 400ms 后校验一次。</summary>
    private void ScheduleLint()
    {
        if (editor is null) { return; }
        if (lintTimer is null)
        {
            lintTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            lintTimer.Tick += async (_, _) => { lintTimer!.Stop(); await LintNowAsync(); };
        }
        lintTimer.Stop();
        lintTimer.Start();
    }

    /// <summary>跑一次校验：后台解析，UI 只收结果。</summary>
    private async Task LintNowAsync()
    {
        var ed = editor;
        if (ed is null) { return; }
        string text = ed.Text ?? "";
        string name = editorName;
        int myVersion = ++editorLintVersion;
        try { lintCts?.Cancel(); } catch { }
        var cts = new CancellationTokenSource();
        lintCts = cts;

        List<EditorDiag> diags;
        try { diags = await Task.Run(() => Lint(text, name), cts.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { EditStatusLintText = "校验异常: " + ex.Message; UpdateEditorStatus(); return; }
        if (myVersion != editorLintVersion || editor is null) { return; }   // 结果已过期

        lock (editorDiagLock)
        {
            editorDiags.Clear();
            editorDiags.AddRange(diags);
        }
        try { editor.TextArea.TextView.Redraw(); } catch { }

        EditStatusLintText = diags.Count == 0
            ? "✓ 语法检查通过"
            : $"⚠ {diags.Count} 个语法问题 · 第一个：{Compact(diags[0].Message)}（行 {diags[0].Line}，列 {diags[0].Col}）";
        UpdateEditorStatus();
    }

    /// <summary>状态栏里只放一行：把多行消息压平。</summary>
    private static string Compact(string s)
    {
        string t = s.Replace("\r", " ").Replace("\n", " ");
        while (t.Contains("  ")) { t = t.Replace("  ", " "); }
        return t.Length > 120 ? t[..120] + "…" : t;
    }

    /// <summary>真正的解析（后台线程调用，不碰 UI）。</summary>
    private static List<EditorDiag> Lint(string text, string name)
    {
        var list = new List<EditorDiag>();
        if (text.Length == 0) { return list; }
        string ext = ExtOf(name);
        try
        {
            if (ext == ".json")
            {
                try { using var _ = JsonDocument.Parse(text); }
                catch (JsonException jx)
                {
                    // ★ 实测（探针）：System.Text.Json 给的位置常在记号的中间/末尾 ——
                    //   例如 {"a": flsae} 它把 f 当成字面量开头、报错位置落在 f 之后的 l 上，
                    //   直接用它画波浪线会只标出 [lsae]。所以以该位置为中心向两侧扩成完整记号。
                    int off = OffsetFromLineByte(text, jx.LineNumber, jx.BytePositionInLine);
                    if (off < 0 || off > text.Length) { off = 0; }
                    (int ts, int tl) = TokenSpanAt(text, off);
                    (int tl2, int tc2) = LineColAt(text, ts);
                    list.Add(new EditorDiag
                    {
                        Offset = ts,
                        Length = tl,
                        Message = jx.Message,
                        Line = tl2,
                        Col = tc2,
                    });
                }
            }
            else if (ext == ".cs" || ext == ".csx")
            {
                var tree = CSharpSyntaxTree.ParseText(text);
                foreach (var d in tree.GetDiagnostics())
                {
                    if (d.Severity != DiagnosticSeverity.Error) { continue; }
                    var span = d.Location.SourceSpan;
                    var pos = d.Location.GetLineSpan().StartLinePosition;
                    list.Add(new EditorDiag
                    {
                        Offset = span.Start,
                        Length = Math.Max(1, span.Length),
                        Message = d.GetMessage(),
                        Line = pos.Line + 1,
                        Col = pos.Character + 1,
                    });
                    if (list.Count >= 50) { break; }
                }
            }
        }
        catch { }
        return list;
    }

    /// <summary>JsonException 给的是"行(0基) + 该行字节列(0基)"，换算成文档字符偏移。</summary>
    private static int OffsetFromLineByte(string text, long? line, long? byteCol)
    {
        long l = line ?? 0, b = byteCol ?? 0;
        int idx = 0; long cur = 0;
        while (cur < l && idx < text.Length) { if (text[idx] == '\n') { cur++; } idx++; }
        long bytes = 0;
        while (idx < text.Length && bytes < b)
        {
            if (text[idx] == '\n' || text[idx] == '\r') { break; }
            bytes += Encoding.UTF8.GetByteCount(text[idx].ToString());
            idx++;
        }
        return idx;
    }

    /// <summary>把偏移扩成一个完整"记号"的区间（向两侧扩；找不到记号就取 1 个字符）。</summary>
    private static (int Start, int Length) TokenSpanAt(string text, int off)
    {
        static bool IsTok(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == '$' || c == '"';
        int i = Math.Clamp(off, 0, text.Length);
        int s = i, e = i;
        while (s > 0 && IsTok(text[s - 1])) { s--; }
        while (e < text.Length && IsTok(text[e])) { e++; }
        if (e <= s)
        {
            s = Math.Clamp(i, 0, Math.Max(0, text.Length - 1));
            e = Math.Min(text.Length, s + 1);
        }
        return (s, Math.Max(1, e - s));
    }

    /// <summary>偏移 → 1 基的行/列（状态栏显示、以及扩边后重算起点用）。</summary>
    private static (int Line, int Col) LineColAt(string text, int off)
    {
        int line = 1, col = 1;
        for (int i = 0; i < off && i < text.Length; i++)
        {
            if (text[i] == '\n') { line++; col = 1; } else { col++; }
        }
        return (line, col);
    }

    /// <summary>红色波浪线渲染器（AvaloniaEdit 的 IBackgroundRenderer）。</summary>
    private sealed class SquiggleRenderer(MainWindow owner) : AvaloniaEdit.Rendering.IBackgroundRenderer
    {
        public AvaloniaEdit.Rendering.KnownLayer Layer => AvaloniaEdit.Rendering.KnownLayer.Selection;

        public void Draw(AvaloniaEdit.Rendering.TextView textView, DrawingContext dc)
        {
            if (!textView.VisualLinesValid) { return; }
            List<EditorDiag> snapshot;
            lock (owner.editorDiagLock)
            {
                if (owner.editorDiags.Count == 0) { return; }
                snapshot = [.. owner.editorDiags];
            }
            var pen = new Pen(new SolidColorBrush(Color.Parse("#FF4D4F")), 1.4);
            foreach (var line in textView.VisualLines)
            {
                int lineStart = line.FirstDocumentLine.Offset;
                int lineEnd = line.LastDocumentLine.EndOffset;
                foreach (var d in snapshot)
                {
                    int s = Math.Max(d.Offset, lineStart);
                    int e = Math.Min(d.Offset + Math.Max(1, d.Length), lineEnd);
                    if (e <= s) { continue; }
                    var seg = new AvaloniaEdit.Document.TextSegment { StartOffset = s, Length = e - s };
                    foreach (var r in AvaloniaEdit.Rendering.BackgroundGeometryBuilder.GetRectsForSegment(textView, seg))
                    {
                        double y = r.Bottom - 1.0;
                        var geo = new StreamGeometry();
                        using (var g = geo.Open())
                        {
                            bool up = true;
                            double x = r.Left;
                            g.BeginFigure(new Point(x, y), false);
                            while (x < r.Right)
                            {
                                x = Math.Min(x + 2.0, r.Right);
                                up = !up;
                                g.LineTo(new Point(x, y + (up ? 0 : -2.5)));
                            }
                            g.EndFigure(false);
                        }
                        dc.DrawGeometry(null, pen, geo);
                    }
                }
            }
        }
    }
}

