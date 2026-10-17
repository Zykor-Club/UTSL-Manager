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
// ★ R8：原来 `i < args.Length - 1` 会让末尾参数被静默忽略（--port 缺值 → 回落内建 7879 = 生产端口），
//   而 --port abc 的 int.Parse 异常未捕获、直接进 Windows 错误报告。现在缺值/非法一律显式退出码 4。
for (int i = 0; i < args.Length; i++)
{
    string Need(string name)
    {
        if (i + 1 >= args.Length)
        {
            Console.WriteLine("[UTSLDaemon] ✗ " + name + " 缺少参数值");
            Environment.ExitCode = 4; Environment.Exit(4);
        }
        return args[++i];
    }
    if (args[i] == "--port")
    {
        if (!int.TryParse(Need("--port"), out port) || port <= 0 || port > 65535)
        {
            Console.WriteLine("[UTSLDaemon] ✗ --port 不是合法端口");
            Environment.ExitCode = 4; Environment.Exit(4);
        }
    }
    else if (args[i] == "--root") { root = Need("--root"); }
    else if (args[i] == "--bind") { bindAddr = Need("--bind"); }
    else if (args[i] == "--token") { token = Need("--token"); }
    else if (args[i] == "--selftest") { /* 主流程后面处理 */ }
    else { Console.WriteLine("[UTSLDaemon] ⚠ 未知参数: " + args[i]); }
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
// ★ 这几个必须是【文件顶部】的顶层变量：顶层语句里被局部函数捕获的变量必须先声明，否则 CS0165。
string _logPick = "";        // 最近一次选中的日志文件（缓存，避免每 150ms 枚举排序整个目录）
long _logPickAtMs = 0;
//   注意：顶层变量若要被局部函数捕获，必须带初始化器（否则 CS0165）。
long _netSent = 0, _netRecv = 0, _netAtMs = 0;   // 网卡累计字节的 1 秒缓存（枚举一次 75~174ms，不值得每 2 秒做一次）
// ★ JsonSerializerOptions 内部带序列化元数据缓存，每次请求 new 一个等于把缓存丢掉（.NET 有专门的分析器警告）。
var jsonOpts = new JsonSerializerOptions { WriteIndented = false };
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
        // ★★ 实测：经 SSH 隧道（RTT≈200ms）下载 6.6MB 只有 0.36 MB/s —— 因为 Windows 默认 socket 缓冲约 64KB，
        //   64KB / 0.2s ≈ 320 KB/s，正好对上。管理器客户端那侧本来设了 4MB（注释里就写着"太小会压到 ~0.3MB/s"），
        //   但【接受连接这一侧】没设 —— 大文件传输的瓶颈就卡在这里。两端都要设。
        try { client.NoDelay = true; client.ReceiveBufferSize = 4 * 1024 * 1024; client.SendBufferSize = 4 * 1024 * 1024; } catch { }
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
            // ★ R3：请求行/请求头必须有读超时，否则"连上不发数据"的连接会无限期占着（D 子代理实测 100 条 1.5 秒后仍在）。
            string? requestLine = await ReadLineAsync(ns).WaitAsync(TimeSpan.FromSeconds(30));
            if (requestLine is null) { return; }
            // 吃掉请求头直到空行（顺手留存，/files/put 需要 Content-Length）
            var reqHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                string? h = await ReadLineAsync(ns).WaitAsync(TimeSpan.FromSeconds(30));
                if (h is null || h.Length == 0) { break; }
                int c = h.IndexOf(':');
                if (c > 0) { reqHeaders[h[..c].Trim()] = h[(c + 1)..].Trim(); }
            }
            string[] parts = requestLine.Split(' ');
            string methodRaw = parts.Length >= 1 ? parts[0].ToUpperInvariant() : "GET";
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

            // ★★ 破坏性端点禁止 GET：浏览器里任意页面都能发起跨站 GET（D 子代理实测带 Origin: http://evil.example
            //   的 GET /files/delete 照样生效），而这些操作会删/建/改名/覆盖文件。
            if (path is "/files/delete" or "/fs/mkdir" or "/fs/rename" or "/files/put" && methodRaw == "GET")
            {
                byte[] m = Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"该端点不接受 GET（破坏性操作）\"}");
                await WriteRawAsync(ns, "HTTP/1.1 405 Method Not Allowed", "application/json", m, keepAlive: false);
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
                    await ProcStartAsync(ns, QueryValue(pathRaw, "console"));
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

