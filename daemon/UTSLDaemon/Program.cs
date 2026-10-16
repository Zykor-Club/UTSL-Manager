using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

// ===== 参数 =====
int port = 7879;
string root = AppContext.BaseDirectory;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--port") { port = int.Parse(args[i + 1]); }
    else if (args[i] == "--root") { root = args[i + 1]; }
}
string logsDir = Path.Combine(root, "logs");

Dictionary<int, TimeSpan> _prevCpu = [];
DateTime _prevAt = DateTime.UtcNow;
long _prevSent = -1, _prevRecv = -1;
int BoundPort = 0;
long metricsCalls = 0;
long lastPingLog = 0;        // 实际绑定到的端口（--selftest 用 0 让系统分配，避免撞上别的程序）

async Task ServeAsync(int listenPort = -1)
{
    int want = listenPort < 0 ? port : listenPort;
    var listener = new TcpListener(IPAddress.Loopback, want);
    try { listener.Start(); }
    catch (Exception ex)
    {
        // ★ 端口被占要显式报错退出：曾经因为"起不来但被静默吞掉"，自检去连了别的程序（msedge）拿到 404，白查半天
        Console.WriteLine("[UTSLDaemon] ✗ 监听 127.0.0.1:" + want + " 失败: " + ex.Message);
        Environment.ExitCode = 2;
        return;
    }
    BoundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
    Console.WriteLine($"[UTSLDaemon] v0.1.0 监听 127.0.0.1:{BoundPort}  root={root}  logs={logsDir}（logs 存在={Directory.Exists(logsDir)}）");
    while (true)
    {
        TcpClient client;
        try { client = await listener.AcceptTcpClientAsync(); }
        catch (Exception ex) { Console.WriteLine("[UTSLDaemon] accept 失败: " + ex.Message); continue; }
        _ = Task.Run(() => HandleAsync(client));
    }
}

if (args.Contains("--selftest"))
{
    Environment.ExitCode = await SelfTestAsync();     // 进程内起监听 + 自己连自己，量延迟（确定性自检）
    return;
}

try { await ServeAsync(); } catch (Exception ex) { Console.WriteLine("[UTSLDaemon] ✗ 运行失败: " + ex.Message); Environment.ExitCode = 3; }

// ================= 请求处理 =================
async Task HandleAsync(TcpClient client)
{
    using (client)
    {
        try
        {
            client.NoDelay = true;
            NetworkStream ns = client.GetStream();
            string? requestLine = await ReadLineAsync(ns);
            if (requestLine is null) { return; }
            // 吃掉请求头直到空行
            while (true)
            {
                string? h = await ReadLineAsync(ns);
                if (h is null || h.Length == 0) { break; }
            }
            string[] parts = requestLine.Split(' ');
            string path = parts.Length >= 2 ? parts[1] : "/";
            int q = path.IndexOf('?');
            if (q >= 0) { path = path[..q]; }

            switch (path)
            {
                case "/ping":
                    if (Environment.TickCount64 - lastPingLog > 60000) { lastPingLog = Environment.TickCount64; Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] /ping（每分钟最多记一次）"); }
                    await WriteJsonAsync(ns, new { ok = true, name = "UTSLDaemon", version = "0.1.0", root, logs = logsDir, logsExists = Directory.Exists(logsDir), at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
                    return;
                case "/metrics":
                    Interlocked.Increment(ref metricsCalls);
                    await WriteJsonAsync(ns, CollectMetrics());
                    return;
                case "/stream/log":
                    Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] 有客户端订阅日志流");
                    try { await StreamLogAsync(ns); } finally { Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] 日志流订阅结束"); }
                    return;
                default:
                    byte[] body = Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"not found\"}");
                    await WriteRawAsync(ns, "HTTP/1.1 404 Not Found", "application/json", body, keepAlive: false);
                    return;
            }
        }
        catch (Exception ex)
        {
            // 客户端正常断开（SSE 取消订阅、curl 超时等）不该当错误刷屏
            if (ex is IOException || ex.InnerException is IOException) { return; }
            Console.WriteLine("[UTSLDaemon] 处理失败: " + ex.Message);
        }
    }
}

// ================= 指标 =================

object CollectMetrics()
{
    var now = DateTime.UtcNow;
    double secs = Math.Max(0.05, (now - _prevAt).TotalSeconds);
    _prevAt = now;

    List<Process> procs = [];
    foreach (Process p in Process.GetProcessesByName("UnifierTSL"))
    {
        try { if (!p.HasExited) { procs.Add(p); } } catch { }
    }

    double cpuSingleCore = 0;
    long privBytes = 0, workBytes = 0;
    int threads = 0, handles = 0, pid = 0;
    foreach (Process p in procs)
    {
        try
        {
            p.Refresh();
            pid = p.Id;
            if (_prevCpu.TryGetValue(p.Id, out TimeSpan prev))
            {
                cpuSingleCore += (p.TotalProcessorTime - prev).TotalMilliseconds / (secs * 1000) * 100.0;
            }
            _prevCpu[p.Id] = p.TotalProcessorTime;
            privBytes += p.PrivateMemorySize64;
            workBytes += p.WorkingSet64;
            threads += p.Threads.Count;
            handles += p.HandleCount;
        }
        catch { }
    }
    cpuSingleCore = Math.Max(0, cpuSingleCore);

