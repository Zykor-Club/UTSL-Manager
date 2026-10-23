using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Collections;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Path = System.IO.Path;
using Avalonia.Threading;

namespace WorldGen;

public partial class MainWindow : Window
{
    // 与现网 EdgeStitch / UTSLStitchTool 一致的几何：片宽 4200、重叠 100/侧 → Δ=3900、母图 12000
    private const int MotherWidth = 12000;
    private const int MotherHeight = 2400;
    private const int Delta = 3900;
    private const int SliceWidth = 4200;

    private readonly string[] _chainLabels = { "西", "中（默认）", "东" };
    private Bitmap? _preview;
    private string _previewPath = "";

    public MainWindow()
    {
        InitializeComponent();

        // 预览图：--preview <png> 或默认指向上一步渲好的真实母图
        string[] args = Environment.GetCommandLineArgs();
        int pi = Array.IndexOf(args, "--preview");
        _previewPath = pi >= 0 && pi + 1 < args.Length
            ? args[pi + 1]
            : @"C:\Users\星梦\Desktop\01_UTSL平台\utsl-verify\out\gen2\preview.png";

        LoadPreview();
        ChkSeam.IsCheckedChanged += (_, _) => DrawSeams();
        BtnRefresh.Click += (_, _) => LoadPreview();
        BtnOpenOut.Click += (_, _) => OpenOutDir();
        PreviewHost.SizeChanged += (_, _) => DrawSeams();
        BtnStart.Click += (_, _) => Log("[提示] 生成引擎尚未接入 —— 这一版只验证外观与预览布局。");

        Log($"[几何] 母图 {MotherWidth}×{MotherHeight} · 片宽 {SliceWidth} · Δ{Delta} · 重叠 300/缝");
        Log("[就绪] 预览图来自 utsl-verify\\out\\gen2\\preview.png（真实 12000×2400 母图渲染，非示意）");
    }

    private void LoadPreview()
    {
        try
        {
            if (!File.Exists(_previewPath))
            {
                PreviewImage.IsVisible = false;
                PreviewHint.IsVisible = true;
                Log("[预览] 找不到 " + _previewPath);
                return;
            }
            using var fs = File.OpenRead(_previewPath);
            _preview = new Bitmap(fs);
            PreviewImage.Source = _preview;
            PreviewImage.IsVisible = true;
            PreviewHint.IsVisible = false;
            TxtNote.Text = $"实际图格色块预览（非客户端贴图）· 源 {_preview.PixelSize.Width}×{_preview.PixelSize.Height} · 接缝 2 道";
            // 布局稳定后再画（ctor 里 Bounds 还是 0）
            Dispatcher.UIThread.Post(DrawSeams, DispatcherPriority.Loaded);
        }
        catch (Exception ex)
        {
            Log("[预览] 加载失败: " + ex.Message);
        }
    }

    /// <summary>在预览图上画两条接缝（Δ3900 / 7800 处）+ 三段标签。</summary>
    private bool _drawing;
    private void DrawSeams()
    {
        if (_drawing) { return; }          // 防止"改 Overlay → 触发布局 → 再进来"的自激
        _drawing = true;
        try { DrawSeamsCore(); } finally { _drawing = false; }
    }

    private void DrawSeamsCore()
    {
        Overlay.Children.Clear();
        if (_preview is null || ChkSeam.IsChecked != true) { return; }

        double hostW = PreviewHost.Bounds.Width, hostH = PreviewHost.Bounds.Height;
        if (hostW < 10 || hostH < 10) { return; }

        // Image 用 Stretch=Uniform：先算出实际绘制矩形，再按母图坐标映射
        double imgW = _preview.PixelSize.Width, imgH = _preview.PixelSize.Height;
        double k = Math.Min(hostW / imgW, hostH / imgH);
        double drawW = imgW * k, drawH = imgH * k;
        double ox = (hostW - drawW) / 2, oy = (hostH - drawH) / 2;

        // 接缝在母图上的列号：Delta, 2*Delta
        for (int i = 1; i < 3; i++)
        {
            double x = ox + (Delta * i / (double)MotherWidth) * drawW;
            Overlay.Children.Add(new Line
            {
                StartPoint = new Point(x, oy),
                EndPoint = new Point(x, oy + drawH),
                Stroke = new SolidColorBrush(Color.Parse("#FF0A84FF")),
                StrokeThickness = 1.5,
                StrokeDashArray = new AvaloniaList<double> { 5, 4 },
            });
        }

        // 三段标签（西 / 中 / 东），贴在每段顶部
        for (int i = 0; i < 3; i++)
        {
            double cx = ox + ((Delta * i + SliceWidth / 2.0) / MotherWidth) * drawW;
            var tb = new TextBlock
            {
                Text = $"{i + 1} · {_chainLabels[i]}",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#E6FFFFFF")),
            };
            Canvas.SetLeft(tb, cx - 26);
            Canvas.SetTop(tb, Math.Max(2, oy - 20));
            Overlay.Children.Add(tb);
        }
    }

    private void OpenOutDir()
    {
        try
        {
            string dir = Path.GetDirectoryName(_previewPath) ?? "";
            if (Directory.Exists(dir)) { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true }); }
        }
        catch (Exception ex) { Log("[打开输出] 失败: " + ex.Message); }
    }

    private int _logLines;
    private void Log(string line)
    {
        _logLines++;
        string prev = TxtLog.Text ?? "";
        TxtLog.Text = (prev.Length > 0 ? prev + "\n" : "") + $"[{DateTime.Now:HH:mm:ss}] {line}";
        if (_logLines > 200) { _logLines = 0; }
        Dispatcher.UIThread.Post(() => { }, DispatcherPriority.Background);
    }
}
