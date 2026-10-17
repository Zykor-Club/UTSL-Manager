using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace UTSLManager.Maps;

/// <summary>
/// 地图叠加层：区块网格 + 出生点标记。
///
/// 为什么单独做一个控件而不是往 Canvas 里塞几十上百个 Line/Rectangle：
///   · 网格线数量随缩放变化（放大时要画上千条），用子元素会让布局/合成开销爆炸；
///   · 这里直接重写 Render，只画"视口内可见"的那几条线，代价与屏幕尺寸相关、与世界大小无关。
///
/// 坐标系：它和 MapImage 一样放在 MapSurface(Canvas) 里，尺寸 = 位图像素尺寸，
/// 并且吃同一个 RenderTransform（缩放/平移），所以这里用"图块坐标"直接画即可；
/// 线宽按 1/zoom 反向补偿，保证屏幕上恒为 1 像素。
/// </summary>
public sealed class MapOverlay : Control
{
    /// <summary>当前缩放（与地图一致）。</summary>
    public double Zoom { get; set; } = 1;

    /// <summary>当前平移（图块→屏幕，已含缩放）。</summary>
    public Vector Offset { get; set; }

    /// <summary>是否画区块网格（200×150 图块一条）。</summary>
    public bool ShowGrid { get; set; }

    /// <summary>是否画出生点。</summary>
    public bool ShowSpawn { get; set; }

    public int GridStepX { get; set; } = 200;
    public int GridStepY { get; set; } = 150;

    /// <summary>出生点（图块坐标）；&lt;0 表示不画。</summary>
    public int SpawnX { get; set; } = -1;
    public int SpawnY { get; set; } = -1;

    private static readonly IBrush GridBrush = new SolidColorBrush(Color.FromArgb(90, 102, 204, 255));
    private static readonly IBrush GridBrushStrong = new SolidColorBrush(Color.FromArgb(160, 102, 204, 255));
    private static readonly IBrush SpawnBrush = new SolidColorBrush(Color.FromArgb(230, 255, 90, 90));
    private static readonly IBrush SpawnTextBrush = new SolidColorBrush(Color.FromArgb(230, 255, 140, 140));

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        double z = Zoom <= 0 ? 1 : Zoom;
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 1 || h < 1) { return; }

        // 视口在图块坐标系里的可见范围
        double vx0 = (-Offset.X) / z, vy0 = (-Offset.Y) / z;
        double vx1 = vx0 + w / z, vy1 = vy0 + h / z;

        if (ShowGrid)
        {
            double pen = 1.0 / z;
            int gx = Math.Max(16, GridStepX), gy = Math.Max(16, GridStepY);
            int i0 = (int)Math.Floor(Math.Max(0, vx0) / gx);
            int i1 = (int)Math.Ceiling(Math.Min(w, vx1) / gx);
            int j0 = (int)Math.Floor(Math.Max(0, vy0) / gy);
            int j1 = (int)Math.Ceiling(Math.Min(h, vy1) / gy);

            int limit = 400;   // 极端缩放下限流，避免糊成一片
            if ((i1 - i0) + (j1 - j0) <= limit)
            {
                for (int i = Math.Max(0, i0); i <= i1; i++)
                {
                    double x = i * (double)gx;
                    bool major = (i % 5 == 0);
                    ctx.DrawLine(new Pen(major ? GridBrushStrong : GridBrush, pen), new Point(x, Math.Max(0, vy0)), new Point(x, Math.Min(h, vy1)));
                }
                for (int j = Math.Max(0, j0); j <= j1; j++)
                {
                    double y = j * (double)gy;
                    bool major = (j % 5 == 0);
                    ctx.DrawLine(new Pen(major ? GridBrushStrong : GridBrush, pen), new Point(Math.Max(0, vx0), y), new Point(Math.Min(w, vx1), y));
                }
            }
        }

        if (ShowSpawn && SpawnX >= 0 && SpawnY >= 0)
        {
            double sx = SpawnX, sy = SpawnY;
            double r = 6.0 / z;
            ctx.DrawEllipse(SpawnBrush, new Pen(Brushes.White, 1.5 / z), new Point(sx, sy), r, r);
            ctx.DrawLine(new Pen(SpawnBrush, 1.5 / z), new Point(sx - r * 2, sy), new Point(sx + r * 2, sy));
            ctx.DrawLine(new Pen(SpawnBrush, 1.5 / z), new Point(sx, sy - r * 2), new Point(sx, sy + r * 2));
        }
    }
}
