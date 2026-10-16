using System.Diagnostics;

namespace UTSLManager.Backends;

/// <summary>
/// 远端文件后端：命令走 ISshTransport.Run，传输走 ISshTransport.Copy*。
/// 与既有 Fs* 实现一一对应（含 base64 分块写与"临时文件 + 改名"的原子写）。
/// </summary>
public sealed class RemoteFileBackend(ISshTransport ssh, string root) : IFileBackend
{
    /// <summary>
    /// EncodedCommand 有命令行长度上限（base64(UTF-16(script)) ≈ 2.7 倍膨胀，CreateProcess 上限 32767 字符），
    /// 写入按此大小分块追加。★ 原来是 1400：写 5KB 配置要 5 趟 ssh（约 8 秒），100KB 要 98 趟（约 2.6 分钟）；
    /// 提到 8000 后 5KB 只需 1 趟。（更大文件的正确做法是走 Upload 的 scp/agent 通道，见 TODO。）
    /// </summary>
    private const int WriteChunk = 8000;

    /// <summary>
    /// 写操作统一入口：脚本必须输出 OK 或 ERR:原因，方法按真实结果返回。
    /// ★ 原来四个写方法都是「跑完 ssh.Run 就 return true」——远端失败（权限/占用/路径错）时界面会谎报成功。
    /// </summary>
    private bool RunWrite(string script, int timeoutMs)
    {
        string r = ssh.Run(Quiet + " $ErrorActionPreference='Stop'; try { " + script + " 'OK' } catch { 'ERR:' + $_.Exception.Message }", timeoutMs);
        return !r.Contains("ERR:") && r.Contains("OK");
    }

    private const string Quiet = "$ProgressPreference='SilentlyContinue';";

    public string Root { get; } = root;

    public bool IsRemote => true;

    public IReadOnlyList<FsEntry> List(string dir)
    {
        var list = new List<FsEntry>();
        string outp = ssh.Run(Quiet + "[Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
            "Get-ChildItem -LiteralPath '" + Ps.Q(dir) + "' -ErrorAction SilentlyContinue | ForEach-Object { $(if ($_.PSIsContainer) { 'D' } else { 'F' }) + '|' + $_.Length + '|' + $_.LastWriteTime.ToString('o') + '|' + $_.Name }", 25000);
        foreach (string ln in outp.Split('\n'))
        {
            var parts = ln.Trim().Split('|', 4);
            if (parts.Length < 4) { continue; }
            long size = long.TryParse(parts[1], out long s) ? s : 0;
            DateTime mt = DateTime.TryParse(parts[2], out DateTime m) ? m : DateTime.MinValue;
            list.Add(new FsEntry(parts[3], parts[0] == "D", size, mt));
        }
        return list;
    }

    public bool Exists(string path)
        => ssh.Run(Quiet + " if (Test-Path -LiteralPath '" + Ps.Q(path) + "') { 'YES' } else { 'NO' }", 20000).Contains("YES");

    public string ReadText(string path)
    {
        string b64 = ssh.Run(Quiet + "[Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
            "if (Test-Path -LiteralPath '" + Ps.Q(path) + "') { [Convert]::ToBase64String([IO.File]::ReadAllBytes('" + Ps.Q(path) + "')) }", 25000).Trim();
        if (b64.Length == 0) { return ""; }
        try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
        catch { return ""; }
    }

    public bool WriteText(string path, string text)
    {
        string b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));
        string tmp = path + ".tmp";
        // 先写临时文件，全部写完再改名覆盖：中途失败不会留下半个配置文件
        ssh.Run(Quiet + " [IO.File]::WriteAllBytes('" + Ps.Q(tmp) + "', [Convert]::FromBase64String('" + b64.Substring(0, Math.Min(WriteChunk, b64.Length)) + "'))", 30000);
        for (int i = WriteChunk; i < b64.Length; i += WriteChunk)
        {
            string chunk = b64.Substring(i, Math.Min(WriteChunk, b64.Length - i));
            ssh.Run(Quiet + " $p='" + Ps.Q(tmp) + "'; $b=[IO.File]::ReadAllBytes($p); [IO.File]::WriteAllBytes($p, $b + [Convert]::FromBase64String('" + chunk + "'))", 30000);
        }
        // 三步（首块/追加块/改名）任一失败都算失败
        if (b64.Length <= WriteChunk)
        {
            if (!RunWrite("[IO.File]::WriteAllBytes('" + Ps.Q(tmp) + "', [Convert]::FromBase64String('" + b64 + "'));", 30000)) { return false; }
        }
        else
        {
            if (!RunWrite("[IO.File]::WriteAllBytes('" + Ps.Q(tmp) + "', [Convert]::FromBase64String('" + b64.Substring(0, WriteChunk) + "'));", 30000)) { return false; }
            for (int i = WriteChunk; i < b64.Length; i += WriteChunk)
            {
                string chunk = b64.Substring(i, Math.Min(WriteChunk, b64.Length - i));
                if (!RunWrite("$p='" + Ps.Q(tmp) + "'; $b=[IO.File]::ReadAllBytes($p); [IO.File]::WriteAllBytes($p, $b + [Convert]::FromBase64String('" + chunk + "'));", 30000)) { return false; }
            }
        }
        return RunWrite("Move-Item -LiteralPath '" + Ps.Q(tmp) + "' -Destination '" + Ps.Q(path) + "' -Force;", 30000);
    }

    public bool CreateDirectory(string dir)
        => RunWrite("New-Item -ItemType Directory -Force -Path '" + Ps.Q(dir) + "' | Out-Null;", 20000);

    public bool Rename(string from, string to)
        => RunWrite("Move-Item -LiteralPath '" + Ps.Q(from) + "' -Destination '" + Ps.Q(to) + "';", 20000);

    public bool Delete(string path)
        => RunWrite("Remove-Item -LiteralPath '" + Ps.Q(path) + "' -Recurse -Force;" +
                    // 复核：Remove-Item 对某些占用/只读情况不抛错也不删，所以再问一次
                    "if (Test-Path -LiteralPath '" + Ps.Q(path) + "') { throw '删除后仍存在' }", 30000);

    public bool Upload(string localPath, string targetPath, Action<string>? progress = null, Func<bool>? cancelled = null)
    {
        // ★ agent 在线就走它：进度是真实字节数（scp 不回传进度，只能估计）；失败自动回落 scp
        if (AgentClient.Alive)
        {
            progress?.Invoke("经 agent 上传（真实进度）…");
            if (AgentClient.Upload(localPath, targetPath, progress, cancelled)) { return true; }
            progress?.Invoke("agent 上传未成功，回落到 scp…");
        }
        return ssh.CopyToRemote(localPath, targetPath, progress, cancelled);
    }

    public bool Download(string sourcePath, string localPath, long knownTotal = 0, Action<string>? progress = null, Func<bool>? cancelled = null)
    {
        // ★ 下载【故意】不用 agent：实测 agent 0.67 MB/s vs scp 1.31 MB/s（同样的隧道、同样的 RTT），
        //   而大文件（世界文件 24~72 MB）正是最吃吞吐的场景。scp 的进度是按本地文件大小估算的，
        //   显示足够用；agent 的价值在【上传】的真实进度与取消（scp 上传同样只能估）。
        return ssh.CopyFromRemote(sourcePath, localPath, knownTotal, progress, cancelled);
    }
}
