using System.Diagnostics;

namespace UTSLManager.Backends;

/// <summary>
/// 远端文件后端：命令走 ISshTransport.Run，传输走 ISshTransport.Copy*。
/// 与既有 Fs* 实现一一对应（含 base64 分块写与"临时文件 + 改名"的原子写）。
/// </summary>
public sealed class RemoteFileBackend(ISshTransport ssh, string root) : IFileBackend
{
    /// <summary>
    /// 每次 ssh 写入携带的 base64 字符数。★ 这个值**不能随便调大** —— 实测（2026-10-16，远端 43.249.195.13）：
    ///   EncodedCommand 长度 = 脚本 × 约 2.67，而**远端 cmd.exe / CreateProcess 的 8191 硬上限**会直接拒绝：
    ///     base64 2800 → Encoded 7728 → OK
    ///     base64 3000 → Encoded 8260 → 远端报「命令行太长。」
    ///   之前这里被"优化"成 8000（只算到本地 32767 上限），导致 EncodedCommand ≈ 22K，
    ///   **凡是最大那块超过约 2.7K base64（即任何 > 2KB 的文件）在 SSH 模式下都写不进去** —— 包括 4.7KB 的 TShock 配置。
    ///   2400 是留足余量的实测安全值（Encoded ≈ 7.3K）。
    ///   （更大文件的正确做法是走 Upload 的 scp/agent 通道，见 TODO。）
    /// </summary>
    private const int WriteChunk = 2400;

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

    /// <summary>超过这个字节数改走 scp 通道（见 WriteText 的说明）。</summary>
    private const int LargeWriteBytes = 4096;

    /// <summary>Windows 路径 → scp 远端路径形式（与管理器 ScpPath 同一约定：C:\a → /C:/a）。</summary>
    private static string ScpPath(string winPath)
    {
        string p = winPath.Replace('\\', '/');
        return (p.Length > 1 && p[1] == ':') ? "/" + p : p;
    }

    public bool WriteText(string path, string text)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
        string tmp = path + ".tmp";

        // ★ 大文件走 scp：远端 cmd.exe 有 8191 的命令行上限（实测），所以分块写每趟只能带 2400 base64 ≈ 1.8KB，
        //   而链路 RTT 实测约 1.67 秒 → 100KB 要 55 趟 ≈ 90 秒。编辑器保存 20KB 配置就会"卡住"十几秒。
        //   改为：本地写临时文件 → scp 到远端同目录的 .tmp → 远端原子改名（仍是原子替换，失败不留半个文件）。
        if (bytes.Length > LargeWriteBytes)
        {
            // ★ 本地临时文件要有退路：%TEMP% 在受限账户 / 重定向的配置文件 / 沙箱里可能不可写 ——
            //   实测（探针进程）会抛 UnauthorizedAccessException。IFileBackend 的契约是"不抛异常，失败以返回值表达"，
            //   所以：先试 %TEMP%，失败退回"程序自己的 tmp/"，两处都不行就 return false。整段再包一层 try/catch 兜住 ssh 侧的异常。
            string? localTmp = null;
            try
            {
                foreach (string dir in new[] { Path.GetTempPath(), Path.Combine(AppContext.BaseDirectory, "tmp") })
                {
                    try
                    {
                        Directory.CreateDirectory(dir);
                        string p = Path.Combine(dir, "utsl-wr-" + Guid.NewGuid().ToString("N") + ".tmp");
                        File.WriteAllBytes(p, bytes);
                        localTmp = p;
                        break;
                    }
                    catch { }
                }
                if (localTmp is null) { return false; }   // 两处都写不了 → 如实失败
                if (!ssh.CopyToRemote(localTmp, ScpPath(tmp)))
                {
                    try { ssh.Run(Quiet + " Remove-Item -LiteralPath '" + Ps.Q(tmp) + "' -Force -ErrorAction SilentlyContinue", 15000); } catch { }
                    return false;
                }
                return RunWrite("Move-Item -LiteralPath '" + Ps.Q(tmp) + "' -Destination '" + Ps.Q(path) + "' -Force;", 30000);
            }
            catch { return false; }
            finally { if (localTmp is not null) { try { File.Delete(localTmp); } catch { } } }
        }

        string b64 = Convert.ToBase64String(bytes);
        // ★ E2（审查报告）：这里原来有一整段"先写一遍 + 逐块追加"的死代码（返回值全丢），
        //   紧接着又执行一遍真正的写入 —— 等于每次写文件都发两遍 ssh。已删除。
        //   追加块也从 `$b=[IO.File]::ReadAllBytes($p); WriteAllBytes($p, $b + 新块)`（O(n²)、每块重读整个文件）
        //   改成 FileStream 追加（O(n)），PowerShell 5.1 / .NET 9 都支持。
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
                // ★ E2：FileStream 追加，避免每块都把整个文件读回内存再拼一个新数组
                if (!RunWrite("$p='" + Ps.Q(tmp) + "'; $fs=[IO.File]::Open($p,[IO.FileMode]::Append); $bb=[Convert]::FromBase64String('" + chunk + "'); $fs.Write($bb,0,$bb.Length); $fs.Close();", 30000)) { return false; }
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
