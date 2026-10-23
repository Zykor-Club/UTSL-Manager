using TEdit.Terraria;

namespace UTSLManager.Maps;

/// <summary>把世界的一块矩形区域裁成区块模板（.utpl）。</summary>
public static class WorldCropper
{
    public sealed record CropResult(UtplRegion Region, string Warning);

    /// <summary>
    /// 选区 → **实际会被裁出来的**矩形（含贴边截断 / 单边 8192 上限 / 总格数上限导致的截高）。
    /// ★ 抽出来的原因：插件按选区尺寸给模板起名，而 Crop 会截断 → 名字里的尺寸和文件里的尺寸对不上
    ///   （实测自动名 `Dev_545x397`，实际裁出 `541×816`）。命名和裁切必须用同一套夹取逻辑，否则必然漂移。
    /// tilesWide/tilesHigh 传世界图格尺寸；未知时传 0 表示"不做贴边夹取"。
    /// </summary>
    public static (int X, int Y, int W, int H, string Note) ClampRegion(
        int tilesWide, int tilesHigh, int x0, int y0, int w, int h)
    {
        if (tilesWide > 0) { x0 = Math.Clamp(x0, 0, Math.Max(0, tilesWide - 1)); }
        if (tilesHigh > 0) { y0 = Math.Clamp(y0, 0, Math.Max(0, tilesHigh - 1)); }
        int maxW = tilesWide > 0 ? Math.Min(tilesWide - x0, UtplCodec.MaxDim) : UtplCodec.MaxDim;
        int maxH = tilesHigh > 0 ? Math.Min(tilesHigh - y0, UtplCodec.MaxDim) : UtplCodec.MaxDim;
        w = Math.Clamp(w, 1, Math.Max(1, maxW));
        h = Math.Clamp(h, 1, Math.Max(1, maxH));
        // ★ 单边受 MaxDim(8192) 限制还不够：UtplCodec.MaxCells 是 2048×2048 ≈ 419 万格，
        //   否则会裁出"导出成功、却读不回来"的模板（Read 会拒绝，用户白等一场）。
        //   这里只【截断高度】而不缩放 —— 缩放会改变建筑的实际尺寸，是错的。
        string limitNote = "";
        if ((long)w * h > UtplCodec.MaxCells)
        {
            int newH = Math.Max(1, (int)(UtplCodec.MaxCells / (long)w));
            limitNote = $"（选区过大：高度从 {h} 截断到 {newH} 格，模板上限 {UtplCodec.MaxCells} 格）";
            h = newH;
        }
        return (x0, y0, w, h, limitNote);
    }

    /// <summary>
    /// 裁 (x0,y0) 起 w×h 的区域。所有记录都转成**模板内相对坐标**（服务端贴的时候是"贴入点 + 记录坐标"）。
    /// </summary>
    public static CropResult Crop(World world, int x0, int y0, int w, int h)
    {
        (x0, y0, w, h, string limitNote) = ClampRegion(world.TilesWide, world.TilesHigh, x0, y0, w, h);

        var tiles = new Tile[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                tiles[y * w + x] = world.Tiles[x0 + x, y0 + y];
            }
        }

        var chests = new List<UtplChest>();
        foreach (Chest c in world.Chests)
        {
            if (c.X < x0 || c.X >= x0 + w || c.Y < y0 || c.Y >= y0 + h) { continue; }
            var slots = new UtplItemSlot[UtplCodec.ChestSlots];
            for (int i = 0; i < UtplCodec.ChestSlots; i++)
            {
                Item? it = i < c.Items.Count ? c.Items[i] : null;
                slots[i] = new UtplItemSlot(it?.NetId ?? 0, it?.StackSize ?? 0, it?.Prefix ?? 0);
            }
            chests.Add(new UtplChest(c.X - x0, c.Y - y0, c.Name ?? "", slots));
        }

        var signs = new List<UtplSign>();
        foreach (Sign s in world.Signs)
        {
            if (s.X < x0 || s.X >= x0 + w || s.Y < y0 || s.Y >= y0 + h) { continue; }
            signs.Add(new UtplSign(s.X - x0, s.Y - y0, s.Text ?? ""));
        }

        var ents = new List<UtplEntity>();
        int skipped = 0;
        foreach (TileEntity e in world.TileEntities)
        {
            if (e.PosX < x0 || e.PosX >= x0 + w || e.PosY < y0 || e.PosY >= y0 + h) { continue; }
            byte? kind = UtplKindOf((TileEntityType)e.Type);
            if (kind is null) { skipped++; continue; }        // 训练假人/传送塔等 .utpl 没有的类型
            ents.Add(new UtplEntity(kind.Value, e.PosX - x0, e.PosY - y0, SlotsOf(e, kind.Value), e.On));
        }

        string warning = (skipped > 0 ? $"（{skipped} 个 .utpl 不支持的图格实体已跳过）" : "") + limitNote;
        // 注意：TEdit 的 TileEntityItem 没有 prefix 字段 → 实体槽位的前缀一律为 0
        return new CropResult(new UtplRegion
        {
            W = w, H = h, Tiles = tiles, Chests = chests, Signs = signs, Entities = ents,
        }, warning);
    }

    /// <summary>
    /// ★ .utpl 的 kind 是它自己的编号，**不是** Terraria/TEdit 的 TileEntityType
    ///   （服务端用 UtplFormat.KindXxx → TeXxx 显式翻译）。这里必须按 .utpl 的编号映射。
    /// </summary>
    public static byte? UtplKindOf(TileEntityType t) => t switch
    {
        TileEntityType.ItemFrame => UtplCodec.KindItemFrame,
        TileEntityType.DisplayDoll => UtplCodec.KindDisplayDoll,
        TileEntityType.HatRack => UtplCodec.KindHatRack,
        TileEntityType.FoodPlatter => UtplCodec.KindFoodPlatter,
        TileEntityType.LogicSensor => UtplCodec.KindLogicSensor,
        TileEntityType.WeaponRack => UtplCodec.KindWeaponsRack,
        _ => null,
    };

    private static UtplItemSlot[] SlotsOf(TileEntity e, byte kind)
    {
        static UtplItemSlot One(int netId, int stack) => new(netId, stack, 0);
        return kind switch
        {
            UtplCodec.KindItemFrame or UtplCodec.KindWeaponsRack or UtplCodec.KindFoodPlatter
                => [One(e.NetId, e.StackSize)],
            UtplCodec.KindLogicSensor
                => [],
            // 服务端读回来时也是"先 Items 再 Dyes"（HatRack: _items+_dyes；DisplayDoll: _equip+_dyes）
            UtplCodec.KindHatRack or UtplCodec.KindDisplayDoll
                => [.. e.Items.Select(i => One(i.Id, i.StackSize)), .. e.Dyes.Select(i => One(i.Id, i.StackSize))],
            _ => [],
        };
    }
}
