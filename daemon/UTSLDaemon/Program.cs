using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

// ===== 参数 =====
int port = 7879;
string root = AppContext.BaseDirectory;
// ★ 直连（面板）模式：--bind 0.0.0.0 把 agent 暴露到网络；--token 是访问令牌。
//   安全红线：绑非回环地址时【必须】给令牌，否则拒绝启动 —— agent 以 SYSTEM 运行且能写文件/启停进程，
//   无认证地挂在公网上等于把整台机器的控制权交出去。
string bindAddr = "127.0.0.1";
string token = "";
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--port") { port = int.Parse(args[i + 1]); }
    else if (args[i] == "--root") { root = args[i + 1]; }
    else if (args[i] == "--bind") { bindAddr = args[i + 1]; }
    else if (args[i] == "--token") { token = args[i + 1]; }
}
bool loopback = bindAddr is "127.0.0.1" or "localhost" or "::1";
if (!loopback && token.Length < 16)
{
    Console.WriteLine("[UTSLDaemon] ✗ 拒绝启动：--bind " + bindAddr + " 是对外地址，但 --token 缺失或过短（至少 16 位）。");
    Console.WriteLine("[UTSLDaemon]   agent 以 SYSTEM 运行、可写文件与启停进程，暴露到网络必须有强令牌。");
    Environment.ExitCode = 3;
    return;
}
string logsDir = Path.Combine(root, "logs");

Dictionary<int, TimeSpan> _prevCpu = [];
DateTime _prevAt = DateTime.UtcNow;
long _prevSent = -1, _prevRecv = -1;
int BoundPort = 0;
// 进程托管状态（P2）：必须声明在所有用到它的局部函数【之前】，否则顶层语句会报"未赋值"
Process? serverProc = null;
var stdoutRing = new ConcurrentQueue<string>();
const int StdoutRingMax = 4000;
// ★ 入队总序号（绝对游标）。环形缓冲满时从队首丢弃；若消费者用数组下标当游标，
//   长度恒为 4000 → 下标永远追不上 → stdout 流会永久静默。序号才是可靠的进度标识。
long stdoutSeq = 0;
long metricsCalls = 0;
long pingCalls = 0;
long lastPingLog = 0;        // 实际绑定到的端口（--selftest 用 0 让系统分配，避免撞上别的程序）

