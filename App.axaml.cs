using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace UTSLManager;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    private static bool _scrollBarFixesInstalled;

    /// <summary>
    /// 滚动条「去轨道，只留滑块」的最后一道（前面见 App.axaml 的 ControlTheme）。
    ///
    /// 为什么必须在代码里做：主题在 ScrollViewer 的**模板**里给 ScrollBar 设了 Background（就是那条
    /// 深色长条），模板属性写进去的是**局部值** —— 局部值优先级高于 Style 与 ControlTheme 的 setter，
    /// 所以在 XAML 里怎么设都盖不住。用「属性变更」类处理器：无论谁、什么时候写进去，都会被改成透明。
    /// 对以后新增的滚动区域同样生效（全局，不用逐个改）。
    ///
    /// 抽成公开静态方法是为了能被无头测试直接调用（OnFrameworkInitializationCompleted 在无头下不一定跑到）。
    /// </summary>
    public static void InstallScrollBarFixes()
    {
        if (_scrollBarFixesInstalled) { return; }
        _scrollBarFixesInstalled = true;

        ScrollBar.BackgroundProperty.Changed.AddClassHandler<ScrollBar>((sb, e) =>
        {
            if (e.NewValue is ISolidColorBrush b && b.Color.A != 0) { sb.Background = Brushes.Transparent; }
        });
        ScrollBar.BorderThicknessProperty.Changed.AddClassHandler<ScrollBar>((sb, e) =>
        {
            if (e.NewValue is Thickness t && t != default) { sb.BorderThickness = default; }
        });

        // 两条滚动条交角处的小方块（ScrollViewer 模板里的 Border）也是同一个深色，一并清掉。
        //   名称与背景谁先设不定，所以两个属性都挂，谁后到都能兜住。
        Border.BackgroundProperty.Changed.AddClassHandler<Border>((b, e) =>
        {
            if (b.Name == "PART_ScrollBarsSeparator" && e.NewValue is ISolidColorBrush s && s.Color.A != 0)
            {
                b.Background = Brushes.Transparent;
            }
        });
        Border.NameProperty.Changed.AddClassHandler<Border>((b, _) =>
        {
            if (b.Name == "PART_ScrollBarsSeparator" && b.Background is ISolidColorBrush s && s.Color.A != 0)
            {
                b.Background = Brushes.Transparent;
            }
        });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        InstallScrollBarFixes();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