    long sent = 0, recv = 0;
    try
    {
        foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) { continue; }
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) { continue; }
            IPInterfaceStatistics st = ni.GetIPStatistics();
            sent += st.BytesSent; recv += st.BytesReceived;
        }
    }
    catch { }

    return new
    {
        ok = true,
        at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        cores = Environment.ProcessorCount,
        server = new
        {
            running = procs.Count > 0,
            processes = procs.Count,
            pid,
            cpuSingleCorePct = Math.Round(cpuSingleCore, 2),
            privateMB = Math.Round(privBytes / 1048576.0, 1),
            workingSetMB = Math.Round(workBytes / 1048576.0, 1),
            threads,
            handles,
        },
        net = new { sentBytes = sent, recvBytes = recv, prevSentBytes = _prevSent, prevRecvBytes = _prevRecv },
        calls = metricsCalls,
        uptimeSec = (long)(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency),
        log = NewestLogInfo(),
    };
}

object? NewestLogInfo()
{
    try
    {
        FileInfo? f = new DirectoryInfo(logsDir).GetFiles("*.log").OrderByDescending(x => x.LastWriteTime).FirstOrDefault();
        return f is null ? null : new { name = f.Name, bytes = f.Length, at = new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeMilliseconds() };
    }
    catch { return null; }
}

