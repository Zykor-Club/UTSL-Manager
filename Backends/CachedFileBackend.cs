namespace UTSLManager.Backends;

/// <summary>
/// 缓存装饰器：吸收原 MainWindow 内嵌在 Fs* 里的 FsMemo（TTL + 写操作全量失效）。
/// 把缓存从"每个方法里各写一遍"提到装饰器，实现里就只剩真实 IO。
/// </summary>
public sealed class CachedFileBackend(IFileBackend inner, int ttlMs = 90_000) : IFileBackend
{
    private sealed class Entry
    {
        public string? Text;
        public IReadOnlyList<FsEntry>? List;
        public bool? Bool;
        public long AtMs;
    }

    private readonly Dictionary<string, Entry> _memo = new();
    private readonly object _gate = new();

    public string Root => inner.Root;

    public bool IsRemote => inner.IsRemote;

    public IReadOnlyList<FsEntry> List(string dir)
    {
        string key = Key("list", dir);
        lock (_gate)
        {
            if (_memo.TryGetValue(key, out var m) && m.List is not null && Fresh(m)) { return m.List; }
        }
        var list = inner.List(dir);
        lock (_gate) { _memo[key] = new Entry { List = list, AtMs = Environment.TickCount64 }; }
        return list;
    }

    public bool Exists(string path)
    {
        string key = Key("exists", path);
        lock (_gate)
        {
            if (_memo.TryGetValue(key, out var m) && m.Bool is bool b && Fresh(m)) { return b; }
        }
        bool v = inner.Exists(path);
        lock (_gate) { _memo[key] = new Entry { Bool = v, AtMs = Environment.TickCount64 }; }
        return v;
    }

    public string ReadText(string path)
    {
        string key = Key("read", path);
        lock (_gate)
        {
            if (_memo.TryGetValue(key, out var m) && m.Text is not null && Fresh(m)) { return m.Text; }
        }
        string t = inner.ReadText(path);
        lock (_gate) { _memo[key] = new Entry { Text = t, AtMs = Environment.TickCount64 }; }
        return t;
    }

    public bool WriteText(string path, string text)
    {
        InvalidateCache();
        return inner.WriteText(path, text);
    }

    public bool CreateDirectory(string dir)
    {
        InvalidateCache();
        return inner.CreateDirectory(dir);
    }

    public bool Rename(string from, string to)
    {
        InvalidateCache();
        return inner.Rename(from, to);
    }

    public bool Delete(string path)
    {
        InvalidateCache();
        return inner.Delete(path);
    }

    public bool Upload(string localPath, string targetPath, Action<string>? progress = null, Func<bool>? cancelled = null)
    {
        InvalidateCache();
        return inner.Upload(localPath, targetPath, progress, cancelled);
    }

    public bool Download(string sourcePath, string localPath, long knownTotal = 0, Action<string>? progress = null, Func<bool>? cancelled = null)
        => inner.Download(sourcePath, localPath, knownTotal, progress, cancelled);

    /// <summary>
    /// 预热：把外部（例如 45 秒合并快照）已经取到的内容直接放进缓存，键格式与内部一致。
    /// ★ 存在的理由：快照本来就"一次 ssh 拿回所有常用数据"，原来却写进了 MainWindow 里一个
    ///   没有任何读取点的 FsMemo 死字典 → 页面读文件仍然各发一趟 ssh（约 1.6 s），快照白做。
    /// </summary>
    public void WarmText(string path, string text)
    {
        lock (_gate) { _memo[Key("read", path)] = new Entry { Text = text, AtMs = Environment.TickCount64 }; }
    }

    public void WarmList(string dir, IReadOnlyList<FsEntry> list)
    {
        lock (_gate) { _memo[Key("list", dir)] = new Entry { List = list, AtMs = Environment.TickCount64 }; }
    }

    public void WarmExists(string path, bool exists)
    {
        lock (_gate) { _memo[Key("exists", path)] = new Entry { Bool = exists, AtMs = Environment.TickCount64 }; }
    }

    /// <summary>清空缓存（替代原 FsInvalidate 的调用点）。</summary>
    public void InvalidateCache()
    {
        lock (_gate) { _memo.Clear(); }
    }

    private bool Fresh(Entry m) => Environment.TickCount64 - m.AtMs < ttlMs;

    private static string Key(string op, string path) => op + "|" + path.ToLowerInvariant();
}
