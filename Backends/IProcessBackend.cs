namespace UTSLManager.Backends;

/// <summary>一次进程操作的结果；Message 可直接显示给用户。</summary>
public readonly record struct ProcessResult(bool Ok, string Message);

/// <summary>被管服务端的进程级操作。</summary>
public interface IProcessBackend
{
    bool IsRemote { get; }

    bool IsAlive();

    Task<ProcessResult> StartAsync();

    Task<ProcessResult> StopAsync();

    /// <summary>压缩服务端工作集；返回可直接显示的结果文案。</summary>
    string TrimMemory();

}
