using System.Text.Json;
using Terraria.WorldBuilding;
using UnifiedServerProcess;

namespace WorldGen.Worker;

/// <summary>
/// 生成期间的实时采样器（在 worker 自己的进程里，所以 <c>Main.tile</c> 就是即将落盘的那份数据）。
///
/// 产物写在 <c>&lt;work&gt;\live\</c>：
///   progress.json { stage, pct, surface, rock, frames, done, ts }
///   frame.bmp     24 位 BMP，**用游戏自己的地图配色**（Terraria.Map.MapHelper.colorLookup）上色，
///                 UI 直接显示、不做二次上色。
///                 参考 mod（JavidPack/WorldGenPreviewer）：它是调游戏的地图渲染 Main.DrawToMap；
///                 我们 headless 没 GPU，就只用它的配色表 —— 纯 CPU，效果接近游戏内地图。
/// </summary>
internal sealed class LiveSampler
{
    private readonly object _gate = new();
    private readonly Timer _timer;
    private string _pass = "初始化";
    private GenerationProgress? _progress;
    private RootContext? _root;
    private long _frames;
    private DateTime _lastAt = DateTime.MinValue;

    private static readonly (byte R, byte G, byte B) Sky = (30, 58, 92);
    private static readonly (byte R, byte G, byte B) Cave = (20, 22, 28);
    private static readonly (byte R, byte G, byte B) Wall = (42, 45, 53);
    private static readonly (byte R, byte G, byte B) Water = (46, 111, 158);
    private static readonly (byte R, byte G, byte B) Lava = (242, 104, 57);
    private static readonly (byte R, byte G, byte B) Honey = (217, 167, 57);
    private static readonly (byte R, byte G, byte B) Shimmer = (186, 131, 229);

    public LiveSampler()
    {
        _timer = new Timer(_ => Capture(), null, 300, 2000);
    }

    public void Enter(string pass, GenerationProgress progress, RootContext root)
    {
        _pass = pass;
        _progress = progress;
        _root = root;
        Capture();
    }

    public void Finish(UnifierTSL.Servers.ServerContext world)
    {
        try { _timer.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
        lock (_gate) { CaptureCore((RootContext)world, final: true); }
        _timer.Dispose();
    }

    private void Capture()
    {
        if (!Monitor.TryEnter(_gate)) { return; }
        try
        {
            var root = _root;
            if (root is not null) { CaptureCore(root, final: false); }
        }
        catch { }
        finally { Monitor.Exit(_gate); }
    }

    private void CaptureCore(RootContext root, bool final)
    {
        var now = DateTime.UtcNow;
        if (!final && (now - _lastAt).TotalMilliseconds < 900) { return; }
        _lastAt = now;

        var m = root.Main;
        int W = m.maxTilesX, H = m.maxTilesY;
        int fw = 0, fh = 0;

        if (W > 0 && H > 0 && m.tile is not null)
        {
            const int MaxW = 2400;
            int step = Math.Max(1, (W + MaxW - 1) / MaxW);
            fw = (W + step - 1) / step;
            fh = (H + step - 1) / step;

            var grid = new ushort[fw * fh];   // bit15 墙 / bit14 液体 / 其余图格
            for (int y = 0; y < fh; y++)
            {
                int sy = Math.Min(H - 1, y * step);
                int row = y * fw;
                for (int x = 0; x < fw; x++)
                {
                    int sx = Math.Min(W - 1, x * step);
                    ushort pick = 0, liquid = 0, wall = 0;
                    for (int dy = 0; dy < 2 && pick == 0; dy++)
                    {
                        int yy = Math.Min(H - 1, sy + dy * Math.Max(1, step / 2));
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int xx = Math.Min(W - 1, sx + dx * Math.Max(1, step / 2));
                            var td = m.tile[xx, yy];
                            if (td.type != 0 && td.type != ushort.MaxValue) { pick = td.type; break; }
                            if (td.liquid > 0 && liquid == 0) { liquid = (ushort)(0x4000 | (td.liquidType() & 0x0F)); }
                            if (td.wall != 0 && wall == 0) { wall = (ushort)(0x8000 | td.wall); }
                        }
                    }
                    grid[row + x] = pick != 0 ? pick : (liquid != 0 ? liquid : wall);
                }
            }

            string live = Path.Combine(Program.WorkDir, "live");
            Directory.CreateDirectory(live);
            byte[] bmp = BuildBmp(grid, fw, fh);
            WriteAtomic(Path.Combine(live, "frame.bmp"), fs => fs.Write(bmp, 0, bmp.Length));
            _frames++;
        }

        double pct = 0;
        string message = _pass;
        try
        {
            pct = _progress?.TotalProgress ?? 0;
            if (!double.IsFinite(pct)) { pct = 0; }
            string msg = _progress?.Message ?? "";
            if (!string.IsNullOrWhiteSpace(msg)) { message = _pass + " - " + msg; }
        }
        catch { }

        WriteAtomic(Path.Combine(Program.WorkDir, "live", "progress.json"), fs =>
        {
            using var w = new Utf8JsonWriter(fs);
            w.WriteStartObject();
            w.WriteString("stage", message);
            w.WriteNumber("pct", Math.Round(Math.Clamp(final ? 1 : pct, 0, 1), 4));
            w.WriteNumber("surface", (int)m.worldSurface);
            w.WriteNumber("rock", (int)m.rockLayer);
            w.WriteNumber("frames", _frames);
            w.WriteBoolean("done", final);
            w.WriteString("ts", DateTimeOffset.Now.ToString("O"));
            w.WriteEndObject();
        });
    }