async Task ServeAsync(int listenPort = -1)
{
    int want = listenPort < 0 ? port : listenPort;
    IPAddress bindIp = loopback ? IPAddress.Loopback : IPAddress.Any;
    var listener = new TcpListener(bindIp, want);
    try { listener.Start(); }
    catch (Exception ex)
    {
        // ★ 端口被占要显式报错退出：曾经因为"起不来但被静默吞掉"，自检去连了别的程序（msedge）拿到 404，白查半天
        Console.WriteLine("[UTSLDaemon] ✗ 监听 " + bindAddr + ":" + want + " 失败: " + ex.Message);
        Environment.ExitCode = 2;
        return;
    }
    BoundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
    Console.WriteLine($"[UTSLDaemon] v0.1.0 监听 {bindAddr}:{BoundPort}  root={root}  logs={logsDir}（logs 存在={Directory.Exists(logsDir)}）"
        + (token.Length > 0 ? "  [已启用令牌认证]" : "  [未启用认证：仅回环]"));
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
            // ★ 缓冲区要够大：这条链路 RTT 约 200 ms，默认窗口下吞吐会被卡在 ~0.3 MB/s（实测）
            try { client.ReceiveBufferSize = 4 * 1024 * 1024; client.SendBufferSize = 4 * 1024 * 1024; } catch { }
            NetworkStream ns = client.GetStream();
            string? requestLine = await ReadLineAsync(ns);
            if (requestLine is null) { return; }
            // 吃掉请求头直到空行（顺手留存，/files/put 需要 Content-Length）
            var reqHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                string? h = await ReadLineAsync(ns);
                if (h is null || h.Length == 0) { break; }
                int c = h.IndexOf(':');
                if (c > 0) { reqHeaders[h[..c].Trim()] = h[(c + 1)..].Trim(); }
            }
            string[] parts = requestLine.Split(' ');
            string pathRaw = parts.Length >= 2 ? parts[1] : "/";
            // ★ 兼容【绝对形式】请求行（RFC 7230 5.3.2）：经代理访问时请求行是
            //   "GET http://host:port/path?query HTTP/1.1"，只取路径部分，否则一律 404。
            if (pathRaw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || pathRaw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                int schemeEnd = pathRaw.IndexOf("://", StringComparison.Ordinal) + 3;
                int slash = pathRaw.IndexOf('/', schemeEnd);
                pathRaw = slash >= 0 ? pathRaw[slash..] : "/";
            }
            string path = pathRaw;
            int q = path.IndexOf('?');
            if (q >= 0) { path = path[..q]; }

            // ★ 令牌认证：配了 token 时，除 /ping（只回版本号，供连通性探测）外所有端点都要
            //   Authorization: Bearer <token>。比对用固定时间比较，避免时序侧信道。
            bool authed = token.Length == 0
                || (reqHeaders.TryGetValue("Authorization", out string? ah)
                    && ah.StartsWith("Bearer ", StringComparison.Ordinal)
                    && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                           Encoding.UTF8.GetBytes(ah[7..].Trim()), Encoding.UTF8.GetBytes(token)));
            if (token.Length > 0 && !authed && path != "/ping")
            {
                byte[] no = Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"未授权：缺少或错误的令牌\"}");
                await WriteRawAsync(ns, "HTTP/1.1 401 Unauthorized", "application/json", no, keepAlive: false);
                return;
            }

            switch (path)
            {
                case "/ping":
                    Interlocked.Increment(ref pingCalls);
                    if (Environment.TickCount64 - lastPingLog > 60000) { lastPingLog = Environment.TickCount64; Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] /ping（每分钟最多记一次）"); }
                    // 未认证时只回最小信息（不泄露本机路径）；认证后给全量
                    await WriteJsonAsync(ns, authed
                        ? new { ok = true, name = "UTSLDaemon", version = "0.1.0", root, logs = logsDir, logsExists = Directory.Exists(logsDir), auth = token.Length > 0, at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
                        : new { ok = true, name = "UTSLDaemon", version = "0.1.0", auth = true });
                    return;
                case "/metrics":
                    Interlocked.Increment(ref metricsCalls);
                    await WriteJsonAsync(ns, CollectMetrics());
                    return;
                case "/proc/start":
                    await ProcStartAsync(ns);
                    return;
                case "/proc/stop":
                    await ProcStopAsync(ns);
                    return;
                case "/proc/status":
                    await WriteJsonAsync(ns, ProcStatus());
                    return;
                // ★ 压缩服务端工作集。原来管理器走 ssh 里的 Add-Type -MemberDefinition（每次重新编译 C#，秒级），
                //   而 ssh 每次都起新进程、类型不跨进程存活 —— 所以那个"只编译一次"的守卫实际无效。这里由 agent 直接做。
                case "/proc/trim":
                    await WriteJsonAsync(ns, TrimServerMemory());
                    return;
                case "/stream/stdout":
                    await StreamStdoutAsync(ns);
                    return;
                case "/files/get":
                    await SendFileAsync(ns, QueryValue(pathRaw, "path"));
                    return;
                case "/files/put":
                    await ReceiveFileAsync(ns, QueryValue(pathRaw, "path"), reqHeaders);
                    return;
                case "/files/list":
                    await ListFilesAsync(ns, QueryValue(pathRaw, "path"));
                    return;
                // ★ 直连（无 SSH）模式需要的最小文件原语：读文本 / 判存在 / 建目录 / 改名。
                //   二进制大文件仍走 /files/get 与 /files/put。
                case "/fs/read":
                    await ReadTextAsync(ns, QueryValue(pathRaw, "path"));
                    return;
                case "/fs/exists":
                    await ExistsAsync(ns, QueryValue(pathRaw, "path"));
                    return;
                case "/fs/mkdir":
                    await MkdirAsync(ns, QueryValue(pathRaw, "path"));
                    return;
                case "/fs/rename":
                    await RenameAsync(ns, QueryValue(pathRaw, "path"), QueryValue(pathRaw, "to"));
                    return;
                case "/files/delete":
                    await DeletePathAsync(ns, QueryValue(pathRaw, "path"));
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
        pings = pingCalls,
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

// ================= 进程托管（P2：agent 拥有服务端进程，直接抓 stdout）=================
//   前提：UnifierTSL 已能在【无控制台 + 重定向 stdout】下启动（补丁在 lib\UnifierTSL.dll —— 注意根目录那份不被加载）。
//   这样控制台就能拿到服务端**真正的 stdout**（含 /help 这类只写控制台、不进日志的输出），不再只依赖日志流。

string ServerExePath() => Path.Combine(root, "UnifierTSL.exe");

object ProcStatus()
{
    var p = serverProc;
    bool running = false; int pid = 0; long privMB = 0;
    try
    {
        if (p is not null && !p.HasExited) { p.Refresh(); running = true; pid = p.Id; privMB = p.PrivateMemorySize64 / 1048576; }
    }
    catch { }
    return new { ok = true, running, pid, privateMB = privMB, exe = ServerExePath(), ring = stdoutRing.Count };
}

async Task ProcStartAsync(NetworkStream ns)
{
    try
    {
        if (serverProc is not null && !serverProc.HasExited)
        {
            await WriteJsonAsync(ns, new { ok = true, already = true, pid = serverProc.Id });
            return;
        }
        if (!File.Exists(ServerExePath())) { await WriteJsonAsync(ns, new { ok = false, error = "找不到 " + ServerExePath() }); return; }
        stdoutRing.Clear();
        var psi = new ProcessStartInfo(ServerExePath())
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-joinserver");
        psi.ArgumentList.Add("first");
        psi.ArgumentList.Add("-logmode");
        psi.ArgumentList.Add("txt");
        var p = Process.Start(psi);
        if (p is null) { await WriteJsonAsync(ns, new { ok = false, error = "启动失败" }); return; }
        serverProc = p;
        _ = Task.Run(() => PumpAsync(p.StandardOutput, "[out] "));
        _ = Task.Run(() => PumpAsync(p.StandardError, "[err] "));
        Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] agent 拉起服务端 pid=" + p.Id + "（无控制台 + 重定向 stdout）");
        await WriteJsonAsync(ns, new { ok = true, pid = p.Id });
    }
    catch (Exception ex) { await WriteJsonAsync(ns, new { ok = false, error = ex.Message }); }
}

async Task PumpAsync(StreamReader reader, string prefix)
{
    try
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            stdoutRing.Enqueue(prefix + line);
            System.Threading.Interlocked.Increment(ref stdoutSeq);
            while (stdoutRing.Count > StdoutRingMax) { stdoutRing.TryDequeue(out _); }
        }
    }
    catch { }
}

