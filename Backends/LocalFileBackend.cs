namespace UTSLManager.Backends;

/// <summary>本机文件后端：直接走 System.IO。</summary>
public sealed class LocalFileBackend(string root) : IFileBackend
{
    public string Root { get; } = root;

    public bool IsRemote => false;

    public IReadOnlyList<FsEntry> List(string dir)
    {
        // ★ 一次枚举拿全：EnumerateFileSystemInfos 返回的 FileSystemInfo 已经带着枚举时取得的
        //   Name / Length / LastWriteTime，不再对每个条目 new DirectoryInfo/FileInfo 去查第二遍元数据
        //   （上千条目录时那是上千次多余的系统调用）。
        //   语义与原实现一致：目录在前、文件在后；目录 Size 恒为 0；不额外过滤隐藏/系统项。
        var list = new List<FsEntry>();
        var dirs = new List<FsEntry>();
        var files = new List<FsEntry>();
        try
        {
            var dirInfo = new DirectoryInfo(dir);
            if (!dirInfo.Exists) { return list; }
            foreach (FileSystemInfo e in dirInfo.EnumerateFileSystemInfos())
            {
                if (e is FileInfo f) { files.Add(new FsEntry(f.Name, false, f.Length, e.LastWriteTime)); }
                else { dirs.Add(new FsEntry(e.Name, true, 0, e.LastWriteTime)); }
            }
        }
        catch { }
        list.AddRange(dirs);      // 保持原来的"目录在前、文件在后"排序
        list.AddRange(files);
        return list;
    }

    public bool Exists(string path)
    {
        try { return File.Exists(path) || Directory.Exists(path); }
        catch { return false; }
    }

    public string ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
        catch { return ""; }
    }

    public bool WriteText(string path, string text)
    {
        try { File.WriteAllText(path, text); return true; }
        catch { return false; }
    }

    public bool CreateDirectory(string dir)
    {
        try { Directory.CreateDirectory(dir); return true; }
        catch { return false; }
    }

    public bool Rename(string from, string to)
    {
        try
        {
            // 原实现只调 File.Move，对目录会抛异常；这里补上目录分支
            if (Directory.Exists(from)) { Directory.Move(from, to); }
            else { File.Move(from, to); }
            return true;
        }
        catch { return false; }
    }

    public bool Delete(string path)
    {
        try
        {
            if (Directory.Exists(path)) { Directory.Delete(path, true); }
            else { File.Delete(path); }
            return true;
        }
        catch { return false; }
    }

    public bool Upload(string localPath, string targetPath, Action<string>? progress = null, Func<bool>? cancelled = null)
    {
        try { File.Copy(localPath, targetPath, overwrite: true); progress?.Invoke("本机模式：直接复制完成"); return true; }
        catch { return false; }
    }

    public bool Download(string sourcePath, string localPath, long knownTotal = 0, Action<string>? progress = null, Func<bool>? cancelled = null)
    {
        try { File.Copy(sourcePath, localPath, overwrite: true); progress?.Invoke("本机模式：直接复制完成"); return true; }
        catch { return false; }
    }
}