(long Sent, long Recv) NetTotalsCached()
{
    long nowMs = Environment.TickCount64;
    if (_netAtMs > 0 && nowMs - _netAtMs < 1000) { return (_netSent, _netRecv); }
    long s0 = 0, r0 = 0;
    try
    {
        // ★ 实测修复（2026-10-17）：Windows 上 .NET (Core) 会把【同一块网卡】的 NDIS 轻量筛选器
        //   （WFP Native/802.3 MAC Layer LightWeight Filter、QoS Packet Scheduler、Native/Virtual WiFi Filter…）
        //   也枚举成独立接口，而它们报告的是【同一份】字节计数器 —— 全加会成倍虚高：
        //   实测本机 20 条/6 倍（33344MB vs 真值 5557MB）、远端 4 条/4 倍（333MB vs 83MB）。
        //   ★ 按 Id 去重无效（筛选器各有不同的 Id）；按 MAC 去重有效，且结果与
        //   Get-NetAdapterStatistics 逐字节一致（实测 5557.4MB == 5557.4MB）。
        //   没有 MAC 的接口（WAN Miniport 等伪接口）退回用 Id 去重。
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) { continue; }
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) { continue; }
            string key;
            try
            {
                string mac = ni.GetPhysicalAddress()?.ToString() ?? "";
                key = mac.Length > 0 ? "mac:" + mac : "id:" + ni.Id;
            }
            catch { key = "id:" + ni.Id; }
            if (!seen.Add(key)) { continue; }
            IPInterfaceStatistics st = ni.GetIPStatistics();
            s0 += st.BytesSent; r0 += st.BytesReceived;
        }
    }
    catch { }
    _netSent = s0; _netRecv = r0; _netAtMs = nowMs;
    return (s0, r0);
}

object CollectMetrics()
{
    var now = DateTime.UtcNow;
    double secs = Math.Max(0.05, (now - _prevAt).TotalSeconds);
    _prevAt = now;

    // ★ 原来把 Process 收进 List 后一直不 Dispose：Process 实现 IDisposable，HasExited/TotalProcessorTime/
    //   Threads/HandleCount 都会 OpenProcess 并缓存句柄。管理器每 2 秒拉一次 /metrics → 句柄只能等 GC 终结器，
    //   高负载下（GC 间隔大）会持续上涨。现在一个循环内取完所需数值并立即释放。
    double cpuSingleCore = 0;
    long privBytes = 0, workBytes = 0;
    int threads = 0, handles = 0, pid = 0, procCount = 0;
    // ★ 修（2026-10-17）：一台机器上跑多个 UnifierTSL 实例时，原来把它们的私有内存/工作集**加总**，
    //   于是 7879 报出 1537 MB（= 1065 + 478），管理器卡片显示成"两台一样大"。
    //   现在只报"与本部署 exe 路径完全一致"的那个进程；没有完全匹配时取私有内存最大的那个，
    //   并在响应里保留 processes 计数，便于识别"这台机器上还有别的实例"。
    var seen = new List<(int Pid, string Path, long Priv, long Work, int Threads, int Handles, TimeSpan Cpu)>();
    foreach (Process p in Process.GetProcessesByName("UnifierTSL"))
    {
        using (p)
        {
            try
            {
                if (p.HasExited) { continue; }
                p.Refresh();
                string fp = "";
                try { fp = p.MainModule?.FileName ?? ""; } catch { }
                seen.Add((p.Id, fp, p.PrivateMemorySize64, p.WorkingSet64, p.Threads.Count, p.HandleCount, p.TotalProcessorTime));
            }
            catch { }
        }
    }
    procCount = seen.Count;
    string wantExe = ServerExePath();
    var pick = seen.FirstOrDefault(x => x.Path.Length > 0 && string.Equals(x.Path, wantExe, StringComparison.OrdinalIgnoreCase));
    if (pick.Pid == 0 && seen.Count > 0) { pick = seen.OrderByDescending(x => x.Priv).First(); }
    if (pick.Pid != 0)
    {
        pid = pick.Pid;
        privBytes = pick.Priv; workBytes = pick.Work; threads = pick.Threads; handles = pick.Handles;
        if (_prevCpu.TryGetValue(pick.Pid, out TimeSpan prev))
        {
            cpuSingleCore += (pick.Cpu - prev).TotalMilliseconds / (secs * 1000) * 100.0;
        }
        _prevCpu[pick.Pid] = pick.Cpu;
    }
    // ★ 网卡枚举实测 75~174ms/次（A 子代理数据），占 /metrics 118~186ms 的大头，而管理器每 2 秒就拉一次。
    //   累计字节本来就要靠相邻采样差分，1 秒的缓存完全够用（快照之间有差分基准）。
    long sent = 0, recv = 0;
    (sent, recv) = NetTotalsCached();

