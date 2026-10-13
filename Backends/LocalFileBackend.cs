namespace UTSLManager.Backends;

/// <summary>本机文件后端：直接走 System.IO。</summary>
public sealed class LocalFileBackend(string root) : IFileBackend
{
    public string Root { get; } = root;

    public bool IsRemote => false;

    public IReadOnlyList<FsEntry> List(string dir)
    {
        var list = new List<FsEntry>();
        try
        {
            if (!Directory.Exists(dir)) { return list; }
            foreach (string d in Directory.GetDirectories(dir))
            {
                var di = new DirectoryInfo(d);
                list.Add(new FsEntry(di.Name, true, 0, di.LastWriteTime));
            }
            foreach (string f in Directory.GetFiles(dir))
            {
                var fi = new FileInfo(f);
                list.Add(new FsEntry(fi.Name, false, fi.Length, fi.LastWriteTime));
            }
        }
        catch { }
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
