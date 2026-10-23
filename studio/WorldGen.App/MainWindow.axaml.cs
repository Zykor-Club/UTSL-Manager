using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Path = System.IO.Path;

namespace WorldGen;

public partial class MainWindow : Window
{
    // 与现网 EdgeStitch / UTSLStitchTool 一致的几何：片宽 4200、重叠 100/侧 → Δ=3900、母图 12000
    private const int MotherWidth = 12000;
    private const int Delta = 3900;
    private const int SliceWidth = 4200;

    /// <summary>隔离部署副本（B′）：UTSL 启动器在这里生成母图，绝不碰生产 utsl-deploy。</summary>
    private const string GenRoot = @"C:\Users\星梦\Desktop\01_UTSL平台\utsl-verify\poc-gen\gen1";

    private readonly string[] _chainLabels = { "西", "中（默认）", "东" };
    private readonly StudioEngine _engine = new();
    private readonly System.Diagnostics.Stopwatch _sw = new();
    private CancellationTokenSource? _cts;
    private Bitmap? _preview;
    private string _previewPath = "";
    private bool _drawing;
    private bool _finished;

    public MainWindow()
    {
        InitializeComponent();

        string[] args = Environment.GetCommandLineArgs();
        int pi = Array.IndexOf(args, "--preview");
        _previewPath = pi >= 0 && pi + 1 < args.Length
            ? args[pi + 1]
            : @"C:\Users\星梦\Desktop\01_UTSL平台\utsl-verify\out\gen2\preview.png";

        _engine.LogLine += s => Dispatcher.UIThread.Post(() => Log(s));
        _engine.Stage += (stage, pct) => Dispatcher.UIThread.Post(() =>
        {
            if (_finished) { return; }          // 收尾后别再被"迟到的阶段事件"盖掉
            TxtStage.Text = stage;
            BarProgress.Value = Math.Clamp(pct, 0, 100);
            TxtEta.Text = $"{pct:F0}% · {_sw.Elapsed:mm\\:ss}";
        });

        LoadPreview();
        ChkSeam.IsCheckedChanged += (_, _) => DrawSeams();
        BtnRefresh.Click += (_, _) => LoadPreview();
        BtnOpenOut.Click += (_, _) => OpenOutDir();
        PreviewHost.SizeChanged += (_, _) => DrawSeams();
        BtnStart.Click += async (_, _) => await StartAsync();
        BtnCancel.Click += (_, _) => Cancel();

        Log($"[几何] 母图 {MotherWidth}×2400 · 片宽 {SliceWidth} · Δ{Delta} · 重叠 300/缝");
        Log("[就绪] 预览图来自 utsl-verify\\out\\gen2\\preview.png（真实 12000×2400 母图渲染，非示意）");
        Log("[引擎] B′ 路线：隔离副本 " + GenRoot);
    }