    return new
    {
        ok = true,
        at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        cores = Environment.ProcessorCount,
        server = new
        {
            running = procCount > 0,
            processes = procCount,
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

// ★ 日志文件缓存：/stream/log 每 150ms、/metrics 每 2 秒都要"取最新日志"，原来每次都是
//   "枚举整个 logs 目录 + 按时间排序 + 构造 FileInfo" 的重活（轮转文件多时就是持续空转的 CPU）。
//   现在 5 秒内复用同一个结果；缓存路径每轮检查是否还存在，文件被删/轮转立刻重新枚举。
FileInfo? PickNewestLog()
{
    long nowMs = Environment.TickCount64;
    if (_logPick.Length > 0 && nowMs - _logPickAtMs < 5000)
    {
        try { var cached = new FileInfo(_logPick); if (cached.Exists) { return cached; } } catch { }
    }
    try
    {
        FileInfo? f = new DirectoryInfo(logsDir).GetFiles("*.log").OrderByDescending(x => x.LastWriteTime).FirstOrDefault();
        _logPick = f?.FullName ?? "";
        _logPickAtMs = nowMs;
        return f;
    }
    catch { _logPick = ""; _logPickAtMs = nowMs; return null; }
}

object? NewestLogInfo()
{
    try
    {
        FileInfo? f = PickNewestLog();
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
        FileInfo? f = PickNewestLog();
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
                if (fs.Length < pos) { pos = 0; }
                // ★ 必须先清 pending：pos 只在一行读全时才前进，所以上一轮末尾那半行【下一轮会从 pos 再读到】。
                //   不清就会把同一段半行重复追加 → 日志文本重复损坏（子代理已实测复现 PARTIALx6）。
                pending.Clear();
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
                        else
                        {
                            // 保险：日志里若出现异常长的一"行"（没有换行），别把内存吃爆
                            if (pending.Count < 4 * 1024 * 1024) { pending.Add(buf[i]); }
                            else { await EmitLineAsync(ns, pending); pending.Clear(); pos = abs; }
                        }
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

async Task ProcStartAsync(NetworkStream ns, string consoleMode = "")
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
        // ★★ 实测结论（三次迭代后才跑通，务必按这个方式来）：
        //   1) UseShellExecute=false（不论 CreateNoWindow 真假）：服务端拿不到控制台 → UnifierTSL 的 headless 控制台
        //      构造函数会读 Console.WindowHeight → IOException「句柄无效」→ TShockAPI.TShock 静态构造失败 → 服务端立刻崩。
        //   2) UseShellExecute=true 直接起 exe：ShellExecute 走 explorer 的环境，UTSL_CONSOLE_MODE 传不进去；
        //      且服务端需要管理员权限时会弹 UAC（无人应答的上下文里被取消 → 启动直接失败）。
        //   ✔ 3) 交给 cmd.exe 起：cmd 自己是控制台程序、会分配到真控制台，服务端继承它；
        //      环境变量用 cmd 内的 set 提供（不依赖 ShellExecute 的环境继承）。
        //      实测：20 秒内 7788/7878 都在监听、REST 报 3 个世界 running、每世界控制台窗口 0。
        bool headlessWanted = string.Equals(consoleMode, "headless", StringComparison.OrdinalIgnoreCase);
        // ★ 关键细节：不要把整条命令拼成 `cmd /c "…"` —— ArgumentList 会给整串加引号，而 cmd 的 /c 对
        //   "外层引号 + 内部引号" 的解析有经典陷阱（实测：agent 里那条命令静默失败，而我手工用同样字符串却能起）。
        //   改为写一个临时 .cmd 脚本再让 cmd 执行它：引号问题彻底消失，也更易读。
        string wrapper = Path.Combine(Path.GetTempPath(), "utsl-start-" + Guid.NewGuid().ToString("N") + ".cmd");
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("@echo off");
        if (headlessWanted) { sb.AppendLine("set UTSL_CONSOLE_MODE=headless"); }
        sb.AppendLine("cd /d \"" + root + "\"");
        sb.AppendLine("\"" + ServerExePath() + "\" -joinserver first -logmode txt");
        try { File.WriteAllText(wrapper, sb.ToString(), new UTF8Encoding(false)); }
        catch (Exception ex) { await WriteJsonAsync(ns, new { ok = false, error = "写启动脚本失败: " + ex.Message }); return; }
        var psi = new ProcessStartInfo("cmd.exe")
        {
            WorkingDirectory = root,
            UseShellExecute = true,           // cmd 会分配到真控制台，服务端继承它（这是它能起来的关键）
            WindowStyle = ProcessWindowStyle.Minimized,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(wrapper);
        Process? p = null;
        try { p = Process.Start(psi); }
        catch (Exception ex) { Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] 用 cmd 启动服务端失败: " + ex.Message); }
        if (p is null) { await WriteJsonAsync(ns, new { ok = false, error = "启动失败（cmd 通道）" }); return; }
        // 注意：这里拿到的是 cmd 的进程；cmd /c 会一直等子进程结束，所以它的存活就等于服务端在跑。
        // 因此 /proc/status 的 running、/proc/stop 的结束判定都仍然正确（见 ProcStopAsync 用 Kill(entireProcessTree)）。
        serverProc = p;
        Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] agent 拉起服务端（cmd pid=" + p.Id + "，headless=" + headlessWanted + "）");
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

/// <summary>解析结果是否就是部署根自身。★ 删除/覆盖写必须显式拒绝它 —— 见 DeletePathAsync 的说明。</summary>
bool IsRootItself(string full)
{
    try
    {
        string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string f = Path.GetFullPath(full).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return f.Equals(r, StringComparison.OrdinalIgnoreCase);
    }
    catch { return true; }   // 判不了就按"是根"处理（安全侧）
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

/// <summary>
/// 排空请求体再回错误响应。★ 不排空时内核会回 RST，客户端可能【收不到响应】（D 子代理实测：同一个越界 PUT
/// 连做 6 次里 5 次 no-response），管理器那侧会因此间歇性静默回落到 scp。
/// </summary>
async Task DrainBodyAsync(NetworkStream ns, Dictionary<string, string> headers)
{
    try
    {
        long left = headers.TryGetValue("Content-Length", out string? cl) && long.TryParse(cl, out long l) ? l : 0;
        byte[] tmp = new byte[64 * 1024];
        while (left > 0)
        {
            int n = await ns.ReadAsync(tmp, 0, (int)Math.Min(tmp.Length, left)).WaitAsync(TimeSpan.FromSeconds(10));
            if (n <= 0) { break; }
            left -= n;
        }
    }
    catch { }
}

async Task ReceiveFileAsync(NetworkStream ns, string rel, Dictionary<string, string> headers)
{
    // ★ 拒绝空路径 / 根自身 / 目录目标：D 子代理实测 PUT path=（空）会在【部署根之外】建 <根>.part 且零响应；
    //   PUT path=<已存在目录> 会在根内留 sub.part。写文件必须落到根内的具体文件。
    if (!ResolveSafe(rel, out string full, out string err)) { await DrainBodyAsync(ns, headers); await WriteJsonAsync(ns, new { ok = false, error = err }); return; }
    if (IsRootItself(full)) { await DrainBodyAsync(ns, headers); await WriteJsonAsync(ns, new { ok = false, error = "拒绝写入部署根自身" }); return; }
    long len = headers.TryGetValue("Content-Length", out string? cl) && long.TryParse(cl, out long l) ? l : -1;
    if (len < 0) { await DrainBodyAsync(ns, headers); await WriteJsonAsync(ns, new { ok = false, error = "缺少 Content-Length" }); return; }
    long got = 0;
    byte[] buf = new byte[1 << 20];
    // ★ 写 .part 再改名：原来直接 FileMode.Create 写最终路径，断连/重启会在原处留下
    //   被截断或写了一半的 .wld/.utpl/.dll —— 服务端可能直接加载坏文件（是数据损坏，不只是失败）。
    string part = full + ".part";
    try
    {
        // ★ R5：原来这两句在 try 之外 —— 父路径是文件（subdir/inner.txt/child.bin）时异常跑出 try → 客户端收不到任何响应
        if (Directory.Exists(full)) { throw new IOException("目标是目录，不能写入：" + rel); }
        Directory.CreateDirectory(Path.GetDirectoryName(full) ?? root);
        using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            while (got < len)
            {
                // ★ R3：异步读不受 NetworkStream.ReadTimeout 约束，必须显式加超时 ——
                //   否则"声明 5GB 只发 100 字节"的连接会把 .part 永久占住（D 子代理实测 11 秒后仍在）。
                int n = await ns.ReadAsync(buf, 0, (int)Math.Min(buf.Length, len - got)).WaitAsync(TimeSpan.FromSeconds(60));
                if (n <= 0) { break; }
                fs.Write(buf, 0, n);
                got += n;
            }
            fs.Flush(true);
        }
        if (got != len)
        {
            try { File.Delete(part); } catch { }
            await WriteJsonAsync(ns, new { ok = false, bytes = got, error = "传输未完成（连接中断/超时），已丢弃半截文件" });
            return;
        }
        File.Move(part, full, overwrite: true);      // 同卷原子替换（★ 也挪进 try：原来在 try 外，出错就是零响应）
    }
    catch (Exception ex)
    {
        try { File.Delete(part); } catch { }
        await WriteJsonAsync(ns, new { ok = false, error = ex.Message });
        return;
    }
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
    // ★★ 绝不能删部署根自身！ResolveSafe 允许"root 自身"（列目录/统计要用），但删除必须显式拒绝 ——
    //   否则 GET /files/delete?path=（空路径）会把整个部署目录递归删掉。D 子代理实测：{"ok":true} + root_gone=true。
    if (IsRootItself(full)) { await WriteJsonAsync(ns, new { ok = false, error = "拒绝对部署根自身执行删除" }); return; }
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
    byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload, jsonOpts);
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