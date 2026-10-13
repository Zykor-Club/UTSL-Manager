namespace UTSLManager.Backends;

/// <summary>
/// 环境相关的路径与文案。把"远端/本机"的分叉收在一处，
/// 避免同一类判断散落在 UI 代码里（漏掉一处就是一个 bug）。
/// </summary>
public interface IEnvironmentInfo
{
    bool IsRemote { get; }

    /// <summary>文件根目录。</summary>
    string FileRoot { get; }

    /// <summary>地图 PNG 所在目录。</summary>
    string MapDir { get; }

    /// <summary>地图下载与缓存目录；本机模式直接等于地图目录。</summary>
    string MapCacheDir { get; }

    /// <summary>统一产出「远端 x」或「本机 x」。</summary>
    string Describe(string what);
}

/// <summary>本机环境。</summary>
public sealed class LocalEnvironmentInfo(string fileRoot, string mapDir) : IEnvironmentInfo
{
    public bool IsRemote => false;

    public string FileRoot { get; } = fileRoot;

    public string MapDir { get; } = mapDir;

    /// <summary>本机没有"下载到缓存"这一步，就地使用地图目录。</summary>
    public string MapCacheDir => MapDir;

    public string Describe(string what) => "本机 " + what;
}

/// <summary>远端环境。</summary>
public sealed class RemoteEnvironmentInfo(string fileRoot, string mapDir, string mapCacheDir) : IEnvironmentInfo
{
    public bool IsRemote => true;

    public string FileRoot { get; } = fileRoot;

    public string MapDir { get; } = mapDir;

    public string MapCacheDir { get; } = mapCacheDir;

    public string Describe(string what) => "远端 " + what;
}
