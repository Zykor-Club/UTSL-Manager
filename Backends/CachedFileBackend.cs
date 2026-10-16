namespace UTSLManager.Backends;

/// <summary>
/// 缓存装饰器：吸收原 MainWindow 内嵌在 Fs* 里的 FsMemo（TTL + 写操作失效）。
/// 把缓存从"每个方法里各写一遍"提到装饰器，实现里就只剩真实 IO。
///
/// ★ 本轮改动（代码审查发现，远端每次未命中就是一趟 ssh，约 1.6 秒）：
///   1) 写操作【定向失效】而不是全量清空 —— 原来改一个文件就让所有页面重走 ssh，90 秒 TTL 等于白设；
///   2) 读路径顺手淘汰过期项 + 容量上限 —— 原来只增不减（长时间浏览文件页会一直吃内存）；
///   3) 同键【单飞】：未命中时在该键自己的锁里再查一次，避免两个页面同时首读同一目录各打一趟 ssh；
///   4) Upload 结束后【再失效一次】—— 原来只在开始前失效，上传几十秒期间回填的旧列表会存活到 TTL 到期。
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
    private readonly Dictionary<string, object> _keyGates = new();
    private readonly object _gate = new();
    private const int MaxEntries = 512;

    public string Root => inner.Root;

    public bool IsRemote => inner.IsRemote;

    public IReadOnlyList<FsEntry> List(string dir)
    {
        string key = Key("list", dir);
        if (TryGet(key, m => m.List, out IReadOnlyList<FsEntry>? hit)) { return hit!; }
        lock (GateFor(key))
        {
            if (TryGet(key, m => m.List, out hit)) { return hit!; }        // 单飞：同键并发只打一趟真实 IO
            var list = inner.List(dir);
            Put(key, new Entry { List = list, AtMs = Environment.TickCount64 });
            return list;
        }
    }

    public bool Exists(string path)
    {
        string key = Key("exists", path);
        if (TryGetBool(key, out bool hit)) { return hit; }
        lock (GateFor(key))
        {
            if (TryGetBool(key, out hit)) { return hit; }
            bool v = inner.Exists(path);
            Put(key, new Entry { Bool = v, AtMs = Environment.TickCount64 });
            return v;
        }
    }

    public string ReadText(string path)
    {
        string key = Key("read", path);
        if (TryGet(key, m => m.Text, out string? hit)) { return hit!; }
        lock (GateFor(key))
        {
            if (TryGet(key, m => m.Text, out hit)) { return hit!; }
            string t = inner.ReadText(path);
            Put(key, new Entry { Text = t, AtMs = Environment.TickCount64 });
            return t;
        }
    }

    public bool WriteText(string path, string text)
    {
        bool r = inner.WriteText(path, text);
        InvalidatePath(path);
        return r;
    }

    public bool CreateDirectory(string dir)
    {
        bool r = inner.CreateDirectory(dir);
        InvalidateDir(dir);
        return r;
    }

    public bool Rename(string from, string to)
    {
        bool r = inner.Rename(from, to);
        InvalidatePath(from);
        InvalidatePath(to);
        return r;
    }

    public bool Delete(string path)
    {
        bool r = inner.Delete(path);
        InvalidatePath(path);
        return r;
    }

    public bool Upload(string localPath, string targetPath, Action<string>? progress = null, Func<bool>? cancelled = null)
    {
        InvalidatePath(targetPath);                       // 开始前：清掉旧值
        bool r = inner.Upload(localPath, targetPath, progress, cancelled);
        InvalidatePath(targetPath);                       // ★ 结束后再清一次：上传期间的读取会把旧列表灌回来
        return r;
    }

    public bool Download(string sourcePath, string localPath, long knownTotal = 0, Action<string>? progress = null, Func<bool>? cancelled = null)
        => inner.Download(sourcePath, localPath, knownTotal, progress, cancelled);

    /// <summary>
    /// 预热：把外部（例如 45 秒合并快照）已经取到的内容直接放进缓存，键格式与内部一致。
    /// ★ 存在的理由：快照本来就"一次 ssh 拿回所有常用数据"，原来却写进了 MainWindow 里一个
    ///   没有任何读取点的 FsMemo 死字典 → 页面读文件仍然各发一趟 ssh（约 1.6 s），快照白做。
    /// </summary>
    public void WarmText(string path, string text) => Put(Key("read", path), new Entry { Text = text, AtMs = Environment.TickCount64 });

    public void WarmList(string dir, IReadOnlyList<FsEntry> list) => Put(Key("list", dir), new Entry { List = list, AtMs = Environment.TickCount64 });

    public void WarmExists(string path, bool exists) => Put(Key("exists", path), new Entry { Bool = exists, AtMs = Environment.TickCount64 });

    /// <summary>定点失效：某路径自身的 read/exists + 它所在目录的 list（写文件后调这个就够）。</summary>
    public void InvalidatePath(string path)
    {
        string p = path.Replace('/', '\\');
        string dir = DirOf(p);
        lock (_gate)
        {
            _memo.Remove(Key("read", p));
            _memo.Remove(Key("exists", p));
            _memo.Remove(Key("list", dir));
        }
    }

    /// <summary>定点失效：只清某目录的列表缓存。</summary>
    public void InvalidateDir(string dir)
    {
        lock (_gate) { _memo.Remove(Key("list", dir)); }
    }

    /// <summary>清空缓存（逃生口；FsInvalidate 在极端情况下用）。</summary>
    public void InvalidateCache()
    {
        lock (_gate) { _memo.Clear(); }
    }

    private bool TryGetBool(string key, out bool value)
    {
        lock (_gate)
        {
            if (_memo.TryGetValue(key, out Entry? m) && m.Bool is bool b && Fresh(m)) { value = b; return true; }
        }
        value = false;
        return false;
    }

    private bool TryGet<T>(string key, Func<Entry, T?> pick, out T? value) where T : class
    {
        lock (_gate)
        {
            if (_memo.TryGetValue(key, out Entry? m))
            {
                T? v = pick(m);
                if (v is not null && Fresh(m)) { value = v; return true; }
            }
        }
        value = null;
        return false;
    }

    private void Put(string key, Entry e)
    {
        lock (_gate)
        {
            if (_memo.Count >= MaxEntries) { PruneLocked(); }
            _memo[key] = e;
        }
    }

    /// <summary>淘汰过期项；若全都没过期仍然满，就整体清掉（宁可重取，也不无限涨）。调用方已持 _gate。</summary>
    private void PruneLocked()
    {
        long now = Environment.TickCount64;
        var dead = new List<string>();
        foreach (var kv in _memo) { if (now - kv.Value.AtMs >= ttlMs) { dead.Add(kv.Key); } }
        foreach (string k in dead) { _memo.Remove(k); }
        if (_memo.Count >= MaxEntries) { _memo.Clear(); }
    }

    private object GateFor(string key)
    {
        lock (_gate)
        {
            if (!_keyGates.TryGetValue(key, out object? g)) { g = new object(); _keyGates[key] = g; }
            return g;
        }
    }

    private bool Fresh(Entry m) => Environment.TickCount64 - m.AtMs < ttlMs;

    private static string DirOf(string p)
    {
        int i = p.LastIndexOf('\\');
        return i > 0 ? p.Substring(0, i) : p;
    }

    // 统一把 / 归一成 \、并小写：否则 C:/x/y 与 C:\x\y 会被当成两个键（定向失效也会对不上）
    private static string Key(string op, string path) => op + "|" + path.Replace('/', '\\').ToLowerInvariant();
}
