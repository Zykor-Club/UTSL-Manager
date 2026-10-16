using System.Net.Sockets;

namespace UTSLManager.Backends;

/// <summary>
/// 云端 agent（UTSLDaemon）的文件通道：走**已有的 SSH 隧道**，不是 scp。
/// 与 scp 的差别：进度是真实字节数（scp 不回传进度，只能按本地文件大小+时间估），
/// 且不必为每个文件再付一次 ssh 握手。agent 不可用时调用方回落到 scp（见 RemoteFileBackend）。
/// 只有回环/隧道可达，路径越界由 agent 侧拒绝。
/// </summary>
public static class AgentClient
{
    /// <summary>由 MainWindow 的 agent 探活循环更新。</summary>
    public static volatile bool Alive;
    public static volatile string Base = "";

    static TcpClient Connect(out NetworkStream ns)
    {
        var uri = new Uri(Base);
        var t = new TcpClient { NoDelay = true };
        // ★ 同 agent 侧：链路 RTT 约 200 ms，缓冲太小会把吞吐压到 ~0.3 MB/s
        try { t.ReceiveBufferSize = 4 * 1024 * 1024; t.SendBufferSize = 4 * 1024 * 1024; } catch { }
        t.Connect(uri.Host, uri.Port);
        ns = t.GetStream();
        return t;
    }

    static string HostHeader => new Uri(Base).Authority;

    /// <summary>上传：PUT 原始字节。progress 收到「已发送/总量 · 速度 · 剩余」。</summary>
    public static bool Upload(string localPath, string remoteRel, Action<string>? progress, Func<bool>? cancelled)
    {
        try
        {
            var fi = new FileInfo(localPath);
            using var t = Connect(out var ns);
            string head = "PUT /files/put?path=" + Uri.EscapeDataString(remoteRel) + " HTTP/1.1\r\n"
                        + "Host: " + HostHeader + "\r\n"
                        + "Content-Length: " + fi.Length + "\r\n"
                        + "Connection: close\r\n\r\n";
            ns.Write(System.Text.Encoding.ASCII.GetBytes(head));
            var buf = new byte[256 * 1024];
            long sent = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using (var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    if (cancelled?.Invoke() == true) { t.Close(); progress?.Invoke("已取消"); return false; }
                    ns.Write(buf, 0, n);
                    sent += n;
                    if (progress is not null)
                    {
                        double sec = Math.Max(0.001, sw.Elapsed.TotalSeconds);
                        double mbps = sent / 1048576.0 / sec;
                        long left = Math.Max(0, fi.Length - sent);
                        double eta = mbps > 0.01 ? left / 1048576.0 / mbps : 0;
                        progress($"{sent / 1048576.0:F1}/{fi.Length / 1048576.0:F1} MB · {mbps:F1} MB/s · 剩余约 {eta:F0}s");
                    }
                }
            }
            ns.Flush();
            return ReadResponseLine(ns).Contains("200");
        }
        catch (Exception ex) { progress?.Invoke("上传失败: " + ex.Message); return false; }
    }

    /// <summary>下载：GET 原始字节。按 Content-Length **精确读取**（不靠连接关闭判断结束）。</summary>
    public static bool Download(string remoteRel, string localPath, long knownTotal, Action<string>? progress, Func<bool>? cancelled)
    {
        try
        {
            using var t = Connect(out var ns);
            string head = "GET /files/get?path=" + Uri.EscapeDataString(remoteRel) + " HTTP/1.1\r\n"
                        + "Host: " + HostHeader + "\r\n"
                        + "Connection: close\r\n\r\n";
            ns.Write(System.Text.Encoding.ASCII.GetBytes(head));
            string status = ReadResponseLine(ns);
            if (!status.Contains("200")) { progress?.Invoke("下载失败: " + status.Trim()); return false; }
            long total = knownTotal;
            string? l;
            while (!string.IsNullOrEmpty(l = ReadResponseLine(ns)))
            {
                if (l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) && long.TryParse(l[15..].Trim(), out long cl)) { total = cl; }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(localPath) ?? ".");
            var buf = new byte[256 * 1024];
            long got = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using (var fs = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                while (total <= 0 || got < total)
                {
                    if (cancelled?.Invoke() == true) { fs.Flush(); progress?.Invoke("已取消"); return false; }
                    int want = total > 0 ? (int)Math.Min(buf.Length, total - got) : buf.Length;
                    int n = ns.Read(buf, 0, want);
                    if (n <= 0) { break; }
                    fs.Write(buf, 0, n);
                    got += n;
                    if (progress is not null)
                    {
                        double sec = Math.Max(0.001, sw.Elapsed.TotalSeconds);
                        double mbps = got / 1048576.0 / sec;
                        string pct = total > 0 ? (got * 100.0 / total).ToString("F0") + "%" : "";
                        progress($"{got / 1048576.0:F1}/{total / 1048576.0:F1} MB · {pct} · {mbps:F1} MB/s");
                    }
                }
                fs.Flush();
            }
            return total <= 0 || got == total;
        }
        catch (Exception ex) { progress?.Invoke("下载失败: " + ex.Message); return false; }
    }

    /// <summary>让 agent 拉起服务端（无窗口 + 重定向 stdout，输出可用 /stream/stdout 抓）。</summary>
    public static string StartServer()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            string s = http.PostAsync(Base + "/proc/start", null).GetAwaiter().GetResult().Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return s.Contains("\"ok\":true") ? ("agent 已拉起服务端 ✓ " + s) : ("agent 启动失败 ✗ " + s);
        }
        catch (Exception ex) { return "agent 启动失败 ✗ " + ex.Message; }
    }

    /// <summary>让 agent 停掉服务端（含子世界控制台客户端）。</summary>
    public static string StopServer()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            string s = http.PostAsync(Base + "/proc/stop", null).GetAwaiter().GetResult().Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return s.Contains("\"ok\":true") ? ("agent 已停止服务端 ✓ " + s) : ("agent 停止失败 ✗ " + s);
        }
        catch (Exception ex) { return "agent 停止失败 ✗ " + ex.Message; }
    }

    static string ReadResponseLine(NetworkStream ns)
    {
        var sb = new System.Text.StringBuilder();
        var one = new byte[1];
        while (true)
        {
            int n = ns.Read(one, 0, 1);
            if (n <= 0) { break; }
            if (one[0] == (byte)'\n') { break; }
            if (one[0] != (byte)'\r') { sb.Append((char)one[0]); }
        }
        return sb.ToString();
    }
}
