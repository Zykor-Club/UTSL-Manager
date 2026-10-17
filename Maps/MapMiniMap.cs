using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace UTSLManager.Maps;

/// <summary>
/// 小地图：把整张地图按"源矩形 → 控件矩形"缩着画一次，再叠一个视口框。
///
/// 为什么不用缩略位图：整图 12000×2400 生成一张缩略图要重采样 2880 万像素（约 115MB 中间缓冲），
/// 而 DrawImage 的源矩形缩放是 GPU/Skia 采样，只画 208×~40 的目标像素，几乎零成本。
/// 换图时只需把 <see cref="Source"/> 换成新位图。
/// </summary>
public sealed class MapMiniMap : Control
{
    private static readonly IBrush Dim = new SolidColorBrush(Color.FromArgb(36, 0, 0, 0));
    private static readonly IBrush Edge = new SolidColorBrush(Color.FromArgb(230, 102, 204, 255));

    public IImage? Source { get; set; }

    /// <summary>整图尺寸（位图像素）。</summary>
    public Size ImageSize { get; set; }

    /// <summary>当前视口在图上的像素矩形。</summary>
    public Rect ViewportInImage { get; set; }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 2 || h < 2) { return; }
        if (Source is not null)
        {
            ctx.DrawImage(Source, new Rect(0, 0, w, h));
        }
        if (ImageSize.Width < 1 || ImageSize.Height < 1) { return; }

        double kx = w / ImageSize.Width, ky = h / ImageSize.Height;
        var r = new Rect(ViewportInImage.X * kx, ViewportInImage.Y * ky,
                         Math.Max(2, ViewportInImage.Width * kx), Math.Max(2, ViewportInImage.Height * ky));
        // 只压暗"视口之外"的四周，视口内保持原样
        if (r.X > 0) { ctx.FillRectangle(Dim, new Rect(0, 0, r.X, h)); }
        if (r.Right < w) { ctx.FillRectangle(Dim, new Rect(r.Right, 0, w - r.Right, h)); }
        if (r.Y > 0) { ctx.FillRectangle(Dim, new Rect(r.X, 0, r.Width, r.Y)); }
        if (r.Bottom < h) { ctx.FillRectangle(Dim, new Rect(r.X, r.Bottom, r.Width, h - r.Bottom)); }
        ctx.DrawRectangle(null, new Pen(Edge, 1.2), r);
    }
}
