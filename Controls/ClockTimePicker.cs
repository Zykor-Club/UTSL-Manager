using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace UTSLManager.Controls;

/// <summary>
/// 钟表式时间选择器（用户明确要求："改成那种钟表，拖动分针来调整时间"）。
///
/// 布局：**外环 = 分钟（0-59，每 5 分钟一个刻度与标注）**、**内环 = 小时（0-23，24 小时制，每 3 小时标注）**。
/// 交互：在盘面上按下/拖动即可 —— 按下的位置离圆心远（> 0.60R）就调分钟，否则调小时；松手结束。
/// 值格式固定为 "HH:mm"（与插件面板契约一致，Lua 侧无需改动）。
///
/// ★ 角度↔时间 的换算都做成 **静态纯函数**，方便自动化测试（探针 ClockMath 会直接验证它们）。
/// </summary>
public sealed class ClockTimePicker : Control
{
    private int _hour;       // 0-23
    private int _minute;     // 0-59

    private enum DragTarget { None, Hour, Minute }
    private DragTarget _drag = DragTarget.None;

    /// <summary>值变化（拖动过程中会多次触发）。</summary>
    public event EventHandler? ValueChanged;

    public ClockTimePicker()
    {
        Width = 176;
        Height = 176;
        Focusable = false;
    }

    /// <summary>当前值，格式 "HH:mm"。</summary>
    public string Value
    {
        get => $"{_hour:00}:{_minute:00}";
        set
        {
            if (TryParse(value, out int h, out int m))
            {
                _hour = h; _minute = m;
                InvalidateVisual();
            }
        }
    }

    // ===== 纯函数（可自动化测试）=====

    /// <summary>解析 "H:m" / "HH:mm"，并做范围钳制。解析不出来返回 false。</summary>
    public static bool TryParse(string? s, out int hour, out int minute)
    {
        hour = 0; minute = 0;
        if (string.IsNullOrWhiteSpace(s)) { return false; }
        var parts = s.Trim().Split(':');
        if (parts.Length != 2) { return false; }
        if (!int.TryParse(parts[0], out int h) || !int.TryParse(parts[1], out int m)) { return false; }
        if (h < 0 || h > 23 || m < 0 || m > 59) { return false; }
        hour = h; minute = m;
        return true;
    }

    /// <summary>角度（12 点方向为 0，顺时针为正，单位度）→ 小时 0-23。</summary>
    public static int AngleToHour(double angleDeg) => (int)Math.Round(Norm(angleDeg) / 15.0) % 24;

    /// <summary>角度 → 分钟 0-59。</summary>
    public static int AngleToMinute(double angleDeg) => (int)Math.Round(Norm(angleDeg) / 6.0) % 60;

    public static double HourToAngle(int hour) => ((hour % 24) + 24) % 24 * 15.0;

    public static double MinuteToAngle(int minute) => ((minute % 60) + 60) % 60 * 6.0;

    /// <summary>屏幕坐标 → 角度（度）。注意屏幕 Y 轴向下，所以取负号。</summary>
    public static double PointToAngle(double dx, double dy)
    {
        double deg = Math.Atan2(dx, -dy) * 180.0 / Math.PI;
        return Norm(deg);
    }

    private static double Norm(double deg) => (deg % 360 + 360) % 360;