async Task ProcStopAsync(NetworkStream ns)
{
    int killed = 0;
    try
    {
        var p = serverProc;
        if (p is not null && !p.HasExited) { p.Kill(entireProcessTree: true); killed++; }
        // 子世界控制台客户端也要收掉
        foreach (var c in Process.GetProcessesByName("UnifierTSL.ConsoleClient")) { try { c.Kill(); killed++; } catch { } }
        await WriteJsonAsync(ns, new { ok = true, killed });
    }
    catch (Exception ex) { await WriteJsonAsync(ns, new { ok = false, error = ex.Message }); }
}

async Task StreamStdoutAsync(NetworkStream ns)
{
    await WriteRawAsync(ns, "HTTP/1.1 200 OK", "text/event-stream", [], keepAlive: true, extraHeaders: "Cache-Control: no-cache\r\nX-Accel-Buffering: no\r\n");
    // 先回放环形缓冲（给上下文），再持续跟随
    foreach (string s in stdoutRing.ToArray())
    {
        await WriteAsync(ns, "data: " + s + "\n\n");
    }
    long lastBeat = Environment.TickCount64;
    // ★★ 游标必须是绝对序号而不是数组下标。原来 sent 是下标，而环形满时队列从队首丢弃：
    //   all.Length 恒等于 4000 == sent → all.Length < sent 永不成立 → for 循环空转 →
    //   服务端 stdout 的前 4000 行之后永久不再推送，连接也不关（控制台 stdout 页永久冻住）。
    long nextSeq = System.Threading.Volatile.Read(ref stdoutSeq) - stdoutRing.Count;
    if (nextSeq < 0) { nextSeq = 0; }
    while (true)
    {
        var all = stdoutRing.ToArray();
        long seqBase = System.Threading.Volatile.Read(ref stdoutSeq) - all.Length;   // all[0] 的绝对序号
        if (nextSeq < seqBase) { nextSeq = seqBase; }        // 落后太多（已被覆盖），从现存最老的开始
        for (long q = nextSeq; q < seqBase + all.Length; q++)
        {
            await WriteAsync(ns, "data: " + all[(int)(q - seqBase)] + "\n\n");
        }
        nextSeq = seqBase + all.Length;
        if (Environment.TickCount64 - lastBeat > 15000) { await WriteAsync(ns, ": beat\n\n"); lastBeat = Environment.TickCount64; }
        await Task.Delay(200);
    }
}

