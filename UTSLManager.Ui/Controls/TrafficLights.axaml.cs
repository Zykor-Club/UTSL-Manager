using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace UTSLManager.Ui.Controls;

/// <summary>
/// macOS 风格圆角红绿灯（关闭/最小化/最大化），从管理器的标题栏抽出来给两个程序共用。
/// 用法：放在自绘标题栏里 —— <c>&lt;ui:TrafficLights /&gt;</c>，窗口需开
/// <c>ExtendClientAreaToDecorationsHint="True"</c>，标题栏那段要自己接拖拽。
/// </summary>
public partial class TrafficLights : UserControl
{
    private Window? _win;

    public TrafficLights()
    {
        InitializeComponent();

        // ★ 这个库是独立程序集，XAML 名生成器不会给别人的命名空间出字段 → 一律 FindControl
        var close = this.FindControl<Border>("BtnClose");
        var min = this.FindControl<Border>("BtnMin");
        var max = this.FindControl<Border>("BtnMax");
        close?.AddHandler(PointerPressedEvent, (_, _) => _win?.Close(), RoutingStrategies.Tunnel);
        min?.AddHandler(PointerPressedEvent, (_, _) => { if (_win is not null) { _win.WindowState = WindowState.Minimized; } }, RoutingStrategies.Tunnel);
        max?.AddHandler(PointerPressedEvent, (_, _) =>
        {
            if (_win is null) { return; }
            _win.WindowState = _win.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }, RoutingStrategies.Tunnel);

        AttachedToVisualTree += (_, _) =>
        {
            _win = TopLevel.GetTopLevel(this) as Window;
            if (_win is null) { return; }
            _win.Activated += OnActivated;
            _win.Deactivated += OnDeactivated;
            SetActive(_win.IsActive);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (_win is null) { return; }
            _win.Activated -= OnActivated;
            _win.Deactivated -= OnDeactivated;
            _win = null;
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnActivated(object? sender, EventArgs e) => SetActive(true);
    private void OnDeactivated(object? sender, EventArgs e) => SetActive(false);

    /// <summary>失焦时只降透明度，不隐藏、不位移（和 macOS / 管理器一致）。</summary>
    private void SetActive(bool active)
    {
        double op = active ? 1.0 : 0.45;
        foreach (var n in new[] { "BtnClose", "BtnMin", "BtnMax" })
        {
            var b = this.FindControl<Border>(n);
            if (b is not null) { b.Opacity = op; }
        }
    }
}
