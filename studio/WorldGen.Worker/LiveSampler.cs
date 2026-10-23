using System.Text.Json;
using Terraria.WorldBuilding;
using UnifiedServerProcess;

namespace WorldGen.Worker;

/// <summary>
/// 生成期间的实时采样器（在 worker 自己的进程里，所以 <c>Main.tile</c> 就是即将落盘的那份数据）。
///
/// 产物写在 <c>&lt;work&gt;\live\</c>：
///   progress.json { stage, pct, surface, rock, ts }
///   frame.bin     'UTWF' + int32 w + int32 h + ushort[w*h]
///                 bit15=墙 id · bit14=液体(低4位=类型) · 其余=图格 id · 0=空
///
/// 尺寸：最宽 2400（200 万像素上限的 1/2 左右），取样 2×2，优先级 图格 &gt; 液体 &gt; 墙。
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

    public LiveSampler()
    {
        _timer = new Timer(_ => Capture(), null, 300, 2000);
    }

    public void Enter(string pass, GenerationProgress progress, RootContext root)
    {
        _pass = pass;
        _progress = progress;
        _root = root;
        // 换 pass 时立刻抓一帧，阶段切换的画面更贴
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

        if (W > 0 && H > 0)
        {
            const int MaxW = 2400;
            int step = Math.Max(1, (W + MaxW - 1) / MaxW);
            fw = (W + step - 1) / step;
            fh = (H + step - 1) / step;

            var px = new ushort[fw * fh];
            for (int y = 0; y < fh; y++)
            {
                int sy = Math.Min(H - 1, y * step);
                int row = y * fw;
                for (int x = 0; x < fw; x++)
                {
                    int sx = Math.Min(W - 1, x * step);
                    ushort pick = 0, liquid = 0, wall = 0;
                    // 3×3 取样：5×5 的块只采两个角会漏掉大部分图格，地形看起来全是洞（天空下出现"空地条"）
                    for (int dy = 0; dy < 3 && pick == 0; dy++)
                    {
                        int yy = Math.Min(H - 1, sy + dy * Math.Max(1, step / 3));
                        for (int dx = 0; dx < 3; dx++)
                        {
                            int xx = Math.Min(W - 1, sx + dx * Math.Max(1, step / 3));
                            var td = m.tile[xx, yy];
                            if (td.type != 0 && td.type != ushort.MaxValue) { pick = td.type; break; }
                            if (td.liquid > 0 && liquid == 0) { liquid = (ushort)(0x4000 | (td.liquidType() & 0x0F)); }
                            if (td.wall != 0 && wall == 0) { wall = (ushort)(0x8000 | td.wall); }
                        }
                    }
                    px[row + x] = pick != 0 ? pick : (liquid != 0 ? liquid : wall);
                }
            }

            string live = Path.Combine(Program.WorkDir, "live");
            Directory.CreateDirectory(live);
            WriteAtomic(Path.Combine(live, "frame.bin"), fs =>
            {
                using var bw = new BinaryWriter(fs);
                bw.Write(0x46575455);   // 'UTWF'
                bw.Write(fw);
                bw.Write(fh);
                foreach (var v in px) { bw.Write(v); }
            });
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