// ================= 文件接口（P1：走 agent 取代 scp，进度=真实字节数）=================
//   ★ 安全：所有路径都解析到 root 之内，越界一律拒绝。agent 只绑回环，但仍不该能读写任意路径。
string QueryValue(string rawPath, string name)
{
    int q = rawPath.IndexOf('?');
    if (q < 0) { return ""; }
    foreach (string kv in rawPath[(q + 1)..].Split('&'))
    {
        int e = kv.IndexOf('=');
        if (e <= 0) { continue; }
        if (!kv[..e].Equals(name, StringComparison.OrdinalIgnoreCase)) { continue; }
        return Uri.UnescapeDataString(kv[(e + 1)..].Replace('+', ' '));
    }
    return "";
}

bool ResolveSafe(string rel, out string full, out string err)
{
    full = ""; err = "";
    try
    {
        // ★ 必须先规范成"带尾分隔符的目录前缀"再比较：否则 C:\utsl-deploy 会把兄弟目录
        //   C:\utsl-deploy-backup\... 判成"在根内"（实测 StartsWith 返回 true），
        //   而 agent 是以 SYSTEM 运行的、/files/delete 还是递归删除 —— 等于把部署根之外的读写删全放开。
        string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string f = Path.GetFullPath(Path.Combine(r, rel ?? ""));
        if (!f.Equals(r, StringComparison.OrdinalIgnoreCase)
            && !f.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { err = "路径越界（只允许部署根之内）"; return false; }
        full = f; return true;
    }
    catch (Exception ex) { err = ex.Message; return false; }
}

async Task SendFileAsync(NetworkStream ns, string rel)
{
    if (!ResolveSafe(rel, out string full, out string err)) { await WriteJsonAsync(ns, new { ok = false, error = err }); return; }
    if (!File.Exists(full)) { await WriteJsonAsync(ns, new { ok = false, error = "文件不存在" }); return; }
    var fi = new FileInfo(full);
    await WriteRawAsync(ns, "HTTP/1.1 200 OK", "application/octet-stream", [], keepAlive: true, extraHeaders: "", contentLength: fi.Length);
    using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
    {
        await fs.CopyToAsync(ns, 1 << 20);
    }
    await ns.FlushAsync();
}

async Task ReceiveFileAsync(NetworkStream ns, string rel, Dictionary<string, string> headers)
{
    if (!ResolveSafe(rel, out string full, out string err)) { await WriteJsonAsync(ns, new { ok = false, error = err }); return; }
    long len = headers.TryGetValue("Content-Length", out string? cl) && long.TryParse(cl, out long l) ? l : -1;
    if (len < 0) { await WriteJsonAsync(ns, new { ok = false, error = "缺少 Content-Length" }); return; }
    Directory.CreateDirectory(Path.GetDirectoryName(full) ?? root);
    long got = 0;
    byte[] buf = new byte[1 << 20];
    // ★ 写 .part 再改名：原来直接 FileMode.Create 写最终路径，断连/重启会在原处留下
    //   被截断或写了一半的 .wld/.utpl/.dll —— 服务端可能直接加载坏文件（是数据损坏，不只是失败）。
    string part = full + ".part";
    try
    {
        using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            while (got < len)
            {
                int n = await ns.ReadAsync(buf, 0, (int)Math.Min(buf.Length, len - got));
                if (n <= 0) { break; }
                fs.Write(buf, 0, n);
                got += n;
            }
            fs.Flush(true);
        }
    }
    catch (Exception ex)
    {
        try { File.Delete(part); } catch { }
        await WriteJsonAsync(ns, new { ok = false, error = ex.Message });
        return;
    }
    if (got != len)
    {
        try { File.Delete(part); } catch { }
        await WriteJsonAsync(ns, new { ok = false, bytes = got, error = "传输未完成（连接中断？），已丢弃半截文件" });
        return;
    }
    File.Move(part, full, overwrite: true);      // 同卷原子替换
    await WriteJsonAsync(ns, new { ok = true, bytes = got, path = rel });
}

