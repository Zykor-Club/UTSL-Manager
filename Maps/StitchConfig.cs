using System.Text.Json.Nodes;

namespace UTSLManager.Maps;

/// <summary>一片（一个世界）在整张拼接图里的位置。</summary>
public sealed record StitchSlice(string Name, string Path, int OffsetTiles);

/// <summary>
/// 读取服务端 EdgeStitch 的拼接规格（config\EdgeStitch\EdgeStitch.json）。
///
/// 服务端的三个大地图本来是**一张超大地图切成的三片**，EdgeStitch 插件负责跨片走边缘传送，
/// 它的配置就是这张大图的权威规格：
///   "世界链"     : ["West","Dev","East"]   ← 从左到右的顺序
///   "切片间距_格": 3900                     ← 相邻两片的起点间隔（世界宽 4200，所以相邻重叠 300 格）
/// 总宽 = 间距 × (片数-1) + 末片宽。
/// </summary>
public static class StitchConfig
{
    public sealed record Plan(string[] Chain, int SpacingTiles, bool Loop)
    {
        /// <summary>整张拼接图的估算总宽（图格）。间距已知时用它决定渲染缩放；否则按每片 4200 粗估。</summary>
        public int EstimateTotalTilesWide()
            => SpacingTiles > 0 ? SpacingTiles * Chain.Length : Chain.Length * 4200;
    };

    /// <summary>解析配置文本；解析不出来返回 null（调用方自行回退）。</summary>
    public static Plan? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) { return null; }
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o) { return null; }
            var chain = new List<string>();
            if (o["世界链"] is JsonArray arr)
            {
                foreach (var n in arr)
                {
                    string s = n?.ToString() ?? "";
                    if (s.Trim().Length > 0) { chain.Add(s.Trim()); }
                }
            }
            if (chain.Count == 0) { return null; }

            int spacing = 0;
            if (int.TryParse(o["切片间距_格"]?.ToString(), out int sp) && sp > 0) { spacing = sp; }
            if (spacing == 0)
            {
                // 没配间距就退化成"首尾相接"（按各世界实际宽度累加，见 BuildSlices）
                spacing = -1;
            }

            bool loop = (o["首尾相连成环"]?.ToString() ?? "false").Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            return new Plan(chain.ToArray(), spacing, loop);
        }
        catch { return null; }
    }
}
