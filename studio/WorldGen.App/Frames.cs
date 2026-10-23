using System.Text.Json;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace WorldGen;

/// <summary>
/// 实时帧：轮询引擎写出的 live\frame.bin，用 TEdit 的图格配色画成位图贴到预览区。
///
/// ★ 两个踩过的坑，别再改回去：
///   1) 不要在后台线程调 Bitmap.Save()（SkiaSharp 的 PNG 编码器在非 UI 线程会 0xC0000005 直接崩进程，
///      WER 里能看到 sk_pngencoder_encode）。这里改成**纯托管编码 BMP**，Skia 只负责解码。
///   2) 地平线用"墙"定位（墙只在地下），取 10% 分位。否则丛林草/树会把地表顶到第二行，
///      整张图被误判成地下 —— 现象就是"天空没了、丛林飘在天上"。
/// </summary>
public partial class MainWindow
{
    private Dictionary<int, Color>? _palette;
    private Color _sky = Color.Parse("#4A90C4");
    private Color _wall = Color.Parse("#383039");
    private Color _dirt = Color.Parse("#5A4632");
    private Color _rock = Color.Parse("#3A3A40");
    private Color _fallback = Color.Parse("#768879");
    private Color _water = Color.Parse("#3C8BC5");
    private Color _lava = Color.Parse("#F26839");
    private Color _honey = Color.Parse("#D9A739");
    private Color _shimmer = Color.Parse("#BA83E5");
    private DispatcherTimer? _frameTimer;
    private DateTime _lastFrameStamp = DateTime.MinValue;
    private int _framesSeen;
    private Bitmap? _liveBitmap;
    private int _engineSurface;
    private int _engineRock;
    private volatile bool _frameBusy;

    private void InitFrames()
    {
        LoadPalette();
        _frameTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _frameTimer.Tick += (_, _) => PollFrame();
        _frameTimer.Start();
    }

