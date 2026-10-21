using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;

namespace OsuSpectatorRelay.Networking;

public enum ProbeTrigger { Startup, Periodic, Failure, NetworkChange, Manual }

public sealed class WorkerClient
{
    private sealed record Selection(string? Ip, string?[] Candidates);
    private sealed record RecoveryRequest(RelayFaultArea Area, string? Ip);
    private readonly AppConfig config;
    private readonly RelayLog log;
    private readonly Func<string?, HttpMessageHandler>? transportFactory;
    private readonly PreferredIpCache? cache;
    private readonly WorkerProbe probe;
    private readonly SemaphoreSlim probes = new(1, 1);
    private readonly object selectionGate = new();
    private Selection? selection;
    private RecoveryRequest? pendingRecovery;
    private long maintenanceVersion;
    private int recoveryStreak;

    public WorkerClient(AppConfig config, RelayLog log, Func<string?, HttpMessageHandler>? transportFactory = null,
        PreferredIpCache? cache = null, Func<string, CancellationToken, Task<double?>>? tcpProbe = null)
    {
        this.config = config; this.log = log; this.transportFactory = transportFactory; this.cache = cache;
        probe = new WorkerProbe(config, CreateTransport, tcpProbe);
        if (cache?.Load(config) is { } previous)
        {
            selection = new(previous.SelectedIp, previous.Candidates);
            log.Write($"[启动判定] 恢复上次优选 IP：{Entry(previous.SelectedIp)}；新测速完成前先用此入口。");
        }
        else log.Write("[启动判定] 没有历史优选 IP；首次业务连接前进行 HTTP 测速与隧道确认。");
    }

    public string SelectedIp => Volatile.Read(ref selection) is { Candidates.Length: > 0 } current ? current.Ip ?? "DNS" : "未选定";
    private bool HasSelection => Volatile.Read(ref selection)?.Candidates.Length > 0;
    public static string Entry(string? ip) => ip ?? "DNS（域名解析，无固定 IP）";
    public static bool IsUsableProbeStatus(HttpStatusCode status) => (int)status is >= 200 and < 300;
    private HttpMessageHandler CreateTransport(string? ip) => transportFactory?.Invoke(ip) ?? CreateHandler(ip);

