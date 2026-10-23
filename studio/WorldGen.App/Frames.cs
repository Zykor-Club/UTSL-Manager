using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace WorldGen;

/// <summary>
/// 实时帧：worker 每 2 秒把当前世界画成 BMP（用**游戏自己的地图配色** MapHelper.colorLookup），
/// 这里只负责读文件显示 —— 不再做任何上色/地平线计算（之前那些"丛林飘在天上"就是自己上色搞出来的）。
/// </summary>
public partial class MainWindow
{
    private DispatcherTimer? _frameTimer;
    private DateTime _lastStamp = DateTime.MinValue;
    private int _framesSeen;
    private Bitmap? _liveBitmap;
    private volatile bool _busy;

    private void InitFrames()
    {
        _frameTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _frameTimer.Tick += (_, _) => PollFrame();
        _frameTimer.Start();
    }

    private void PollFrame()
    {
        if (_cts is null || _busy || StudioEngine.WorkDir.Length == 0) { return; }

        string path = Path.Combine(StudioEngine.WorkDir, "live", "frame.bmp");
        if (!File.Exists(path)) { return; }
        var stamp = File.GetLastWriteTimeUtc(path);
        if (stamp == _lastStamp) { return; }
        _lastStamp = stamp;

        _busy = true;
        _ = Task.Run(() =>
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch { _busy = false; return; }

            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    _liveBitmap = new Bitmap(new MemoryStream(bytes));
                    PreviewImage.Source = _liveBitmap;
                    PreviewImage.IsVisible = true;
                    PreviewHint.IsVisible = false;
                    _framesSeen++;
                    TxtNote.Text = $"实时帧 {_liveBitmap.PixelSize.Width}×{_liveBitmap.PixelSize.Height} · 第 {_framesSeen} 帧 · 游戏地图配色 · 每 2 s";
                    if (_framesSeen == 1) { Log($"[帧] 收到第一帧 {_liveBitmap.PixelSize.Width}×{_liveBitmap.PixelSize.Height}"); }
                }
                catch (Exception ex) { Log("[帧] 显示失败: " + ex.Message); }
                finally { _busy = false; }
            });
        });
    }
}