// ================= 日志流（SSE）=================
//   ★ 两个实测踩过的坑：
//   1. 偏移记账必须用【绝对偏移】。最初写 consumed += i + 1，而 i 只是当前读块内的下标，
//      导致 pos 推进错误 → 同一批日志被反复重发 → 管理器控制台"一直抽搐"。
//   2. 必须按 UTF-8 解码【完整行】。最初逐字节 (char)b，等于 Latin-1 → 中文全变乱码；
//      文件开头的 BOM 也要去掉，否则首行多出不可见字符。
async Task StreamLogAsync(NetworkStream ns)
{
    await WriteRawAsync(ns, "HTTP/1.1 200 OK", "text/event-stream", [], keepAlive: true, extraHeaders: "Cache-Control: no-cache\r\nX-Accel-Buffering: no\r\n");
    string cur = "";
    long pos = 0;
    const int TailBytes = 64 * 1024;
    bool tailDone = false;
    byte[] buf = new byte[64 * 1024];
    var pending = new List<byte>(4096);
    long lastBeat = Environment.TickCount64;

    while (true)
    {
        FileInfo? f = null;
        try { f = new DirectoryInfo(logsDir).GetFiles("*.log").OrderByDescending(x => x.LastWriteTime).FirstOrDefault(); } catch { }
        if (f is null)
        {
            if (Environment.TickCount64 - lastBeat > 15000) { await WriteAsync(ns, ": no-log-dir\n\n"); lastBeat = Environment.TickCount64; }
            await Task.Delay(500);
            continue;
        }
        if (f.FullName != cur)
        {
            cur = f.FullName;
            tailDone = false;
            pending.Clear();
            await WriteAsync(ns, "event: file\ndata: " + f.Name + "\n\n");
        }
        try
        {
            using (FileStream fs = new(cur, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (!tailDone) { pos = Math.Max(0, fs.Length - TailBytes); tailDone = true; }
                if (fs.Length < pos) { pos = 0; pending.Clear(); }
                fs.Seek(pos, SeekOrigin.Begin);
                long abs = pos;
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    for (int i = 0; i < n; i++)
                    {
                        abs++;
                        if (buf[i] == 10)
                        {
                            await EmitLineAsync(ns, pending);
                            pending.Clear();
                            pos = abs;
                        }
                        else { pending.Add(buf[i]); }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            await WriteAsync(ns, "event: error\ndata: " + ex.Message.Replace("\n", " ") + "\n\n");
            await Task.Delay(1000);
            continue;
        }
        if (Environment.TickCount64 - lastBeat > 15000) { await WriteAsync(ns, ": beat\n\n"); lastBeat = Environment.TickCount64; }
        await Task.Delay(150);
    }
}

/// <summary>把一行（字节）按 UTF-8 解码后推给订阅方；顺手去掉开头的 BOM。</summary>
async Task EmitLineAsync(NetworkStream ns, List<byte> raw)
{
    int start = 0, count = raw.Count;
    if (count >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF) { start = 3; count -= 3; }
    if (count <= 0) { return; }
    byte[] arr = new byte[count];
    raw.CopyTo(start, arr, 0, count);
    string line = Encoding.UTF8.GetString(arr).TrimEnd('\r');
    if (line.Length == 0) { return; }
    await WriteAsync(ns, "data: " + line + "\n\n");
}

// ================= 极简 HTTP 工具 =================
async Task<string?> ReadLineAsync(NetworkStream ns)
{
    var sb = new StringBuilder();
    byte[] one = new byte[1];
    while (true)
    {
        int n = await ns.ReadAsync(one);
        if (n == 0) { return sb.Length > 0 ? sb.ToString() : null; }
        if (one[0] == (byte)'\n') { return sb.ToString().TrimEnd('\r'); }
        sb.Append((char)one[0]);
        if (sb.Length > 8192) { return sb.ToString(); }
    }
}

async Task WriteJsonAsync(NetworkStream ns, object payload)
{
    byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions { WriteIndented = false });
    await WriteRawAsync(ns, "HTTP/1.1 200 OK", "application/json; charset=utf-8", body, keepAlive: false);
}

async Task WriteRawAsync(NetworkStream ns, string status, string contentType, byte[] body, bool keepAlive, string extraHeaders = "")
{
    string head = status + "\r\n"
        + "Content-Type: " + contentType + "\r\n"
        + "Connection: " + (keepAlive ? "keep-alive" : "close") + "\r\n"
        + extraHeaders
        + (keepAlive ? "" : "Content-Length: " + body.Length + "\r\n")
        + "\r\n";
    await WriteAsync(ns, head);
    if (body.Length > 0) { await ns.WriteAsync(body); await ns.FlushAsync(); }
}

async Task WriteAsync(NetworkStream ns, string text)
{
    byte[] bytes = Encoding.UTF8.GetBytes(text);
    await ns.WriteAsync(bytes);
    await ns.FlushAsync();
}

// ================= 自检（--selftest）：进程内起监听、自己连自己，量真实延迟 =================
async Task<int> SelfTestAsync()
{
    int fail = 0;
    void Ok(bool ok, string what) { Console.WriteLine((ok ? "  ✓ " : "  ✗ ") + what); if (!ok) { fail++; } }

    _ = Task.Run(() => ServeAsync(0));       // 0 = 系统分配空闲端口（7880/7881 实测被 node/msedge 占着）
    for (int i = 0; i < 40 && BoundPort == 0; i++) { await Task.Delay(100); }
    if (BoundPort == 0) { Console.WriteLine("  ✗ agent 没起来"); return 1; }
    Console.WriteLine("  自检端口 = " + BoundPort);

    using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
    {
        string ping = await http.GetStringAsync("http://127.0.0.1:" + BoundPort + "/ping");
        Ok(ping.Contains("\"ok\":true") && ping.Contains("UTSLDaemon"), "/ping 返回 ok + 版本");
        string m1 = await http.GetStringAsync("http://127.0.0.1:" + BoundPort + "/metrics");
        await Task.Delay(1200);
        string m2 = await http.GetStringAsync("http://127.0.0.1:" + BoundPort + "/metrics");
        Ok(m2.Contains("\"running\"") && m2.Contains("\"cores\""), "/metrics 返回进程与整机字段");
        Console.WriteLine("    metrics: " + m2);
    }

    // SSE：订阅后追加 5 行，逐行量"写入 → 收到"的延迟
    string testLog = Path.Combine(logsDir, "selftest.log");
    Directory.CreateDirectory(logsDir);
    File.WriteAllText(testLog, "第一行" + Environment.NewLine);
    using var client = new TcpClient();
    client.Connect(IPAddress.Loopback, BoundPort);
    client.ReceiveTimeout = 8000;
    using var ns = client.GetStream();
    byte[] req = Encoding.ASCII.GetBytes("GET /stream/log HTTP/1.1\r\nHost: 127.0.0.1\r\nAccept: text/event-stream\r\n\r\n");
    await ns.WriteAsync(req);
    var reader = new StreamReader(ns, Encoding.UTF8);
    while (true) { string? h = await reader.ReadLineAsync(); if (h is null || h.Length == 0) { break; } }
    string? first = await reader.ReadLineAsync();      // event: file
    Ok(first is not null && first.StartsWith("event: file"), "SSE 建流并通告当前日志文件（" + first + "）");

    var delays = new List<double>();
    for (int k = 1; k <= 5; k++)
    {
        string marker = "PROBE_" + k + "_" + DateTime.Now.ToString("HH:mm:ss.fff");
        var sw = Stopwatch.StartNew();
        File.AppendAllText(testLog, marker + Environment.NewLine);
        while (true)
        {
            string? line = await reader.ReadLineAsync();
            if (line is not null && line.StartsWith("data: ") && line.Contains(marker))
            {
                sw.Stop();
                delays.Add(sw.Elapsed.TotalMilliseconds);
                break;
            }
        }
    }
    Console.WriteLine("    5 次 push 延迟(ms): " + string.Join(", ", delays.Select(d => d.ToString("F0"))));
    double avg = delays.Average();
    Ok(avg < 500, $"平均延迟 {avg:F0} ms（目标 < 500 ms；对比：现管理器本机 1500 ms 轮询、远端 12 s 限流）");
    Ok(delays.Max() < 1500, $"最大延迟 {delays.Max():F0} ms（目标 < 1500 ms）");
    try { File.Delete(testLog); } catch { }

    Console.WriteLine();
    Console.WriteLine(fail == 0 ? "自检全部通过" : fail + " 项失败");
    return fail == 0 ? 0 : 1;
}