    public SocketsHttpHandler CreateHandler(string? preferredIp)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(config.ConnectTimeoutSeconds)
        };
        if (preferredIp is not null)
        {
            var ip = IPAddress.Parse(preferredIp);
            var expected = config.GetWorkerUri("/");
            handler.ConnectCallback = async (context, token) =>
            {
                if (!context.DnsEndPoint.Host.Equals(expected.DnsSafeHost, StringComparison.OrdinalIgnoreCase) || context.DnsEndPoint.Port != expected.Port)
                    throw new IOException("优选 IP 连接目标与 Worker 不一致。");
                var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(ip, context.DnsEndPoint.Port, token).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            };
        }
        return handler;
    }

    public void RequestRecovery(RelayFault fault)
    {
        if (fault.Area == RelayFaultArea.LocalClient) return;
        if (Interlocked.CompareExchange(ref pendingRecovery, new(fault.Area, fault.EntryIp), null) is not null) return;
        log.Write($"[失败恢复] {fault.Description} 已安排当前入口复测，确认后决定保留或重新测速切换。");
    }

    public bool HasPendingRecovery => Volatile.Read(ref pendingRecovery) is not null;
    public async Task<bool> RecoverPendingAsync(CancellationToken token)
    {
        var request = Interlocked.Exchange(ref pendingRecovery, null);
        if (request is null) return HasSelection;
        try { return await MaintainAsync(ProbeTrigger.Failure, token, request.Area, request.Ip).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            Interlocked.CompareExchange(ref pendingRecovery, request, null);
            throw;
        }
    }

    // Manual checks deliberately run a complete scan. Automatic maintenance first
    // confirms the current IP, avoiding periodic wholesale replacement of healthy IPs.
    public async Task<bool> ProbeAsync(CancellationToken token)
    {
        await probes.WaitAsync(token).ConfigureAwait(false);
        try { return await ScanAsync(ProbeTrigger.Manual, null, token).ConfigureAwait(false); }
        finally { Interlocked.Increment(ref maintenanceVersion); probes.Release(); }
    }

    public async Task<bool> MaintainAsync(ProbeTrigger trigger, CancellationToken token,
        RelayFaultArea failureArea = RelayFaultArea.ClientWorker, string? failedIp = null)
    {
        long before = Volatile.Read(ref maintenanceVersion);
        await probes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // Coalesce recovery from parallel SignalR connections into one scan.
            if ((trigger is ProbeTrigger.Failure or ProbeTrigger.Startup) && before != Volatile.Read(ref maintenanceVersion)) return HasSelection;
            var current = Volatile.Read(ref selection);
            if (trigger == ProbeTrigger.Startup && current?.Candidates.Length == 0) return false;
            int streak = trigger == ProbeTrigger.Failure && failureArea != RelayFaultArea.WorkerUpstream ? ++recoveryStreak : 0;
            if (current?.Candidates.Length > 0)
            {
                log.Write($"[复测] 原因：{Reason(trigger)}；当前优选 IP：{Entry(current.Ip)}。先检查 HTTP 与 WebSocket。");
                bool usable = await probe.VerifyAsync(current.Ip, token).ConfigureAwait(false);
                if (!usable)
                {
                    log.Write("[复测] 首次检查失败，稍后再次确认，避免瞬时波动触发换 IP。");
                    await Task.Delay(500, token).ConfigureAwait(false);
                    usable = await probe.VerifyAsync(current.Ip, token).ConfigureAwait(false);
                }
                if (usable && streak < 3)
                {
                    if (trigger != ProbeTrigger.Failure) recoveryStreak = 0;
                    log.Write($"[复测] 通过，保留当前优选 IP：{Entry(current.Ip)}。" +
                        (trigger == ProbeTrigger.Failure && failureArea == RelayFaultArea.WorkerUpstream
                            ? " 客户端到 Worker 正常；故障由 Worker 上游报告，不因该故障更换 CF 入口。"
                            : trigger == ProbeTrigger.Failure ? $" 本轮失败恢复检查 {streak}/3；累计 3 次时全量重测，优先尝试其他 IP。" : " 无需全量测速。"));
                    return true;
                }
                log.Write(usable ? "[失败恢复] 当前入口复测通过但已累计 3 次失败恢复检查，启动完整测速并优先尝试其他 IP。" : "[复测] 两次检查均失败，启动完整测速。");
            }
            bool result = await ScanAsync(trigger, streak >= 3 ? failedIp ?? current?.Ip : null, token).ConfigureAwait(false);
            recoveryStreak = 0;
            return result;
        }
        finally { Interlocked.Increment(ref maintenanceVersion); probes.Release(); }
    }

    private async Task<bool> ScanAsync(ProbeTrigger trigger, string? avoidIp, CancellationToken token)
    {
        _ = config.GetWorkerUri("/health");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(25));
        var previous = Volatile.Read(ref selection);
        log.Write($"[测速开始] 原因：{Reason(trigger)}；TCP 初筛 → 最快 8 个 HTTPS 响应头测速 → HTTP/WS 复核；现用 IP：{(HasSelection ? Entry(previous?.Ip) : "未选定")}。");
        try
        {
            var scan = await probe.ScanAsync(previous?.Ip, budget.Token).ConfigureAwait(false);
            var good = scan.Results.Where(r => r.Success).OrderBy(r => r.Ip is null)
                .ThenBy(r => avoidIp is not null && r.Ip == avoidIp).ThenBy(r => r.HttpMs).ThenBy(r => r.TcpMs).ToArray();
            log.Write($"[测速结果] 候选 {scan.Total} 个，TCP 通过 {scan.TcpPassed} 个，HTTP 通过 {good.Length} 个（含 DNS 备用）。");
            foreach (var candidate in good)
            {
                if (!await probe.VerifyAsync(candidate.Ip, budget.Token).ConfigureAwait(false)) continue;
                SetSelection(candidate.Ip, good.Select(r => r.Ip).ToArray());
                log.Write($"[优选 IP] {(previous?.Candidates.Length > 0 ? Entry(previous.Ip) : "未选定")} → {Entry(candidate.Ip)}；HTTP {candidate.HttpMs:F0} ms，TCP {candidate.TcpMs:F0} ms，复核通过；后续新连接使用此 IP。");
                return true;
            }
            SetSelection(null, []);
            cache?.Clear();
            log.Write("[测速失败][客户端→CF Worker] 未找到通过 HTTP/WS 复核的入口；检查网络、Worker 地址和密钥。等待下一次恢复检查。");
            return false;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            log.Write($"[测速超时] 本轮达到 25 秒，保留已有结果；当前优选 IP：{(HasSelection ? Entry(Volatile.Read(ref selection)?.Ip) : "未选定")}。");
            return HasSelection;
        }
    }

    private void SetSelection(string? ip, string?[] candidates)
    {
        lock (selectionGate)
        {
            Volatile.Write(ref selection, new(ip, candidates));
            if (candidates.Length > 0) cache?.Save(config, ip, candidates);
        }
    }

    public async Task<WorkerTunnel> ConnectAsync(string host, int port, CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(config.ConnectionBudgetSeconds));
        RelayFault? lastFailure = null;
        try
        {
            if (Volatile.Read(ref selection) is null) await MaintainAsync(ProbeTrigger.Startup, budget.Token).ConfigureAwait(false);
            if (!HasSelection)
                throw new RelayFault(RelayFaultArea.ClientWorker, "尚无可用 Worker 入口，等待后台恢复测速。");
            return await RetryPolicy.ExecuteAsync(config.RetryAttempts, config.RetryDelayMilliseconds, async (attempt, retryToken) =>
            {
                var current = Volatile.Read(ref selection)!;
                if (current.Candidates.Length == 0) throw new RelayFault(RelayFaultArea.ClientWorker, "测速后仍无可用入口。");
                string? ip = current.Ip;
                var socket = new ClientWebSocket();
                var invoker = new HttpMessageInvoker(CreateTransport(ip));
                try
                {
                    socket.Options.CollectHttpResponseDetails = true;
                    socket.Options.SetRequestHeader("Authorization", "Bearer " + config.SharedSecret);
                    socket.Options.SetRequestHeader("X-Relay-Host", host.Trim('[', ']'));
                    socket.Options.SetRequestHeader("X-Relay-Port", port.ToString());
                    socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(retryToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(config.ConnectTimeoutSeconds));
                    await socket.ConnectAsync(new UriBuilder(config.GetWorkerUri("/tunnel")) { Scheme = "wss" }.Uri, invoker, deadline.Token).ConfigureAwait(false);
                    return new WorkerTunnel(socket, invoker, ip);
                }
                catch (Exception e)
                {
                    var status = socket.HttpStatusCode;
                    bool upstream = socket.HttpResponseHeaders?.Any(h => h.Key.Equals("X-Relay-Error-Stage", StringComparison.OrdinalIgnoreCase) && h.Value.Contains("worker-upstream")) == true;
                    socket.Dispose(); invoker.Dispose(); retryToken.ThrowIfCancellationRequested();
                    if (status == HttpStatusCode.Unauthorized) throw new NonRetryableException("[客户端→CF Worker][鉴权] Worker 返回 401，请核对两端密钥。");
                    var fault = upstream
                        ? new RelayFault(RelayFaultArea.WorkerUpstream, $"Worker 上游 TCP 建连失败（HTTP {(int)status}）。", ip)
                        : new RelayFault(RelayFaultArea.ClientWorker, status != 0 ? $"Worker 握手返回 HTTP {(int)status}。" : e is OperationCanceledException ? "Worker 建连超时。" : $"Worker 建连失败（{e.GetType().Name}）。", ip);
                    lastFailure = fault;
                    log.Write($"[连接失败] {fault.Description} 当前入口：{Entry(ip)}；尝试 {attempt + 1}/{config.RetryAttempts}。");
                    if (attempt + 1 < config.RetryAttempts)
                    {
                        log.Write("[失败恢复] 重试前复测入口；必要时完整测速并切换，已转发的数据不会重放。");
                        await MaintainAsync(ProbeTrigger.Failure, retryToken, fault.Area, ip).ConfigureAwait(false);
                    }
                    throw fault;
                }
            }, budget.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (!token.IsCancellationRequested && e is not NonRetryableException)
        {
            var fault = e as RelayFault ?? lastFailure ?? new(RelayFaultArea.ClientWorker, "Worker 建连总时间预算耗尽或传输失败。", Volatile.Read(ref selection)?.Ip);
            if (e is OperationCanceledException) log.Write("[连接超时] 本次建连总时间预算耗尽；最后故障：" + fault.Description);
            RequestRecovery(fault); throw fault;
        }
    }

    private static string Reason(ProbeTrigger trigger) => trigger switch
    {
        ProbeTrigger.Startup => "游戏/后台启动", ProbeTrigger.Periodic => "定时保险检查",
        ProbeTrigger.Failure => "连接失败恢复", ProbeTrigger.NetworkChange => "系统代理或网络变化", _ => "手动检查"
    };
}