/// <summary>读文本（UTF-8）。上限 8MB —— 更大的文件请走 /files/get 二进制通道。</summary>
async Task ReadTextAsync(NetworkStream ns, string rel)
{
    if (!ResolveSafe(rel, out string full, out string err)) { await WriteJsonAsync(ns, new { ok = false, error = err }); return; }
    try
    {
        if (!File.Exists(full)) { await WriteJsonAsync(ns, new { ok = false, error = "文件不存在" }); return; }
        long len = new FileInfo(full).Length;
        if (len > 8 * 1024 * 1024) { await WriteJsonAsync(ns, new { ok = false, error = "文件过大（" + (len / 1048576) + " MB > 8 MB），请走 /files/get" }); return; }
        string text = await File.ReadAllTextAsync(full, Encoding.UTF8);
        await WriteJsonAsync(ns, new { ok = true, path = rel, bytes = len, text });
    }
    catch (Exception ex) { await WriteJsonAsync(ns, new { ok = false, error = ex.Message }); }
}

/// <summary>压缩 UnifierTSL 进程的工作集（EmptyWorkingSet 等价于任务管理器里"清空工作集"）。</summary>
Dictionary<string, object> TrimServerMemory()
{
    int n = 0;
    long before = 0, after = 0;
    foreach (Process p in Process.GetProcessesByName("UnifierTSL"))
    {
        using (p)
        {
            try
            {
                if (p.HasExited) { continue; }
                before += p.WorkingSet64;
                NativeMethods.EmptyWorkingSet(p.Handle);
                p.Refresh();
                after += p.WorkingSet64;
                n++;
            }
            catch { }
        }
    }
    return new Dictionary<string, object>
    {
        ["ok"] = true,
        ["trimmed"] = n,
        ["beforeMB"] = Math.Round(before / 1048576.0, 1),
        ["afterMB"] = Math.Round(after / 1048576.0, 1),
    };
}


