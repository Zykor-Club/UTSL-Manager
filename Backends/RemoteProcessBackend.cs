namespace UTSLManager.Backends;

/// <summary>远端服务端的进程操作（全部经 SSH）。</summary>
public sealed class RemoteProcessBackend(ISshTransport ssh, Func<string> startScript, Func<string> stopScript, string startArgs = "", string startEnv = "") : IProcessBackend
{
    /// <summary>本档的启动参数/环境变量（原样交给 agent 或远端启动脚本；空 = 不带参数）。</summary>
    public string StartArgs { get; } = (startArgs ?? "").Trim();
    public string StartEnv { get; } = (startEnv ?? "").Trim();

    public bool IsRemote => true;

    public Task<ProcessResult> StartAsync() => Task.Run(() =>
    {
        // ★ agent 在线就走它：无窗口启动 + stdout 可抓（不再需要控制台启动器）
        if (AgentClient.Alive)
        {
            string r = AgentClient.StartServer(StartArgs, StartEnv);
            if (r.Contains("✓")) { return new ProcessResult(true, r); }
        }
        try
        {
            string r = ssh.Run(startScript(), 60000);
            // ★ 启动脚本回的是 'wmi rc=N pid=M'：只有 rc==0 才算成功（原来无条件 Ok=true，WMI 失败也报成功）
            return new ProcessResult(r.Contains("rc=0"), r);
        }
        catch (Exception ex) { return new ProcessResult(false, "远端启动失败: " + ex.Message); }
    });

    public Task<ProcessResult> StopAsync() => Task.Run(() =>
    {
        if (AgentClient.Alive)
        {
            string r = AgentClient.StopServer();
            if (r.Contains("✓")) { return new ProcessResult(true, r); }
        }
        try
        {
            string r = ssh.Run(stopScript(), 40000);
            // ★ 停止脚本回 'remaining=N'：只有 remaining=0 才算停干净
            return new ProcessResult(r.Contains("remaining=0"), r);
        }
        catch (Exception ex) { return new ProcessResult(false, "远端停止失败: " + ex.Message); }
    });

    public string TrimMemory()
    {
        // ★ agent 在线就走 agent 的 /proc/trim：ssh 每次都起新的 powershell 进程，Add-Type 的类型
        //   不跨进程存活 → 那个"只编译一次"的守卫在 ssh 路径下其实无效（每次仍重新编译，秒级）。
        if (AgentClient.Alive)
        {
            string? t = AgentClient.TrimServerMemory();
            if (t is not null && t.Contains("\"ok\":true")) { return t; }
        }
        // 回落：ssh + Add-Type（同一会话内仍只编译一次）
        string ps = @"if (-not ('W.E' -as [type])) { $sig='[DllImport(""psapi.dll"")] public static extern bool EmptyWorkingSet(IntPtr hProcess);'; " +
                    "Add-Type -MemberDefinition $sig -Name E -Namespace W | Out-Null }; " +
                    "$t=[W.E]; $o=@(); " +
                    "foreach($p in Get-Process UnifierTSL -ErrorAction SilentlyContinue){ $b=$p.WorkingSet64; " +
                    "$null=$t::EmptyWorkingSet($p.Handle); Start-Sleep -Milliseconds 250; $p.Refresh(); " +
                    "$o += ([string][math]::Round($b/1MB) + ' -> ' + [string][math]::Round($p.WorkingSet64/1MB) + ' MB') }; " +
                    "if($o.Count -eq 0){'NORUN'} else {$o -join ' ; '}";
        string r = ssh.Run(ps, 90000);
        if (r.Trim() == "NORUN") { return "远端服务端没在运行"; }
        return "远端工作集已压缩: " + r.Trim() + " —— 注意：私有提交(commit)不变 ✗ 世界尺寸上限不会因此提高 ✓ 真正用途是【重操作前腾物理内存】✓";
    }

}
