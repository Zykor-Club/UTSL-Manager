namespace UTSLManager.Backends;

/// <summary>远端服务端的进程操作（全部经 SSH）。</summary>
public sealed class RemoteProcessBackend(ISshTransport ssh, Func<string> startScript, Func<string> stopScript) : IProcessBackend
{
    public bool IsRemote => true;

    /// <summary>远端存活由合并快照的进程数判断，这里不做单独探测。</summary>
    public bool IsAlive() => true;

    public Task<ProcessResult> StartAsync() => Task.Run(() =>
    {
        // ★ agent 在线就走它：无窗口启动 + stdout 可抓（不再需要控制台启动器）
        if (AgentClient.Alive)
        {
            string r = AgentClient.StartServer();
            if (r.Contains("✓")) { return new ProcessResult(true, r); }
        }
        try { return new ProcessResult(true, ssh.Run(startScript(), 60000)); }
        catch (Exception ex) { return new ProcessResult(false, "远端启动失败: " + ex.Message); }
    });

    public Task<ProcessResult> StopAsync() => Task.Run(() =>
    {
        if (AgentClient.Alive)
        {
            string r = AgentClient.StopServer();
            if (r.Contains("✓")) { return new ProcessResult(true, r); }
        }
        try { return new ProcessResult(true, ssh.Run(stopScript(), 40000)); }
        catch (Exception ex) { return new ProcessResult(false, "远端停止失败: " + ex.Message); }
    });

    public string TrimMemory()
    {
        string ps = @"$sig='[DllImport(""psapi.dll"")] public static extern bool EmptyWorkingSet(IntPtr hProcess);'; " +
                    "$t=Add-Type -MemberDefinition $sig -Name E -Namespace W -PassThru; $o=@(); " +
                    "foreach($p in Get-Process UnifierTSL -ErrorAction SilentlyContinue){ $b=$p.WorkingSet64; " +
                    "$null=$t::EmptyWorkingSet($p.Handle); Start-Sleep -Milliseconds 250; $p.Refresh(); " +
                    "$o += ([string][math]::Round($b/1MB) + ' -> ' + [string][math]::Round($p.WorkingSet64/1MB) + ' MB') }; " +
                    "if($o.Count -eq 0){'NORUN'} else {$o -join ' ; '}";
        string r = ssh.Run(ps, 90000);
        if (r.Trim() == "NORUN") { return "远端服务端没在运行"; }
        return "远端工作集已压缩: " + r.Trim() + " —— 注意：私有提交(commit)不变 ✗ 世界尺寸上限不会因此提高 ✓ 真正用途是【重操作前腾物理内存】✓";
    }

}
