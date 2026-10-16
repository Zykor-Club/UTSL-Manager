using System.IO.Compression;
using System.Text;
using TEdit.Terraria;

namespace UTSLManager.Maps;

public sealed record UtplItemSlot(int Type, int Stack, int Prefix);
public sealed record UtplChest(int X, int Y, string Name, UtplItemSlot[] Items);
public sealed record UtplSign(int X, int Y, string Text);
public sealed record UtplEntity(byte Kind, int X, int Y, UtplItemSlot[] Slots, bool On);

/// <summary>一个区块模板（模板内相对坐标）。</summary>
public sealed class UtplRegion
{
    public int W { get; init; }
    public int H { get; init; }
    public Tile[] Tiles { get; init; } = [];          // 行优先，长度 W*H
    public List<UtplChest> Chests { get; init; } = [];
    public List<UtplSign> Signs { get; init; } = [];
    public List<UtplEntity> Entities { get; init; } = [];

    /// <summary>
    /// 服务端独有的"额外帧变体"字段（frameNumber/wallFrameX/wallFrameY/wallFrameNumber），按图格下标索引。
    /// TEdit 不保留这些字段 → 从世界裁出来的模板这里全是 null，写文件时就不写这一段（规范允许，文件更小）。
    /// 存在的意义是**往返验证**：读服务端的文件时把它们收着，写回去才能逐字节一致。
    /// </summary>
    public Dictionary<int, (byte FrameNumber, byte WallFrameX, byte WallFrameY, byte WallFrameNumber)> Extras { get; init; } = [];

}

/// <summary>
/// .utpl（v3 语义格式）的读写。**服务端同格式的实现在 UTSLServerPlugin 的
/// src\VirtualWorldCore\UtplFormat.cs**，这里逐字段镜像它（字段顺序错一个就不兼容）。
///
/// ★ entity kind 用的是 .utpl 自己的编号，**不是** Terraria/TEdit 的 TileEntityType：
///     1=ItemFrame 2=DisplayDoll 3=HatRack 4=FoodPlatter 5=LogicSensor 6=WeaponsRack
///   Terraria/TEdit 的枚举是 TrainingDummy=0, ItemFrame=1, LogicSensor=2, DisplayDoll=3,
///   WeaponRack=4, HatRack=5, FoodPlatter=6 —— 两者对不上，服务端是靠显式映射翻译的
///   （VirtualWorldService 里 UtplFormat.KindXxx → TeXxx）。所以这里也必须用 .utpl 的编号。
/// </summary>
public static class UtplCodec
{
    public const uint Magic = 0x4C505455;      // "UTPL" 小端
    public const byte Version = 3;
    public const string Extension = ".utpl";

    public const byte SegChests = 1;
    public const byte SegSigns = 2;
    public const byte SegEntities = 3;
    public const byte SegEnd = 0xFF;

    public const byte KindItemFrame = 1;
    public const byte KindDisplayDoll = 2;
    public const byte KindHatRack = 3;
    public const byte KindFoodPlatter = 4;
    public const byte KindLogicSensor = 5;
    public const byte KindWeaponsRack = 6;

    public const int MaxDim = 8192;
    // ★ 上限只用于"防坏文件把内存吃爆"，模板实际只有几百格。
    //   Tile 是约 44 字节的结构体，Read 里的 new Tile[w*h] 直接按格数分配：
    //   原来的 4096*4096（1677 万格 ≈ 700 MB）形同虚设 —— 一个坏文件就能吃掉几百 MB。
    //   现在收到 2048*2048（≈420 万格 ≈ 180 MB），并把 extras 字典也一并限制在同一量级内。
    public const int MaxCells = 2048 * 2048;
    public const int ChestSlots = 40;
    // ★ 未知段只是"未来版本多出来的段"，正常文件为 0。上限从 256MB 收到 8MB，
    //   避免畸形文件用超大长度触发天文数字的读取（而且每次读都要穿过 GZip 解压层）。
    public const int MaxUnknownSegmentBytes = 8 * 1024 * 1024;
    private const int SkipChunkBytes = 64 * 1024;   // 跳过未知段用的分块缓冲大小

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    // ==================== 写 ====================

    public static void Write(string path, UtplRegion r)
    {
        string tmp = path + ".tmp";
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }

        using (FileStream fs = File.Create(tmp))
        using (GZipStream gz = new(fs, CompressionLevel.Optimal))
        using (BinaryWriter w = new(gz, Utf8, leaveOpen: true))
        {
            WritePayload(w, r);
        }

