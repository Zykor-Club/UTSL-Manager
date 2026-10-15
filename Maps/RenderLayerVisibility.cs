namespace TEdit5.Controls.WorldRenderEngine.Layers;

/// <summary>
/// TEdit5 里这个类继承 ReactiveObject（只为属性变更通知），被 PixelMap.GetTileColor 的参数签名带进了渲染路径。
/// 管理器侧不需要任何绑定/通知 —— 所以这里放一个**同命名空间同名**的普通类当垫片，
/// 让编译进来的 PixelMap 解析到它，从而不必为了一个 bool 容器把 ReactiveUI 的全局 using 铺满整个管理器工程。
/// 字段与 TEdit5 版本保持一致，避免语义漂移。
/// </summary>
public sealed class RenderLayerVisibility
{
    public bool Wall { get; set; } = true;
    public bool Tile { get; set; } = true;
    public bool Liquid { get; set; } = true;
    public bool WireRed { get; set; } = true;
    public bool WireBlue { get; set; } = true;
    public bool WireGreen { get; set; } = true;
    public bool WireYellow { get; set; } = true;
    public bool Coatings { get; set; } = true;
}
