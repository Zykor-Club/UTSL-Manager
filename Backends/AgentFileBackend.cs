namespace UTSLManager.Backends;

/// <summary>
/// 直连模式（不经 SSH）的文件后端：全部走 agent 的 HTTP 端点（/files/* 与 /fs/*）。
/// ★ 与 RemoteFileBackend 的区别：不需要 ssh，靠 agent 的令牌认证；
///   路径按【agent 部署根】下的绝对/相对路径解析，越界由 agent 侧 ResolveSafe 拒绝。
/// </summary>
public sealed class AgentFileBackend(string root) : IFileBackend
{
    public string Root => root.Length > 0 ? root : "/";

    public bool IsRemote => true;

    public IReadOnlyList<FsEntry> List(string dir)
    {
        var list = new List<FsEntry>();
        // ★ B 子代理的观察：agent 的 ResolveSafe 把 "/" 当盘根判越界，所以列【根】必须传空串。
        //   管理器有时会用 "/"（例如 root 还没从 /ping 取回来时的占位值），这里统一映射成空串。
        if (dir is "/" or "\\" or "") { dir = ""; }
        string? s = AgentClient.GetText("/files/list?path=" + Uri.EscapeDataString(dir));
        if (s is null) { return list; }
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(s);
            if (!doc.RootElement.TryGetProperty("entries", out var arr)) { return list; }
            foreach (var e in arr.EnumerateArray())
            {
                string name = e.GetProperty("name").GetString() ?? "";
                bool isDir = e.GetProperty("dir").GetBoolean();
                long size = e.GetProperty("size").GetInt64();
                long mtime = e.GetProperty("mtime").GetInt64();
                DateTime mt = mtime > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(mtime).LocalDateTime : DateTime.MinValue;
                list.Add(new FsEntry(name, isDir, size, mt));
            }
        }
        catch { }
        return list;
    }

    public bool Exists(string path)
    {
        string? s = AgentClient.GetText("/fs/exists?path=" + Uri.EscapeDataString(path));
        return s is not null && s.Contains("\"exists\":true");
    }

    public string ReadText(string path)
    {
        string? s = AgentClient.GetText("/fs/read?path=" + Uri.EscapeDataString(path));
        if (s is null) { return ""; }
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(s);
            if (doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean()
                && doc.RootElement.TryGetProperty("text", out var t)) { return t.GetString() ?? ""; }
        }
        catch { }
        return "";
    }

    public bool WriteText(string path, string text)
        => AgentClient.PutBytes(System.Text.Encoding.UTF8.GetBytes(text), path);

    // ★ 破坏性操作走 POST：daemon 端已禁止这些端点用 GET（防跨站 GET 触发删除/改名）
    public bool CreateDirectory(string dir)
        => Ok(AgentClient.PostText("/fs/mkdir?path=" + Uri.EscapeDataString(dir)));

    public bool Rename(string from, string to)
        => Ok(AgentClient.PostText("/fs/rename?path=" + Uri.EscapeDataString(from) + "&to=" + Uri.EscapeDataString(to)));

    public bool Delete(string path)
        => Ok(AgentClient.PostText("/files/delete?path=" + Uri.EscapeDataString(path)));

    public bool Upload(string localPath, string targetPath, Action<string>? progress = null, Func<bool>? cancelled = null)
        => AgentClient.Alive && AgentClient.Upload(localPath, targetPath, progress, cancelled);

    public bool Download(string sourcePath, string localPath, long knownTotal = 0, Action<string>? progress = null, Func<bool>? cancelled = null)
        => AgentClient.Alive && AgentClient.Download(sourcePath, localPath, knownTotal, progress, cancelled);

    private static bool Ok(string? s) => s is not null && s.Contains("\"ok\":true");
}