        if (File.Exists(path)) { File.Delete(path); }
        File.Move(tmp, path);
    }

    private static void WritePayload(BinaryWriter w, UtplRegion r)
    {
        w.Write(Magic);
        w.Write(Version);
        w.Write(r.W);
        w.Write(r.H);

        for (int i = 0; i < r.Tiles.Length; i++)
        {
            Tile t = r.Tiles[i];
            if (IsEmpty(t)) { w.Write((byte)1); continue; }
            w.Write((byte)0);
            WriteTile(w, t, r.Extras.TryGetValue(i, out var ex) ? ex : null);
        }

        WriteChests(w, r.Chests);
        WriteSigns(w, r.Signs);
        WriteEntities(w, r.Entities);
        w.Write(SegEnd);
    }

    /// <summary>与服务端 UtplFormat.IsEmpty 同口径：图格/墙/液体/线/制动器/非激活 全无才算空。</summary>
    public static bool IsEmpty(Tile t) =>
        !t.IsActive && t.Wall == 0 && t.LiquidAmount == 0 && !t.HasWire && !t.Actuator && !t.InActive;

    private static void WriteTile(BinaryWriter w, Tile t,
                                  (byte FrameNumber, byte WallFrameX, byte WallFrameY, byte WallFrameNumber)? extra)
    {
        bool active = t.IsActive;
        bool hasWall = t.Wall != 0;
        bool hasLiquid = t.LiquidAmount != 0;

        byte f1 = 0;
        if (active) { f1 |= 0x01; }
        if (t.InActive) { f1 |= 0x02; }
        if (t.Actuator) { f1 |= 0x04; }
        if (hasWall) { f1 |= 0x08; }
        if (hasLiquid) { f1 |= 0x10; }
        if (t.InvisibleBlock) { f1 |= 0x20; }
        if (t.InvisibleWall) { f1 |= 0x40; }
        if (t.FullBrightBlock) { f1 |= 0x80; }

        byte f2 = (byte)(((byte)t.BrickStyle & 0x07) << 1);
        if (t.FullBrightWall) { f2 |= 0x01; }
        if (t.WireRed) { f2 |= 0x10; }
        if (t.WireBlue) { f2 |= 0x20; }
        if (t.WireGreen) { f2 |= 0x40; }
        if (t.WireYellow) { f2 |= 0x80; }

        // TEdit 不保留 frameNumber / wallFrame* 这些随机变体 → 通常不写额外帧段（规范允许，管理器端可省）；
        // 只有"把服务端文件读进来再写回去"时才透传，好让往返逐字节一致。
        bool hasExtra = extra is not null && (extra.Value.FrameNumber != 0 || extra.Value.WallFrameX != 0
                                           || extra.Value.WallFrameY != 0 || extra.Value.WallFrameNumber != 0);
        w.Write(f1);
        w.Write(f2);
        w.Write(hasExtra ? (byte)0x01 : (byte)0);

        if (active)
        {
            w.Write(t.Type);
            w.Write(t.U);
            w.Write(t.V);
            w.Write(t.TileColor);
        }
        if (hasWall)
        {
            w.Write(t.Wall);
            w.Write(t.WallColor);
        }
        if (hasLiquid)
        {
            w.Write(t.LiquidAmount);
            w.Write((byte)t.LiquidType);      // TEdit 的 LiquidType 就是存盘编码：1/2/3/8
        }
        if (hasExtra && extra is not null)
        {
            w.Write(extra.Value.FrameNumber);
            w.Write(extra.Value.WallFrameX);
            w.Write(extra.Value.WallFrameY);
            w.Write(extra.Value.WallFrameNumber);
        }
    }

    private static void WriteChests(BinaryWriter w, List<UtplChest> chests)
    {
        w.Write(SegChests);
        w.Write(chests.Count);
        foreach (UtplChest c in chests)
        {
            w.Write(c.X);
            w.Write(c.Y);
            w.Write(c.Name ?? "");
            for (int i = 0; i < ChestSlots; i++)
            {
                UtplItemSlot? s = i < c.Items.Length ? c.Items[i] : null;
                w.Write(s?.Type ?? 0);
                w.Write(s?.Stack ?? 0);
                w.Write(s?.Prefix ?? 0);
            }
        }
    }

    private static void WriteSigns(BinaryWriter w, List<UtplSign> signs)
    {
        w.Write(SegSigns);
        w.Write(signs.Count);
        foreach (UtplSign s in signs)
        {
            w.Write(s.X);
            w.Write(s.Y);
            w.Write(s.Text ?? "");
        }
    }

    private static void WriteEntities(BinaryWriter w, List<UtplEntity> ents)
    {
        w.Write(SegEntities);
        w.Write(ents.Count);
        foreach (UtplEntity e in ents)
        {
            w.Write(e.Kind);
            w.Write(e.X);
            w.Write(e.Y);
            w.Write((byte)Math.Min(255, e.Slots.Length));
            for (int i = 0; i < e.Slots.Length && i < 255; i++)
            {
                w.Write(e.Slots[i].Type);
                w.Write(e.Slots[i].Stack);
                w.Write(e.Slots[i].Prefix);
            }
            w.Write(e.On);
        }
    }

    /// <summary>模板名净化（只留安全字符；拒绝空名与路径穿越）。镜像服务端 UtplFormat.SanitizeName。</summary>
    public static string SanitizeName(string? name)
    {
        string safe = (name ?? "").Trim();
        if (safe.Length == 0) { return ""; }
        foreach (char c in Path.GetInvalidFileNameChars()) { safe = safe.Replace(c, '_'); }
        safe = safe.Trim('.', ' ');
        return safe is "" or "." or ".." ? "" : safe;
    }

    // ==================== 读（镜像服务端 Read）====================

    /// <param name="keepExtras">
    /// 是否保留服务端独有的额外帧字段（UtplRegion.Extras）。默认 false：
    /// 渲染等只读用途根本用不到它，省下一整张最多 MaxCells 项的字典；
    /// 只有"读服务端文件再逐字节写回"的往返验证才需要传 true。
    /// </param>
    public static UtplRegion Read(string path, bool keepExtras = false)
    {
        using FileStream fs = File.OpenRead(path);
        using GZipStream gz = new(fs, CompressionMode.Decompress);
        using BinaryReader r = new(gz, Utf8, leaveOpen: true);

        if (r.ReadUInt32() != Magic) { throw new InvalidDataException("不是 .utpl 文件（magic 不匹配）"); }
        if (r.ReadByte() != Version) { throw new InvalidDataException("不支持的 .utpl 版本"); }

        int w = r.ReadInt32(), h = r.ReadInt32();
        if (w <= 0 || h <= 0 || w > MaxDim || h > MaxDim) { throw new InvalidDataException($"模板尺寸非法: {w}x{h}"); }
        if ((long)w * h > MaxCells)
        {
            throw new InvalidDataException(
                $"模板格数过大: {w}x{h} = {(long)w * h} 格（上限 {MaxCells} 格 ≈ 2048x2048）；" +
                "模板通常只有几百格，该文件可能已损坏或不是模板文件");
        }

        var tiles = new Tile[w * h];
        // ★ 只有往返写回才需要 extras；渲染路径（keepExtras=false）这里不分配字典，
        //   省下最多 MaxCells 项的内存。字段仍照常从流里读出来（不能少读一个字节）。
        var extras = keepExtras ? new Dictionary<int, (byte, byte, byte, byte)>() : null;
        for (int i = 0; i < tiles.Length; i++)
        {
            if (r.ReadByte() == 1) { tiles[i] = default; continue; }
            tiles[i] = ReadTile(r, out var ex);
            if (extras is not null && ex is not null) { extras[i] = ex.Value; }
        }

        var chests = new List<UtplChest>();
        var signs = new List<UtplSign>();
        var ents = new List<UtplEntity>();
        while (true)
        {
            byte seg = r.ReadByte();
            if (seg == SegEnd) { break; }
            switch (seg)
            {
                case SegChests:
                    for (int n = r.ReadInt32(), i = 0; i < n; i++)
                    {
                        int x = r.ReadInt32(), y = r.ReadInt32();
                        string name = r.ReadString();
                        var items = new UtplItemSlot[ChestSlots];
                        for (int s = 0; s < ChestSlots; s++) { items[s] = new UtplItemSlot(r.ReadInt32(), r.ReadInt32(), r.ReadInt32()); }
                        chests.Add(new UtplChest(x, y, name, items));
                    }
                    break;
                case SegSigns:
                    for (int n = r.ReadInt32(), i = 0; i < n; i++) { signs.Add(new UtplSign(r.ReadInt32(), r.ReadInt32(), r.ReadString())); }
                    break;
                case SegEntities:
                    for (int n = r.ReadInt32(), i = 0; i < n; i++)
                    {
                        byte kind = r.ReadByte();
                        int x = r.ReadInt32(), y = r.ReadInt32();
                        byte slots = r.ReadByte();
                        var arr = new UtplItemSlot[slots];
                        for (int s = 0; s < slots; s++) { arr[s] = new UtplItemSlot(r.ReadInt32(), r.ReadInt32(), r.ReadInt32()); }
                        bool on = r.ReadByte() != 0;
                        ents.Add(new UtplEntity(kind, x, y, arr, on));
                    }
                    break;
                default:
                    int len = r.ReadInt32();
                    if (len < 0 || len > MaxUnknownSegmentBytes)
                    {
                        throw new InvalidDataException(
                            $"未知段 {seg} 长度非法: {len} 字节（允许 0 ~ {MaxUnknownSegmentBytes} 字节）；" +
                            "文件可能已损坏或来自不兼容的更新版本");
                    }
                    SkipUnknownSegment(r, len, seg);   // 未知段真读跳过（GZipStream 不可 Seek）
                    break;
            }
        }
        return new UtplRegion
        {
            W = w, H = h, Tiles = tiles, Chests = chests, Signs = signs, Entities = ents,
            Extras = extras ?? new Dictionary<int, (byte, byte, byte, byte)>(),
        };
    }

    /// <summary>
    /// 分块跳过未知段：64KB 缓冲 + Read，比逐字节快两个数量级
    /// （逐字节要穿过 GZip 解压层，最多能放大成上亿次调用 → "打开模板卡几十秒"）。
    /// 读到 0 说明文件被截断，抛 EndOfStreamException。
    /// </summary>
    private static void SkipUnknownSegment(BinaryReader r, int len, byte seg)
    {
        byte[] buf = new byte[SkipChunkBytes];
        while (len > 0)
        {
            int n = r.Read(buf, 0, Math.Min(buf.Length, len));
            if (n <= 0) { throw new EndOfStreamException($"未知段 {seg} 数据提前结束：还有 {len} 字节未读"); }
            len -= n;
        }
    }

    private static Tile ReadTile(BinaryReader r, out (byte, byte, byte, byte)? extra)
    {
        extra = null;
        byte f1 = r.ReadByte();
        byte f2 = r.ReadByte();
        byte f3 = r.ReadByte();

        bool active = (f1 & 0x01) != 0;
        bool inActive = (f1 & 0x02) != 0;
        bool actuator = (f1 & 0x04) != 0;
        bool hasWall = (f1 & 0x08) != 0;
        bool hasLiquid = (f1 & 0x10) != 0;
        bool invBlock = (f1 & 0x20) != 0;
        bool invWall = (f1 & 0x40) != 0;
        bool fbBlock = (f1 & 0x80) != 0;

        bool fbWall = (f2 & 0x01) != 0;
        byte style = (byte)((f2 >> 1) & 0x07);
        bool wireRed = (f2 & 0x10) != 0;
        bool wireBlue = (f2 & 0x20) != 0;
        bool wireGreen = (f2 & 0x40) != 0;
        bool wireYellow = (f2 & 0x80) != 0;

        bool hasExtra = (f3 & 0x01) != 0;

        var t = new Tile
        {
            IsActive = active,
            InActive = inActive,
            Actuator = actuator,
            InvisibleBlock = invBlock,
            InvisibleWall = invWall,
            FullBrightBlock = fbBlock,
            FullBrightWall = fbWall,
            BrickStyle = (BrickStyle)style,
            WireRed = wireRed,
            WireBlue = wireBlue,
            WireGreen = wireGreen,
            WireYellow = wireYellow,
        };
        if (active) { t.Type = r.ReadUInt16(); t.U = r.ReadInt16(); t.V = r.ReadInt16(); t.TileColor = r.ReadByte(); }
        if (hasWall) { t.Wall = r.ReadUInt16(); t.WallColor = r.ReadByte(); }
        if (hasLiquid) { t.LiquidAmount = r.ReadByte(); t.LiquidType = (LiquidType)r.ReadByte(); }
        if (hasExtra)
        {
            // 服务端独有字段：TEdit 不保留它们，这里收下来只为往返一致
            byte fn = r.ReadByte(), wx = r.ReadByte(), wy = r.ReadByte(), wn = r.ReadByte();
            extra = (fn, wx, wy, wn);
        }
        return t;
    }
}