    // ==================== 生成流程 ====================
    private async Task StartAsync()
    {
        if (_cts is not null) { return; }
        _cts = new CancellationTokenSource();
        _finished = false;
        _sw.Restart();
        BtnStart.IsEnabled = false;
        BtnCancel.IsEnabled = true;
        BarProgress.Value = 0;

        try
        {
            var req = new GenRequest(
                WorldName: string.IsNullOrWhiteSpace(TxtWorldName.Text) ? "SourceMap" : TxtWorldName.Text!.Trim(),
                Seed: string.IsNullOrWhiteSpace(TxtSeed.Text) ? "20261023" : TxtSeed.Text!.Trim(),
                Width: int.TryParse(TxtWidth.Text, out int w) ? w : 12000,
                Height: int.TryParse(TxtHeight.Text, out int h) ? h : 2400,
                Difficulty: Math.Max(0, CmbDifficulty.SelectedIndex),
                Evil: "random",
                Slices: 3,
                Overlap: 100,
                Chain: new[] { "West", "Dev", "East" },
                DeployRoot: GenRoot,
                OutDir: TxtOut.Text ?? @"C:\Users\星梦\Desktop\01_UTSL平台\utsl-verify\out\gen3");

            Directory.CreateDirectory(req.OutDir);
            string reqFile = Path.Combine(req.OutDir, "gen-request.json");
            File.WriteAllText(reqFile, System.Text.Json.JsonSerializer.Serialize(req,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Log($"[请求] 已写 {reqFile}");

            int port = _engine.Prepare(req);
            Log($"[准备] 配置已写（端口 {port}，单世界 {req.WorldName}，种子 {req.Seed}，{req.Width}×{req.Height}）");

            _engine.Launch(req, port);
            TxtStage.Text = "生成中";
            Log("[生成] 已拉起 UTSL（独立控制台，最小化）—— 12000×2400 约需 6~7 分钟");

            string mother = await _engine.WaitWorldAsync(req, _cts.Token, TimeSpan.FromMinutes(20));
            Log($"[生成] 母图落盘 {new FileInfo(mother).Length / 1048576.0:F1} MB · {_sw.Elapsed:mm\\:ss}");
            _engine.KillEngine();
            BarProgress.Value = 96;

            var parts = await _engine.SplitAsync(req, mother, _cts.Token);
            BarProgress.Value = 100;
            TxtStage.Text = "完成";
            Log($"[完成] 三片已产出（{string.Join(", ", parts.Select(Path.GetFileName))}）· 用时 {_sw.Elapsed:mm\\:ss}");
        }
        catch (OperationCanceledException)
        {
            Log("[取消] 已按用户请求停止");
            TxtStage.Text = "已取消";
        }
        catch (Exception ex)
        {
            Log("[失败] " + ex.GetType().Name + " · " + ex.Message);
            TxtStage.Text = "失败";
        }
        finally
        {
            _engine.KillEngine();
            _cts?.Dispose();
            _cts = null;
            BtnStart.IsEnabled = true;
            BtnCancel.IsEnabled = false;
            _sw.Stop();
            _finished = true;
        }
    }

    private void Cancel()
    {
        try { _cts?.Cancel(); } catch { }
        _engine.KillEngine();
    }

    // ==================== 标题栏 / 预览 / 日志 ====================
    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { BeginMoveDrag(e); }
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
            Dispatcher.UIThread.Post(DrawSeams, DispatcherPriority.Loaded);
        }
        catch (Exception ex) { Log("[预览] 加载失败: " + ex.Message); }
    }

    private void DrawSeams()
    {
        if (_drawing) { return; }
        _drawing = true;
        try { DrawSeamsCore(); } finally { _drawing = false; }
    }

    private void DrawSeamsCore()
    {
        Overlay.Children.Clear();
        if (_preview is null || ChkSeam.IsChecked != true) { return; }

        double hostW = PreviewHost.Bounds.Width, hostH = PreviewHost.Bounds.Height;
        if (hostW < 10 || hostH < 10) { return; }

        double imgW = _preview.PixelSize.Width, imgH = _preview.PixelSize.Height;
        double k = Math.Min(hostW / imgW, hostH / imgH);
        double drawW = imgW * k, drawH = imgH * k;
        double ox = (hostW - drawW) / 2, oy = (hostH - drawH) / 2;

        for (int i = 1; i < 3; i++)
        {
            double x = ox + (Delta * i / (double)MotherWidth) * drawW;
            Overlay.Children.Add(new Line
            {
                StartPoint = new Point(x, oy),
                EndPoint = new Point(x, oy + drawH),
                Stroke = new SolidColorBrush(Color.Parse("#FF0A84FF")),
                StrokeThickness = 1.5,
                StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 5, 4 },
            });
        }

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
            string dir = TxtOut.Text ?? "";
            if (Directory.Exists(dir))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex) { Log("[打开输出] 失败: " + ex.Message); }
    }

    private void Log(string line)
    {
        string prev = TxtLog.Text ?? "";
        string next = (prev.Length > 0 ? prev + "\n" : "") + $"[{DateTime.Now:HH:mm:ss}] {line}";
        var lines = next.Split('\n');
        if (lines.Length > 300) { next = string.Join('\n', lines[^300..]); }
        TxtLog.Text = next;
    }
}
