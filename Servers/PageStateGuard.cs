using System;

namespace UTSLManager.Servers;

/// <summary>
/// 切档后"页面级一致性"的判定逻辑（纯函数，无 UI 依赖，方便被探针/回归直接测）。
///
/// 背景：这些页面各自缓存着"上一台服务器"的内容，切档时若不主动处理就会串台：
///   · 地图预览：画布上是上一台渲染出的世界图（看起来"切了服务器但地图没变"）
///   · 在线编辑器：editorPath 是绝对路径，而读写走"当前激活档"的后端 ——
///     在 A 台打开文件、切到 B、再保存，会把 A 的内容写进 B 的同名路径（数据破坏）
/// </summary>
public static class PageStateGuard
{
    /// <summary>
    /// 编辑器保存是否允许。<paramref name="editorProfileId"/> = 打开文件时激活的档，空表示还没打开过文件。
    /// </summary>
    public static bool EditorSaveAllowed(string? editorProfileId, string activeProfileId, out string reason)
    {
        if (string.IsNullOrEmpty(editorProfileId)
            || string.Equals(editorProfileId, activeProfileId, StringComparison.Ordinal))
        {
            reason = "";
            return true;
        }
        reason = "服务器已切换（文件属于 " + editorProfileId + "，当前 " + activeProfileId + "）";
        return false;
    }

    /// <summary>切档后地图画布是否必须丢弃：画布上有图就说明那是上一台渲染的。</summary>
    public static bool MapMustBeForgotten(bool hasImage) => hasImage;

    /// <summary>切档后世界链是否必须重取：上一台的链条与当前服务器无关。</summary>
    public static bool ChainMustBeRefreshed(bool chainViewVisible) => chainViewVisible;
}
