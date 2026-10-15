namespace UTSLManager.Maps;

/// <summary>服务端"区块模板"能力探测结果。</summary>
public sealed record TemplateCapability(bool CoreModule, bool Consumer, bool AnyTemplates)
{
    /// <summary>
    /// 是否展示模板相关界面（框选 / 导出模板 / 浏览模板）。
    /// 判据：装了核心模块（契约里就有模板 API），或者模板目录里已经有 .utpl（说明确实在用）。
    /// </summary>
    public bool Supported => CoreModule || AnyTemplates;

    /// <summary>能浏览/导出，但服务端没有能消费模板的玩法（如尘歌壶的 /cg paste）。</summary>
    public bool NeedsConsumer => Supported && !Consumer;
}

/// <summary>
/// 探测服务端有没有"区块模板"这套能力，用来决定管理器地图页上要不要显示模板相关控件。
///
/// 为什么用"看文件"而不是"问协议"：只做几次只读检查，本机直读、远端走一次 ssh stat，
/// 不需要给服务端加任何接口，也不依赖 TShock 命令面。
/// ★ 这里是纯函数（exists 由调用方注入），所以可以脱离文件系统直接测。
/// </summary>
public static class ServerCapabilities
{
    /// <summary>核心模块：v7 契约的 ListRegionTemplates / ApplyTemplateAsync 就在它里面。</summary>
    public const string CoreModuleRel = @"plugins\VirtualWorldCore\VirtualWorldCore.dll";

    /// <summary>消费端玩法（目前 /cg paste 来自 MyPotWorld）。</summary>
    public static readonly string[] ConsumerRels =
    [
        @"config\MyPotWorld\settings.json",
        @"plugins\VirtualWorldCore\MyPotWorld.dll",
    ];

    /// <summary>模板目录（相对部署根）。</summary>
    public const string TemplatesRel = @"lib\Saves\Worlds\_utpl_templates";

    /// <summary>
    /// <paramref name="exists"/>：传入"相对部署根的路径"，返回是否存在。
    /// <paramref name="anyTemplates"/>：模板目录里有没有 .utpl（调用方列目录后传入）。
    /// </summary>
    public static TemplateCapability DetectTemplate(Func<string, bool> exists, bool anyTemplates)
    {
        bool core = false;
        try { core = exists(CoreModuleRel); } catch { }
        bool consumer = false;
        foreach (string rel in ConsumerRels)
        {
            try { if (exists(rel)) { consumer = true; break; } } catch { }
        }
        return new TemplateCapability(core, consumer, anyTemplates);
    }
}