    private void LoadPalette()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "tile-palette.json");
            if (!File.Exists(path)) { Log("[帧] 找不到配色表 " + path); return; }
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            // 天空/土/岩/墙 用 Frames.cs 里的常量（社区那套深色系），不被静态全景的配色表覆盖
            if (root.TryGetProperty("wall", out var w)) { _wall = Color.Parse(w.GetString()!); }
            _palette = new Dictionary<int, Color>(1024);
            if (root.TryGetProperty("tiles", out var tiles))
            {
                foreach (var p in tiles.EnumerateObject())
                {
                    if (int.TryParse(p.Name, out int type)) { _palette[type] = Color.Parse(p.Value.GetString()!); }
                }
            }
            Log($"[帧] 配色表已载入：{_palette.Count} 个图格色");
        }
        catch (Exception ex) { Log("[帧] 配色表加载失败: " + ex.Message); }
    }

    private void PollFrame()
    {
        if (_cts is null || _frameBusy) { return; }        // 只在生成期间刷帧；上一帧没画完就跳过

        string path = Path.Combine(GenRoot, "live", "frame.bin");
        if (!File.Exists(path)) { return; }
        var stamp = File.GetLastWriteTimeUtc(path);
        if (stamp == _lastFrameStamp) { return; }
        _lastFrameStamp = stamp;

        var frame = StudioEngine.ReadFrame(GenRoot);
        if (frame is null) { return; }
        ReadHorizonFromProgress();

        var (w, h, px) = frame.Value;
        _frameBusy = true;
        _ = Task.Run(() =>
        {
            try
            {
                byte[] bmp = EncodeBmp(w, h, px);
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        _liveBitmap = new Bitmap(new MemoryStream(bmp));
                        PreviewImage.Source = _liveBitmap;
                        PreviewImage.IsVisible = true;
                        PreviewHint.IsVisible = false;
                        _framesSeen++;
                        TxtNote.Text = $"实时帧 {w}×{h} · 第 {_framesSeen} 帧 · 采样间隔 2 s（图格色来自 TEdit，与最终渲染同源）";
                        if (_framesSeen == 1) { Log($"[帧] 收到第一帧 {w}×{h}"); }
                    }
                    catch (Exception ex) { Log("[帧] 显示失败: " + ex.Message); }
                    finally { _frameBusy = false; }
                });
            }
            catch (Exception ex)
            {
                _frameBusy = false;
                Dispatcher.UIThread.Post(() => Log("[帧] 编码失败: " + ex.Message));
            }
        });
    }

    /// <summary>从 progress.json 读引擎算出的 worldSurface / rockLayer（比按图格猜准得多）。</summary>
    private void ReadHorizonFromProgress()
    {
        try
        {
            string p = Path.Combine(GenRoot, "live", "progress.json");
            if (!File.Exists(p)) { return; }
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            if (doc.RootElement.TryGetProperty("surface", out var s) && s.GetInt32() > 0) { _engineSurface = s.GetInt32(); }
            if (doc.RootElement.TryGetProperty("rock", out var r) && r.GetInt32() > 0) { _engineRock = r.GetInt32(); }
        }
        catch { }
    }
    /// <summary>纯托管 32 位 BMP（bottom-up BGRA + 4 字节对齐）。不调用 Skia 编码器，后台线程可安全使用。</summary>
    private byte[] EncodeBmp(int w, int h, ushort[] px)
    {
        // 地平线：每列首个"实心图格"（不算墙/液体）的 15% 分位。
        // 实测这个世界的分位：10%→11.2% 高度、20%→17.1%，取 15% 落在山脚/树线之间，
        // 既不会被丛林树顶到天上，也不会像 worldSurface(587=24.5%) 那样把天空涂到真实地形上。
        var firstSolid = new int[w];
        for (int x = 0; x < w; x++)
        {
            firstSolid[x] = h;
            for (int y = 0; y < h; y++)
            {
                ushort vv = px[y * w + x];
                if (vv != 0 && (vv & 0xC000) == 0) { firstSolid[x] = y; break; }
            }
        }
        var solids = firstSolid.Where(v => v < h).OrderBy(v => v).ToArray();
        int surface = solids.Length > 0 ? solids[(int)(solids.Length * 0.15)] : (int)(h * 0.17);
        int rockLine = _engineRock > 0 ? Math.Clamp(_engineRock * h / 2400, surface + 1, h - 1)
                                       : Math.Min(h - 1, (int)(h * 0.45));

        int rowBytes = w * 4;
        int pad = (4 - rowBytes % 4) % 4;
        int stride = rowBytes + pad;
        int dataSize = stride * h;
        var buf = new byte[54 + dataSize];

        buf[0] = (byte)'B'; buf[1] = (byte)'M';
        BitConverter.GetBytes(54 + dataSize).CopyTo(buf, 2);
        BitConverter.GetBytes(54).CopyTo(buf, 10);          // 像素数据偏移
        BitConverter.GetBytes(40).CopyTo(buf, 14);          // BITMAPINFOHEADER
        BitConverter.GetBytes(w).CopyTo(buf, 18);
        BitConverter.GetBytes(h).CopyTo(buf, 22);
        BitConverter.GetBytes((short)1).CopyTo(buf, 26);    // planes
        BitConverter.GetBytes((short)32).CopyTo(buf, 28);   // bpp
        BitConverter.GetBytes(dataSize).CopyTo(buf, 34);

        for (int y = 0; y < h; y++)
        {
            int srcRow = y * w;
            int dstRow = 54 + (h - 1 - y) * stride;         // BMP 是自底向上
            for (int x = 0; x < w; x++)
            {
                ushort v = px[srcRow + x];
                Color c;
                if ((v & 0x4000) != 0)
                {
                    c = (v & 0x0F) switch { 1 => _lava, 2 => _honey, 3 => _shimmer, _ => _water };
                }
                else if (v == 0)
                {
                    c = y < surface ? _sky : (y < rockLine ? _dirt : _rock);
                }
                else if ((v & 0x8000) != 0) { c = _wall; }
                else if (_palette is not null && _palette.TryGetValue(v, out var pc)) { c = pc; }
                else { c = _fallback; }

                int o = dstRow + x * 4;
                buf[o] = c.B; buf[o + 1] = c.G; buf[o + 2] = c.R; buf[o + 3] = 255;
            }
        }
        return buf;
    }
}