    // ===== 交互 =====

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        double cx = Bounds.Width / 2, cy = Bounds.Height / 2;
        double dx = p.X - cx, dy = p.Y - cy;
        double r = Math.Sqrt(dx * dx + dy * dy);
        double outer = Math.Min(Bounds.Width, Bounds.Height) / 2 - 6;
        _drag = r > outer * 0.60 ? DragTarget.Minute : DragTarget.Hour;   // 外环调分钟，内环调小时
        Apply(p, cx, cy);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag == DragTarget.None) { return; }
        var p = e.GetPosition(this);
        Apply(p, Bounds.Width / 2, Bounds.Height / 2);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _drag = DragTarget.None;
        e.Pointer.Capture(null);
    }

    private void Apply(Point p, double cx, double cy)
    {
        double angle = PointToAngle(p.X - cx, p.Y - cy);
        if (_drag == DragTarget.Minute)
        {
            int m = AngleToMinute(angle);
            if (m != _minute) { _minute = m; InvalidateVisual(); ValueChanged?.Invoke(this, EventArgs.Empty); }
        }
        else if (_drag == DragTarget.Hour)
        {
            int h = AngleToHour(angle);
            if (h != _hour) { _hour = h; InvalidateVisual(); ValueChanged?.Invoke(this, EventArgs.Empty); }
        }
    }

    // ===== 绘制 =====

    // ★ 配色决定可读性：**外环=分钟=橙**、**内环=小时=蓝**，与对应指针同色，一眼就能分清哪个环在调什么。
    private static readonly IBrush Face = new SolidColorBrush(Color.Parse("#101B2E"));
    private static readonly IBrush RingLine = new SolidColorBrush(Color.Parse("#243A5E"));
    private static readonly IBrush HourRingLine = new SolidColorBrush(Color.Parse("#2C4E78"));
    private static readonly IBrush TickMinute = new SolidColorBrush(Color.Parse("#6E5A3A"));
    private static readonly IBrush TickMinuteMajor = new SolidColorBrush(Color.Parse("#C98F4E"));
    private static readonly IBrush TickHour = new SolidColorBrush(Color.Parse("#2F5A85"));
    private static readonly IBrush TickHourMajor = new SolidColorBrush(Color.Parse("#5C9BD1"));
    private static readonly IBrush LabelMinute = new SolidColorBrush(Color.Parse("#D9A263"));
    private static readonly IBrush LabelHour = new SolidColorBrush(Color.Parse("#6FA8D6"));
    private static readonly IBrush HandHour = new SolidColorBrush(Color.Parse("#7DCFFF"));
    private static readonly IBrush HandMinute = new SolidColorBrush(Color.Parse("#FFB86B"));
    private static readonly IBrush CenterDot = new SolidColorBrush(Color.Parse("#DCE7F5"));

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 20 || h < 20) { return; }
        double cx = w / 2, cy = h / 2;
        double R = Math.Min(w, h) / 2 - 6;

        ctx.DrawEllipse(Face, new Pen(RingLine, 1), new Point(cx, cy), R, R);

        double rHourRing = R * 0.60;
        // ★ 小时环的"引导圆"：没有它，两个环的刻度/数字混在一起根本分不清（这是渲染出来看到才发现的）
        ctx.DrawEllipse(null, new Pen(HourRingLine, 1), new Point(cx, cy), rHourRing, rHourRing);

        // 外环：分钟刻度（每 5 分钟加长加亮）—— 橙色系
        for (int m = 0; m < 60; m++)
        {
            double a = (MinuteToAngle(m) - 90) * Math.PI / 180.0;
            bool major = m % 5 == 0;
            double len = major ? 8 : 4;
            var p1 = new Point(cx + Math.Cos(a) * (R - 1), cy + Math.Sin(a) * (R - 1));
            var p2 = new Point(cx + Math.Cos(a) * (R - 1 - len), cy + Math.Sin(a) * (R - 1 - len));
            ctx.DrawLine(new Pen(major ? TickMinuteMajor : TickMinute, major ? 1.6 : 1), p1, p2);
        }
        // 外环标注：0/15/30/45（橙色）
        foreach (int m in new[] { 0, 15, 30, 45 })
        {
            double a = (MinuteToAngle(m) - 90) * Math.PI / 180.0;
            DrawLabel(ctx, m.ToString("00"), cx + Math.Cos(a) * (R - 20), cy + Math.Sin(a) * (R - 20), 10.5, LabelMinute);
        }

        // 内环：小时刻度（24 个，每 3 小时加长）—— 蓝色系
        for (int hh = 0; hh < 24; hh++)
        {
            double a = (HourToAngle(hh) - 90) * Math.PI / 180.0;
            bool major = hh % 3 == 0;
            double len = major ? 7 : 3;
            var p1 = new Point(cx + Math.Cos(a) * rHourRing, cy + Math.Sin(a) * rHourRing);
            var p2 = new Point(cx + Math.Cos(a) * (rHourRing - len), cy + Math.Sin(a) * (rHourRing - len));
            ctx.DrawLine(new Pen(major ? TickHourMajor : TickHour, major ? 1.5 : 1), p1, p2);
        }
        // 内环标注：★ 只标 06/12/18 —— 00 不标，否则会和分钟环顶部的 00 叠在一起（渲染出来看到的）
        foreach (int hh in new[] { 6, 18 })
        {
            double a = (HourToAngle(hh) - 90) * Math.PI / 180.0;
            DrawLabel(ctx, hh.ToString("00"), cx + Math.Cos(a) * (rHourRing - 19), cy + Math.Sin(a) * (rHourRing - 19), 10.5, LabelHour);
        }

        // 指针（★ 末端加"可抓"的圆头：光看两根线不像能拖）
        double am = (MinuteToAngle(_minute) - 90) * Math.PI / 180.0;
        double ah = (HourToAngle(_hour) - 90) * Math.PI / 180.0;
        double hx = cx + Math.Cos(ah) * (rHourRing - 6), hy = cy + Math.Sin(ah) * (rHourRing - 6);
        double mx = cx + Math.Cos(am) * (R - 14), my = cy + Math.Sin(am) * (R - 14);
        ctx.DrawLine(new Pen(HandHour, _drag == DragTarget.Hour ? 4.5 : 3), new Point(cx, cy), new Point(hx, hy));
        ctx.DrawEllipse(HandHour, null, new Point(hx, hy), _drag == DragTarget.Hour ? 6 : 5, _drag == DragTarget.Hour ? 6 : 5);
        ctx.DrawLine(new Pen(HandMinute, _drag == DragTarget.Minute ? 4 : 3), new Point(cx, cy), new Point(mx, my));
        ctx.DrawEllipse(HandMinute, null, new Point(mx, my), _drag == DragTarget.Minute ? 6.5 : 5.5, _drag == DragTarget.Minute ? 6.5 : 5.5);
        ctx.DrawEllipse(CenterDot, null, new Point(cx, cy), 3.5, 3.5);

        // 中央时间文本（放中心稍下，避开指针交点）
        DrawLabel(ctx, $"{_hour:00}:{_minute:00}", cx, cy + R * 0.34, 15, new SolidColorBrush(Color.Parse("#E6EEF9")), center: true);
    }

    private static void DrawLabel(DrawingContext ctx, string text, double x, double y, double size, IBrush brush, bool center = false)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI, Microsoft YaHei"), size, brush);
        var org = center ? new Point(x - ft.Width / 2, y - ft.Height / 2) : new Point(x - ft.Width / 2, y - ft.Height / 2);
        ctx.DrawText(ft, org);
    }
}