async Task ExistsAsync(NetworkStream ns, string rel)
{
    if (!ResolveSafe(rel, out string full, out string err)) { await WriteJsonAsync(ns, new { ok = false, error = err }); return; }
    await WriteJsonAsync(ns, new { ok = true, path = rel, exists = File.Exists(full) || Directory.Exists(full) });
}

async Task MkdirAsync(NetworkStream ns, string rel)
{
    if (!ResolveSafe(rel, out string full, out string err)) { await WriteJsonAsync(ns, new { ok = false, error = err }); return; }
    try
    {
        Directory.CreateDirectory(full);
        await WriteJsonAsync(ns, new { ok = true, path = rel });
    }
    catch (Exception ex) { await WriteJsonAsync(ns, new { ok = false, error = ex.Message }); }
}

async Task RenameAsync(NetworkStream ns, string rel, string to)
{
    if (!ResolveSafe(rel, out string full, out string err)) { await WriteJsonAsync(ns, new { ok = false, error = err }); return; }
    if (!ResolveSafe(to, out string dest, out string err2)) { await WriteJsonAsync(ns, new { ok = false, error = err2 }); return; }
    try
    {
        if (!File.Exists(full) && !Directory.Exists(full)) { await WriteJsonAsync(ns, new { ok = false, error = "源不存在" }); return; }
        bool isDir = Directory.Exists(full);
        if (isDir) { Directory.Move(full, dest); } else { File.Move(full, dest, overwrite: true); }
        await WriteJsonAsync(ns, new { ok = true, path = rel, to });
    }
    catch (Exception ex) { await WriteJsonAsync(ns, new { ok = false, error = ex.Message }); }
}

async Task ListFilesAsync(NetworkStream ns, string rel)
{
    if (!ResolveSafe(rel, out string full, out string err)) { await WriteJsonAsync(ns, new { ok = false, error = err }); return; }
    if (!Directory.Exists(full)) { await WriteJsonAsync(ns, new { ok = false, error = "目录不存在" }); return; }
    var list = new List<object>();
    foreach (string d in Directory.EnumerateDirectories(full)) { list.Add(new { name = Path.GetFileName(d), dir = true, size = 0L, mtime = 0L }); }
    foreach (string f in Directory.EnumerateFiles(full))
    {
        var fi = new FileInfo(f);
        list.Add(new { name = fi.Name, dir = false, size = fi.Length, mtime = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds() });
    }
    await WriteJsonAsync(ns, new { ok = true, path = rel, entries = list });
}

async Task DeletePathAsync(NetworkStream ns, string rel)
{
    if (!ResolveSafe(rel, out string full, out string err)) { await WriteJsonAsync(ns, new { ok = false, error = err }); return; }
    try
    {
        if (Directory.Exists(full)) { Directory.Delete(full, true); }
        else if (File.Exists(full)) { File.Delete(full); }
        else { await WriteJsonAsync(ns, new { ok = false, error = "不存在" }); return; }
        await WriteJsonAsync(ns, new { ok = true });
    }
    catch (Exception ex) { await WriteJsonAsync(ns, new { ok = false, error = ex.Message }); }
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

async Task WriteRawAsync(NetworkStream ns, string status, string contentType, byte[] body, bool keepAlive, string extraHeaders = "", long contentLength = -1)
{
    string head = status + "\r\n"
        + "Content-Type: " + contentType + "\r\n"
        + "Connection: " + (keepAlive ? "keep-alive" : "close") + "\r\n"
        + extraHeaders
        + (keepAlive ? (contentLength >= 0 ? "Content-Length: " + contentLength + "\r\n" : "") : "Content-Length: " + body.Length + "\r\n")
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

    // ★ 禁用代理：自检连的是 127.0.0.1，走代理会失败（实测这台机器开了系统代理）
using (var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) })
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