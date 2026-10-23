using System.Text.Json;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace WorldGen;

/// <summary>
/// 实时帧：轮询引擎写出的 live\frame.bin，用 TEdit 的图格配色画成位图贴到预览区。
/// 生成期间每 1 s 试一次；没在生成就走原来的 PNG 预览。
/// </summary>
public partial class MainWindow
{
    private Dictionary<int, Color>? _palette;
    private Color _sky = Color.Parse("#74AEDA");
    private Color _wall = Color.Parse("#3E3432");
    private Color _fallback = Color.Parse("#4A4A52");
    private DispatcherTimer? _frameTimer;
    private DateTime _lastFrameStamp = DateTime.MinValue;
    private int _framesSeen;
    private Bitmap? _liveBitmap;

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
            if (root.TryGetProperty("sky", out var s)) { _sky = Color.Parse(s.GetString()!); }
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
        if (_cts is null) { return; }        // 只在生成期间刷帧

        var frame = StudioEngine.ReadFrame(GenRoot);
        if (frame is null) { return; }

        // 帧没更新就不重画（引擎每 2s 写一份）
        string path = Path.Combine(GenRoot, "live", "frame.bin");
        var stamp = File.GetLastWriteTimeUtc(path);
        if (stamp == _lastFrameStamp) { return; }
        _lastFrameStamp = stamp;

        var (w, h, px) = frame.Value;
        try
        {
            var bmp = new WriteableBitmap(new Avalonia.PixelSize(w, h), new Avalonia.Vector(96, 96),
                                          PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using (var fb = bmp.Lock())
            {
                unsafe
                {
                    var dst = (byte*)fb.Address;
                    int stride = fb.RowBytes;
                    for (int y = 0; y < h; y++)
                    {
                        byte* row = dst + y * stride;
                        for (int x = 0; x < w; x++)
                        {
                            ushort v = px[y * w + x];
                            Color c;
                            if (v == 0) { c = _sky; }
                            else if ((v & 0x8000) != 0) { c = _wall; }
                            else if (_palette is not null && _palette.TryGetValue(v, out var pc)) { c = pc; }
                            else { c = _fallback; }

                            byte* p = row + x * 4;
                            p[0] = c.B; p[1] = c.G; p[2] = c.R; p[3] = 255;
                        }
                    }
                }
            }

            // 先落一份 PNG 再加载回来 —— 这是已经在用的显示路径，稳（WriteableBitmap 直接贴图在本机不显示）
            string dbg = Path.Combine(AppContext.BaseDirectory, "live-frame.png");
            bmp.Save(dbg);
            using (var fs2 = File.OpenRead(dbg)) { _liveBitmap = new Bitmap(fs2); }
            PreviewImage.Source = _liveBitmap;
            PreviewImage.IsVisible = true;
            PreviewHint.IsVisible = false;
            _framesSeen++;
            TxtNote.Text = $"实时帧 {w}×{h} · 第 {_framesSeen} 帧 · 采样间隔 2 s（图格色来自 TEdit，与最终渲染同源）";
            if (_framesSeen == 1) { Log($"[帧] 收到第一帧 {w}×{h}"); }
        }
        catch (Exception ex) { Log("[帧] 绘制失败: " + ex.Message); }
    }
}
