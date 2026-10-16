using System.Diagnostics;

namespace UTSLManager.Backends;

/// <summary>
/// 远端文件后端：命令走 ISshTransport.Run，传输走 ISshTransport.Copy*。
/// 与既有 Fs* 实现一一对应（含 base64 分块写与"临时文件 + 改名"的原子写）。
/// </summary>
public sealed class RemoteFileBackend(ISshTransport ssh, string root) : IFileBackend
{
    /// <summary>EncodedCommand 有命令行长度上限，写入按此大小分块追加。</summary>
    private const int WriteChunk = 1400;

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
        ssh.Run(Quiet + " Move-Item -LiteralPath '" + Ps.Q(tmp) + "' -Destination '" + Ps.Q(path) + "' -Force; 'ok'", 30000);
        return true;
    }

    public bool CreateDirectory(string dir)
    {
        ssh.Run(Quiet + " New-Item -ItemType Directory -Force -Path '" + Ps.Q(dir) + "' | Out-Null; 'ok'", 20000);
        return true;
    }

    public bool Rename(string from, string to)
    {
        ssh.Run(Quiet + " Move-Item -LiteralPath '" + Ps.Q(from) + "' -Destination '" + Ps.Q(to) + "'; 'ok'", 20000);
        return true;
    }

    public bool Delete(string path)
    {
        ssh.Run(Quiet + " Remove-Item -LiteralPath '" + Ps.Q(path) + "' -Recurse -Force -ErrorAction SilentlyContinue; 'ok'", 30000);
        return true;
    }

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
