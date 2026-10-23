using System.ComponentModel;
using System.Diagnostics;
using ReactiveUI.Builder;
using SkiaSharp;
using TEdit.Common;
using TEdit.Terraria;
using TEdit5.Controls;
using TEdit5.Controls.WorldRenderEngine;

namespace UTSLManager.Maps;

/// <summary>一次渲染的统计（给界面显示用）。</summary>
public sealed record MapRenderStats(
    int WorldWidth, int WorldHeight, int PixelWidth, int PixelHeight,
    int Chests, int Signs, int Entities, long LoadMs, long RenderMs, long EncodeMs, int Slices = 1,
    int SpawnX = -1, int SpawnY = -1)
{
    /// <summary>一行简短信息。刻意只放事实，不放操作提示。</summary>
    public string Describe()
    {
        string head = Slices > 1 ? $"拼接 {Slices} 片 · 总 {WorldWidth}×{WorldHeight}" : $"{WorldWidth}×{WorldHeight}";
        // ★ EncodeMs 恒为 0（PNG 编解码早已从管理器移除，A 子代理 grep 确认无非 third_party 调用）——
        //   再显示"编码 0ms"就是无意义信息，所以为 0 时不显示。
        string enc = EncodeMs > 0 ? $" · 编码 {EncodeMs}ms" : "";
        return $"{head} · 像素 {PixelWidth}×{PixelHeight} · 箱 {Chests} · 牌 {Signs} · 实体 {Entities}"
             + $" · 读取 {LoadMs}ms · 渲染 {RenderMs}ms{enc}";
    }
}

/// <summary>
/// .wld → 位图。直接跑在管理器进程里（不是插件），渲染口径来自 TEdit5 的 PixelMap：
/// 每个图格画一个纯色像素，不依赖任何贴图/主题资源，因此不需要 TEdit 的 App/DI/视图。
///
/// 两处刻意的设计：
///   1) 单条目世界缓存：解析一个 4200×2400 世界约 0.8~1.4s，切回同一个世界时不重复解析。
///      ★ 内存口径按 A 子代理实测修正：TEdit 的 Tile 是 32 字节（原注释写 44），
///        4200×2400 = 1008 万格 → Tile 数组约 307MB（原注释写"约 200MB"），LoadWorld 后托管堆增量实测 324MB。
///        所以这份缓存是"用约 300MB 换一次 1.4 秒的重解析"，只留一份。
///   2) 按行并行渲染：PixelMap 是纯静态无状态函数，行与行互不重叠，所以并行安全。
/// </summary>
public static class WorldMapRenderer
{
    /// <summary>
    /// 兜底初始化 ReactiveUI。
    /// ★ 实测踩到：TEdit.Terraria.World 的构造函数走 ReactiveUI 的属性变更机制，
    ///   若没有先初始化就会抛 "ReactiveUI has not been initialized. You must initialize ReactiveUI using the builder pattern."，
    ///   而异常发生在 World.LoadWorld 深处 —— 排查成本很高。
    ///   在管理器进程里 Avalonia/ReactiveUI 的 App 已经初始化，这里是幂等的兜底；
    ///   但对**独立调用**（外部工具、探针、将来可能的 CLI，以及文档承诺"插件可直接调 UTSLManager.Maps.*"的场景）
    ///   这一步是必需的。已初始化时 BuildApp 可能抛，所以吞掉异常。
    /// </summary>
    static WorldMapRenderer()
    {
        try { ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp(); }
        catch { /* 已经初始化过就会抛，忽略 */ }
    }

    private static readonly object Gate = new();
    private static bool _ready;
    private static string _info = "未初始化";
    private static string _error = "";

    public static bool Ready { get { lock (Gate) { return _ready; } } }
    public static string Info { get { lock (Gate) { return _info; } } }
    public static string Error { get { lock (Gate) { return _error; } } }

    /// <summary>TEdit 的 World 继承 ReactiveObject，必须先 bootstrap（见 TEdit 自己的单测 ModuleInit.cs）。</summary>
    public static bool EnsureReady()
    {
        lock (Gate)
        {
            if (_ready) { return true; }
            try
            {
                RxAppBuilder.CreateReactiveUIBuilder().BuildApp();
                TerrariaDataStore.Initialize();
                WorldConfiguration.Initialize();
                _info = $"数据表 {WorldConfiguration.TileProperties.Count} 图格 / {WorldConfiguration.WallProperties.Count} 墙 / {WorldConfiguration.GlobalColors.Count} 底色";
                _ready = true;
                _error = "";
                return true;
            }
            catch (Exception ex)
            {
                _error = ex.GetType().Name + ": " + ex.Message;
                _info = "初始化失败";
                return false;
            }
        }
    }

