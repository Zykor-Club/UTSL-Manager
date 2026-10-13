namespace UTSLManager.Backends;

/// <summary>文件/目录条目。</summary>
public sealed record FsEntry(string Name, bool IsDir, long Size, DateTime Mtime);

/// <summary>PowerShell 单引号字符串转义。</summary>
internal static class Ps
{
    internal static string Q(string s) => s.Replace("'", "''");
}

/// <summary>
/// 远端通道：命令 + 传输。由调用方实现（复用既有 SshRun / scp 代码，行为不变）。
/// 传输带进度与取消回调 —— 进度文案由实现方给出，调用方只负责显示。
/// </summary>
public interface ISshTransport
{
    string Host { get; }
    int Port { get; }
    string User { get; }
    string KeyPath { get; }

    /// <summary>在远端执行 PowerShell 脚本并返回 stdout。</summary>
    string Run(string psScript, int timeoutMs = 30000);

    /// <summary>本地 → 远端（远端路径用 /C:/... 形式）。</summary>
    bool CopyToRemote(string localPath, string remotePath, Action<string>? progress = null, Func<bool>? cancelled = null);

    /// <summary>远端 → 本地。knownTotal 仅用于显示剩余时间。</summary>
    bool CopyFromRemote(string remotePath, string localPath, long knownTotal = 0, Action<string>? progress = null, Func<bool>? cancelled = null);
}

/// <summary>
/// 文件操作后端：本地直连与远端（SSH）各一个实现。
/// 约定：所有方法都不抛异常，失败以返回值表达。
/// </summary>
public interface IFileBackend
{
    /// <summary>该环境下的文件根目录（替代原 FsRoot）。</summary>
    string Root { get; }

    bool IsRemote { get; }

    IReadOnlyList<FsEntry> List(string dir);

    bool Exists(string path);

    /// <summary>读取文本；失败返回空串。</summary>
    string ReadText(string path);

    bool WriteText(string path, string text);

    bool CreateDirectory(string dir);

    bool Rename(string from, string to);

    bool Delete(string path);

    /// <summary>本地路径 → 该环境的路径。</summary>
    bool Upload(string localPath, string targetPath, Action<string>? progress = null, Func<bool>? cancelled = null);

    /// <summary>该环境的路径 → 本地路径。knownTotal 仅用于显示剩余时间。</summary>
    bool Download(string sourcePath, string localPath, long knownTotal = 0, Action<string>? progress = null, Func<bool>? cancelled = null);
}