    /// <summary>24 位 BMP：图格用游戏地图配色（MapHelper.colorLookup），墙/液体/天空按固定色。</summary>
    private static byte[] BuildBmp(ushort[] grid, int w, int h)
    {
        var lookup = Terraria.Map.MapHelper.colorLookup;

        // 地平线：每列首个"图格或液体"（不含墙）→ 中值平滑（不被单棵树拽走，也不沉进洞穴）
        var firstHit = new int[w];
        for (int x = 0; x < w; x++)
        {
            firstHit[x] = h;
            for (int y = 0; y < h; y++)
            {
                ushort v = grid[y * w + x];
                if (v != 0 && (v & 0x8000) == 0) { firstHit[x] = y; break; }
            }
        }
        var skyLine = new int[w];
        const int Rad = 12;
        var med = new int[Rad * 2 + 1];
        for (int x = 0; x < w; x++)
        {
            int n = 0;
            for (int dx = -Rad; dx <= Rad; dx++) { med[n++] = firstHit[Math.Clamp(x + dx, 0, w - 1)]; }
            Array.Sort(med, 0, n);
            skyLine[x] = med[n / 2];
        }

        int rowBytes = w * 3;
        int pad = (4 - rowBytes % 4) % 4;
        int stride = rowBytes + pad;
        int dataSize = stride * h;
        var buf = new byte[54 + dataSize];

        buf[0] = (byte)'B'; buf[1] = (byte)'M';
        BitConverter.GetBytes(54 + dataSize).CopyTo(buf, 2);
        BitConverter.GetBytes(54).CopyTo(buf, 10);
        BitConverter.GetBytes(40).CopyTo(buf, 14);
        BitConverter.GetBytes(w).CopyTo(buf, 18);
        BitConverter.GetBytes(h).CopyTo(buf, 22);
        BitConverter.GetBytes((short)1).CopyTo(buf, 26);
        BitConverter.GetBytes((short)24).CopyTo(buf, 28);
        BitConverter.GetBytes(dataSize).CopyTo(buf, 34);

        for (int y = 0; y < h; y++)
        {
            int srcRow = y * w;
            int dstRow = 54 + (h - 1 - y) * stride;   // BMP 自底向上
            for (int x = 0; x < w; x++)
            {
                ushort v = grid[srcRow + x];
                (byte R, byte G, byte B) c;

                if ((v & 0x4000) != 0)
                {
                    c = (v & 0x0F) switch { 1 => Lava, 2 => Honey, 3 => Shimmer, _ => Water };
                }
                else if (v == 0)
                {
                    c = y < skyLine[x] ? Sky : Cave;
                }
                else if ((v & 0x8000) != 0) { c = Wall; }
                else
                {
                    int t = v & 0x3FFF;
                    if (lookup is not null && t < lookup.Length)
                    {
                        var mc = lookup[t];
                        // 索引缺失/全黑（新版本新增图格常见）→ 用中性灰，别留成黑块
                        if (mc.R + mc.G + mc.B < 24) { mc = new Microsoft.Xna.Framework.Color(110, 112, 120); }
                        double k = 1.0 - 0.18 * (y / (double)h);   // 纵深阴影，避免大片平色
                        c = ((byte)(mc.R * k), (byte)(mc.G * k), (byte)(mc.B * k));
                    }
                    else { c = (118, 136, 121); }
                }

                int o = dstRow + x * 3;
                buf[o] = c.B; buf[o + 1] = c.G; buf[o + 2] = c.R;
            }
        }
        return buf;
    }

    private static void WriteAtomic(string path, Action<Stream> write)
    {
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { write(fs); }
            for (int i = 0; i < 6; i++)
            {
                try
                {
                    if (File.Exists(path)) { File.Replace(tmp, path, null); } else { File.Move(tmp, path); }
                    return;
                }
                catch (IOException) { Thread.Sleep(8 * (i + 1)); }
                catch (UnauthorizedAccessException) { Thread.Sleep(8 * (i + 1)); }
            }
            try { File.Delete(tmp); } catch { }
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
        }
    }
}