    // ===== 世界缓存（只留最近一个）=====

    private static string _cacheKey = "";
    private static World? _cacheWorld;

    /// <summary>
    /// 每真正解析一个新世界（缓存换新）就 +1。
    /// 宿主据此判断"被换掉的那份世界已经变成垃圾"——它是 LOH 上的 ~330MB，
    /// 不做压缩式 Gen2 GC 就一直占着（实测：连换 2 个世界 WS 690→710MB 不回落，
    /// 一次 GC.Collect(2, Aggressive, compacting) 立刻 -330MB，耗时 116ms）。
    /// 缓存命中（同文件未改）不递增。
    /// </summary>
    public static int CacheVersion { get; private set; }

    /// <summary>把回调直接当 IProgress 用（Progress&lt;T&gt; 会异步 post，这里要同步转发）。</summary>
    private sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }

    /// <summary>
    /// 解析 .wld（同一个文件且未修改时直接复用上次结果）。
    /// progress 报 0..1：载入阶段 TEdit 会按已读图格列报真实百分比（World.FileV2.cs:1514）。
    /// </summary>
    public static (World? world, string error) LoadWorld(string path, Action<double>? progress = null)
    {
        string key;
        try
        {
            var fi = new FileInfo(path);
            key = fi.FullName + "|" + fi.LastWriteTimeUtc.Ticks + "|" + fi.Length;
        }
        catch (Exception ex) { return (null, ex.Message); }

        lock (Gate)
        {
            if (_cacheWorld is not null && _cacheKey == key) { return (_cacheWorld, ""); }
        }

        IProgress<ProgressChangedEventArgs>? lp = progress is null
            ? null
            : new SyncProgress<ProgressChangedEventArgs>(p => progress(Math.Clamp(p.ProgressPercentage / 100.0, 0.0, 1.0)));
        (World? w, Exception? err) = World.LoadWorld(path, false, lp);
        if (w is null) { return (null, err?.Message ?? "未知错误"); }
        // ★ R6：D 子代理实测 —— 0 字节或 4KB 乱码的 .wld 会被 TEdit 当成"合法世界"返回（尺寸 0×0），
        //   截断的 6.5MB 文件也会返回 4200×2400 且 error 为空 → 界面把坏存档渲染成空白/半张图却不报错。
        //   这里至少把"尺寸非法"这种明确的坏文件挡下来（截断的情况 TEdit 内部吞掉了异常，无法在此判定）。
        if (w.TilesWide <= 0 || w.TilesHigh <= 0)
        {
            return (null, $"世界尺寸非法（{w.TilesWide}×{w.TilesHigh}）：文件可能为空、乱码或已损坏");
        }

        lock (Gate)
        {
            _cacheKey = key;
            _cacheWorld = w;      // 旧的交给 GC
            CacheVersion++;       // 通知宿主：上一份世界成了 LOH 垃圾，该做压缩 GC 了
        }
        return (w, "");
    }

    /// <summary>丢弃缓存（换世界/重新读盘时用）。</summary>
    public static void DropCache()
    {
        lock (Gate) { _cacheKey = ""; _cacheWorld = null; }
    }

    /// <summary>
    /// 把一张大图按"整数倍抽样"缩小到 maxW 宽（用于拼接过程中的预览）。
    /// 不用 SKImage.Resize：那会先建 SKImage 再重采样整个源，拼接中途调用太贵；
    /// 这里只读 1/stride 的像素并按行 memcpy 写入目标缓冲，12000×2400 → 1600×320 约 10~20ms。
    /// </summary>
    public static unsafe SKBitmap? MakePreview(SKBitmap src, int maxW)
    {
        try
        {
            if (src.Width <= maxW) { return src.Copy(); }
            int w = Math.Max(1, maxW);
            int h = Math.Max(1, (int)Math.Round(src.Height * (w / (double)src.Width)));
            var dst = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
            byte* sp = (byte*)src.GetPixels();
            byte* dp = (byte*)dst.GetPixels();
            int srb = src.RowBytes, drb = dst.RowBytes;
            int sx = Math.Max(1, src.Width / w), sy = Math.Max(1, src.Height / h);
            for (int y = 0; y < h; y++)
            {
                byte* sprow = sp + (long)(y * sy) * srb;
                byte* dprow = dp + (long)y * drb;
                for (int x = 0; x < w; x++)
                {
                    byte* s = sprow + (long)(x * sx) * 4;
                    byte* d = dprow + (long)x * 4;
                    d[0] = s[0]; d[1] = s[1]; d[2] = s[2]; d[3] = 255;
                }
            }
            return dst;
        }
        catch { return null; }
    }

    // ===== 渲染 =====

    /// <summary>
    /// 渲染成位图。<paramref name="scale"/>=1 即全分辨率。
    ///
    /// ★ 性能要点（实测）：**不要用 SKBitmap.SetPixel**。它每个像素一次 P/Invoke，
    ///   4200×2400（1008 万像素）要 313 秒；而且并行只会因为竞争更慢
    ///   （1400×800 并行 13.9s，比单线程还差）。
    ///   直接写像素缓冲后是纯托管计算，快两个数量级。
    ///   PixelMap.GetTileColor 是纯函数（无静态可变状态），所以这里可以安全地按行分块。
    /// </summary>
    public static SKBitmap Render(World world, int scale, Action<double>? progress = null,
                                 TEdit5.Controls.WorldRenderEngine.Layers.RenderLayerVisibility? layers = null)
    {
        scale = Math.Max(1, scale);
        var bmp = new SKBitmap(Math.Max(1, world.TilesWide / scale), Math.Max(1, world.TilesHigh / scale),
                               SKImageInfo.PlatformColorType, SKAlphaType.Premul);
        RenderInto(bmp, world, scale, 0, progress, layers);
        return bmp;
    }

    /// <summary>
    /// 把一个世界直接渲染进目标位图的指定横向偏移处（拼接时用，省掉每片一张中间位图）。
    ///
    /// 并行策略：PixelMap 是纯静态函数（无可变状态），行与行写入的地址区间互不重叠，
    /// 所以按行并行是安全的；唯一共享的是"已完成行数"计数器，用 Interlocked。
    /// </summary>
    public static unsafe void RenderInto(SKBitmap canvas, World world, int scale, int offsetX, Action<double>? progress = null,
                                        TEdit5.Controls.WorldRenderEngine.Layers.RenderLayerVisibility? layers = null)
    {
        scale = Math.Max(1, scale);
        int ow = Math.Max(1, world.TilesWide / scale);
        int oh = Math.Max(1, world.TilesHigh / scale);
        Tile[,] tiles = world.Tiles;

        if (canvas.ColorType != SKColorType.Bgra8888)
        {
            for (int y = 0; y < oh; y++)
            {
                TEditColor bg0 = BackgroundAt(world, y * scale);
                int ty0 = y * scale;
                for (int x = 0; x < ow; x++)
                {
                    canvas.SetPixel(x + offsetX, y, PixelMap.GetTileColor(tiles[x * scale, ty0], bg0, layers).ToSKColor().WithAlpha(255));
                }
                progress?.Invoke((y + 1) / (double)oh);
            }
            return;
        }

        byte* basePtr = (byte*)canvas.GetPixels().ToPointer();
        int rb = canvas.RowBytes;
        int canvasW = canvas.Width;
        int reportEvery = Math.Max(1, oh / 100);     // 进度最多报 100 次，别把 UI 线程刷爆
        int done = 0;
        var opt = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };

        Parallel.For(0, oh, opt, y =>
        {
            TEditColor bg = BackgroundAt(world, y * scale);
            int ty = y * scale;
            byte* row = basePtr + (long)y * rb;
            int xs = Math.Max(0, -offsetX);
            int xe = Math.Min(ow, canvasW - offsetX);
            for (int x = xs; x < xe; x++)
            {
                SKColor c = PixelMap.GetTileColor(tiles[x * scale, ty], bg, layers).ToSKColor();
                byte* p = row + (long)(x + offsetX) * 4;
                p[0] = c.Blue;       // Bgra8888：内存里是 B,G,R,A
                p[1] = c.Green;
                p[2] = c.Red;
                p[3] = 255;
            }
            if (progress is not null)
            {
                int d = Interlocked.Increment(ref done);
                if (d % reportEvery == 0 || d == oh) { progress(d / (double)oh); }
            }
        });
    }

    /// <summary>
    /// 按偏移把多片世界拼成一张图（服务端的三个大地图本来是一张超大地图切成的三片）。
    ///
    /// ★ 内存要点：一片 4200×2400 的世界约 200MB（~T~Tile[,]~T~），**不能三片同时驻留**。
    ///   所以每渲染完一片立刻 DropCache()，只保留小得多的位图。
    /// <paramref name="OffsetTiles"/> 传负数表示"紧接在上一片后面"（按实际宽度累加）。
    /// </summary>
    public static SKBitmap? RenderStitched(IReadOnlyList<StitchSlice> slices, int scale,
                                           out MapRenderStats stats, out string error, Action<double>? progress = null,
                                           TEdit5.Controls.WorldRenderEngine.Layers.RenderLayerVisibility? layers = null,
                                           Action<SKBitmap, int, int>? slicePreview = null)
    {
        stats = new MapRenderStats(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, slices.Count);
        error = "";
        if (slices.Count == 0) { error = "没有可拼接的切片"; return null; }
        scale = Math.Max(1, scale);

        int totalTilesW = 0, maxTilesH = 0, chests = 0, signs = 0, entities = 0;
        long loadMs = 0, renderMs = 0;
        int curOffset = 0;
        SKBitmap? canvas = null;

        // ★ 画布尺寸必须**一开始就按最大偏移**定好：否则第一片载入时只知道自己的宽度，
        //   后面两片会被裁掉（这里曾经踩过：三片只画出 4200 宽而不是 12000）。
        bool offsetsKnown = slices.All(x => x.OffsetTiles >= 0);
        int maxOffset = offsetsKnown ? slices.Max(x => x.OffsetTiles) : 0;
        try
        {
            for (int si = 0; si < slices.Count; si++)
            {
                StitchSlice s = slices[si];
                int idxL = si, totalL = slices.Count;
                var swL = Stopwatch.StartNew();
                // 实测：拼接里"载入世界"占约 75% 时间、"渲染"占 25% —— 按这个权重合成，
                // 否则进度条会在载入那 1.8 秒里一动不动。
                (World? w, string err) = LoadWorld(s.Path, p => progress?.Invoke((idxL + 0.75 * p) / totalL));
                loadMs += swL.ElapsedMilliseconds;
                if (w is null) { error = s.Name + "：" + err; canvas?.Dispose(); return null; }

                int off = s.OffsetTiles >= 0 ? s.OffsetTiles : curOffset;
                curOffset = off + w.TilesWide;
                totalTilesW = Math.Max(totalTilesW, off + w.TilesWide);
                maxTilesH = Math.Max(maxTilesH, w.TilesHigh);
                chests += w.Chests.Count; signs += w.Signs.Count; entities += w.TileEntities.Count;

                // 第一片载入后用「最大偏移 + 本片宽度」定画布（链上各片同宽是配置保证的）；
                // 偏移未知（按实际宽度累加）时用「片数 × 本片宽」当上界。
                if (canvas is null)
                {
                    int tilesW = offsetsKnown ? maxOffset + w.TilesWide : slices.Count * w.TilesWide;
                    canvas = new SKBitmap(Math.Max(1, (int)Math.Ceiling(tilesW / (double)scale)),
                                          Math.Max(1, (int)Math.Ceiling(w.TilesHigh / (double)scale)),
                                          SKImageInfo.PlatformColorType, SKAlphaType.Premul);
                    maxTilesH = w.TilesHigh;
                }
                // 若这一片比第一片更宽，画布不够就加宽（拷贝旧内容），不静默裁掉
                int needPx = (int)Math.Ceiling((off + w.TilesWide) / (double)scale);
                if (needPx > canvas.Width)
                {
                    var wider = new SKBitmap(needPx, canvas.Height, SKImageInfo.PlatformColorType, SKAlphaType.Premul);
                    using (var c = new SKCanvas(wider)) { c.Clear(new SKColor(0, 0, 0, 255)); c.DrawBitmap(canvas, 0, 0); }
                    canvas.Dispose();
                    canvas = wider;
                }
                if (w.TilesHigh / scale > canvas.Height)
                {
                    int needH = (int)Math.Ceiling(w.TilesHigh / (double)scale);
                    var taller = new SKBitmap(canvas.Width, needH, SKImageInfo.PlatformColorType, SKAlphaType.Premul);
                    using (var c = new SKCanvas(taller)) { c.Clear(new SKColor(0, 0, 0, 255)); c.DrawBitmap(canvas, 0, 0); }
                    canvas.Dispose();
                    canvas = taller;
                }

                int idx = si, total = slices.Count;
                var swR = Stopwatch.StartNew();
                RenderInto(canvas, w, scale, (int)Math.Round(off / (double)scale), p => progress?.Invoke((idx + 0.75 + 0.25 * p) / total), layers);
                renderMs += swR.ElapsedMilliseconds;

                // 逐片回调：让界面先把"已经拼好的部分"显示出来（拼接总共要 3~7 秒，用户不必对着白屏等）
                if (slicePreview is not null)
                {
                    try { slicePreview(canvas, si + 1, slices.Count); } catch { }
                }

                DropCache();     // ★ 立刻释放这一片的世界（约 200MB）
            }

            stats = new MapRenderStats(totalTilesW, maxTilesH, canvas!.Width, canvas.Height,
                chests, signs, entities, loadMs, renderMs, 0, slices.Count);
            return canvas;
        }
        catch
        {
            canvas?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 把一个区块模板（.utpl）渲染成位图 —— 直接遍历 <see cref="UtplRegion.Tiles"/>，不需要构造 World。
    /// 底色刻意用深色（模板没有"世界高度"概念，谈不上海拔配色），这样建筑轮廓最清楚。
    /// </summary>
    public static unsafe SKBitmap RenderRegion(UtplRegion region, int scale, out MapRenderStats stats, Action<double>? progress = null)
    {
        scale = Math.Max(1, scale);
        int ow = Math.Max(1, region.W / scale);
        int oh = Math.Max(1, region.H / scale);
        var bmp = new SKBitmap(ow, oh, SKImageInfo.PlatformColorType, SKAlphaType.Premul);
        // ★ TEditColor 的分量是 float(0..1)，直接传整数会得到乱颜色（曾把整张图染红）。
        //   用 FromString 最稳：格式是 #RRGGBBAA。
        TEditColor bg = TEditColor.FromString("#1C1C22FF");

        var sw = Stopwatch.StartNew();
        if (bmp.ColorType == SKColorType.Bgra8888)
        {
            byte* basePtr = (byte*)bmp.GetPixels().ToPointer();
            int rb = bmp.RowBytes;
            int reportEvery = Math.Max(1, oh / 100);
            int done = 0;
            var opt = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };
            int rw = region.W;
            Tile[] tiles = region.Tiles;
            Parallel.For(0, oh, opt, y =>
            {
                byte* row = basePtr + (long)y * rb;
                int ty = y * scale;
                for (int x = 0; x < ow; x++)
                {
                    SKColor c = PixelMap.GetTileColor(tiles[ty * rw + x * scale], bg).ToSKColor();
                    byte* p = row + (long)x * 4;
                    p[0] = c.Blue; p[1] = c.Green; p[2] = c.Red; p[3] = 255;
                }
                if (progress is not null)
                {
                    int d = Interlocked.Increment(ref done);
                    if (d % reportEvery == 0 || d == oh) { progress(d / (double)oh); }
                }
            });
        }
        else
        {
            for (int y = 0; y < oh; y++)
            {
                int ty = y * scale;
                for (int x = 0; x < ow; x++) { bmp.SetPixel(x, y, PixelMap.GetTileColor(region.Tiles[ty * region.W + x * scale], bg).ToSKColor().WithAlpha(255)); }   // 区域渲染不接图层开关（导出模板用，保持全量）
                progress?.Invoke((y + 1) / (double)oh);
            }
        }
        long renderMs = sw.ElapsedMilliseconds;

        stats = new MapRenderStats(region.W, region.H, ow, oh,
            region.Chests.Count, region.Signs.Count, region.Entities.Count, 0, renderMs, 0);
        return bmp;
    }

    /// <summary>底色随高度变化（太空/天空/地表/岩石/地狱）——TEdit5 RasterTileRenderer 的口径。</summary>
    private static TEditColor BackgroundAt(World w, int y)
    {
        try
        {
            if (y < 80) { return WorldConfiguration.GlobalColors["Space"]; }
            if (y > w.TilesHigh - 192) { return WorldConfiguration.GlobalColors["Hell"]; }
            if (y > w.RockLevel) { return WorldConfiguration.GlobalColors["Rock"]; }
            if (y > w.GroundLevel) { return WorldConfiguration.GlobalColors["Earth"]; }
            return WorldConfiguration.GlobalColors["Sky"];
        }
        catch { return TEditColor.White; }
    }
}
