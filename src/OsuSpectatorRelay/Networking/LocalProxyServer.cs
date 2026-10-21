using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OsuSpectatorRelay.Networking;

public sealed class LocalProxyServer(AppConfig config, RouteMonitor routes, WorkerClient worker, RelayLog log)
{
    private readonly ConcurrentDictionary<long, Task> clients = new();
    private long nextId;
    private long lastActivityTicks = DateTime.UtcNow.Ticks;
    private readonly object summaryGate = new();
    private DateTime lastSummary = DateTime.UtcNow;
    private int openedWorker, openedSystem, ended, failed;
    private string? announcedRoute;
    public int ActiveConnections => clients.Count;
    public DateTime LastActivityUtc => new(Interlocked.Read(ref lastActivityTicks), DateTimeKind.Utc);
    public string? LastError { get; private set; }
    public void Touch() => Interlocked.Exchange(ref lastActivityTicks, DateTime.UtcNow.Ticks);

    public void LogSummary()
    {
        lock (summaryGate)
        {
            if (DateTime.UtcNow - lastSummary < TimeSpan.FromSeconds(30)) return;
            lastSummary = DateTime.UtcNow;
            if (openedWorker + openedSystem + ended + failed == 0) return;
            log.Write($"[连接汇总] 最近 30 秒：新建 Worker 隧道 {openedWorker}，新建系统/直连隧道 {openedSystem}，结束 {ended}，失败 {failed}，当前活动连接 {ActiveConnections}。这是业务连接统计，不是测速。");
            openedWorker = openedSystem = ended = failed = 0;
        }
    }

    private void Connected(string routeKey, string description, bool useWorker)
    {
        lock (summaryGate)
        {
            if (useWorker) openedWorker++; else openedSystem++;
            if (announcedRoute == routeKey) return;
            announcedRoute = routeKey;
            log.Write("[业务连接] " + description + " 后续同路线连接每 30 秒汇总，单条记录不代表持续测速。");
        }
    }

    public async Task RunAsync(CancellationToken token)
    {
        var listener = new TcpListener(IPAddress.Loopback, config.ListenPort);
        listener.Server.ExclusiveAddressUse = true;
        listener.Start(128);
        log.Write($"本地代理就绪：127.0.0.1:{config.ListenPort}");
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                if (clients.Count >= 128) { client.Dispose(); continue; }
                long id = Interlocked.Increment(ref nextId);
                Touch();
                Task task = HandleAsync(client, id, token);
                clients[id] = task;
                _ = task.ContinueWith(_ => { clients.TryRemove(id, out var removed); Touch(); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        finally
        {
            listener.Stop();
            try { await Task.WhenAll(clients.Values).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (Exception e) when (e is OperationCanceledException or TimeoutException) { }
        }
    }

    private async Task HandleAsync(TcpClient client, long id, CancellationToken serviceToken)
    {
        using (client)
        {
            client.NoDelay = true;
            using var local = client.GetStream();
            var snapshot = routes.Capture();
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(serviceToken, snapshot.Token);
            bool started = false;
            bool tracked = false;
            string stage = "osu→本地代理";
            try
            {
                ProxyRequest request;
                using (var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                {
                    headerTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                    request = ProxyRequest.Parse(await HeaderBlock.ReadAsync(local, headerTimeout.Token).ConfigureAwait(false));
                }
                // Do not permit local proxy chains to loop into this listener.
                if (request.Port == config.ListenPort && IsLocalHost(request.Host))
                    throw new IOException("目标为本地代理自身，已阻止循环。");

                ProxyRoute route;
                using (var resolutionTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                {
                    resolutionTimeout.CancelAfter(TimeSpan.FromSeconds(config.ConnectTimeoutSeconds));
                    route = await routes.ResolveAsync(snapshot.Settings, request.ProbeUri, config.EnableWpad, resolutionTimeout.Token).ConfigureAwait(false);
                }
                bool useWorker = !route.IsSystemRoute && config.UsesWorker(request.Host);
                tracked = config.UsesWorker(request.Host);
                stage = useWorker ? "客户端→CF Worker" : route.Proxy is not null ? "客户端→系统代理→目标服务器" : "客户端→目标服务器直连";
                byte[] initial = request.ForwardHeader is null ? request.Remainder : request.ForwardHeader.Concat(request.Remainder).ToArray();
                if (useWorker)
                {
                    await using var tunnel = await worker.ConnectAsync(request.Host, request.Port, lifetime.Token).ConfigureAwait(false);
                    if (request.IsConnect) await EstablishedAsync(local, lifetime.Token).ConfigureAwait(false);
                    started = true;
                    Connected("worker:" + tunnel.EntryLabel, $"osu 实时 TCP 隧道已建立；路线：本地→Worker→{request.Authority}；本次优选 IP：{tunnel.EntryLabel}。", true);
                    await tunnel.RelayAsync(local, initial, lifetime.Token).ConfigureAwait(false);
                }
                else
                {
                    Stream remote;
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                    {
                        timeout.CancelAfter(TimeSpan.FromSeconds(config.ConnectionBudgetSeconds));
                        remote = await RetryPolicy.ExecuteAsync(config.RetryAttempts, config.RetryDelayMilliseconds, async (_, retryToken) =>
                        {
                            using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(retryToken);
                            attemptTimeout.CancelAfter(TimeSpan.FromSeconds(config.ConnectTimeoutSeconds));
                            return await TcpTransport.ConnectAsync(request.Host, request.Port, route.Proxy, config.ListenPort, attemptTimeout.Token).ConfigureAwait(false);
                        }, timeout.Token).ConfigureAwait(false);
                    }
                    await using (remote)
                    {
                        if (request.IsConnect) await EstablishedAsync(local, lifetime.Token).ConfigureAwait(false);
                        started = true;
                        if (initial.Length > 0) await remote.WriteAsync(initial, lifetime.Token).ConfigureAwait(false);
                        if (tracked) Connected("system:" + route.Proxy?.Authority, $"osu 实时 TCP 隧道已建立；目标 {request.Authority}；{(route.Proxy is null ? "遵循系统绕过规则直连" : "使用上游系统代理")}，本次不经过 Worker。", false);
                        await RelayStreamsAsync(local, remote, lifetime.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception) when (lifetime.IsCancellationRequested) { }
            catch (RelayFault fault) when (fault.Area == RelayFaultArea.LocalClient) { /* normal game-side closure */ }
            catch (Exception e)
            {
                LastError = e is RelayFault relayFault ? relayFault.Description : $"[{stage}] " +
                    (e is IOException or InvalidOperationException ? e.Message : e is SocketException socket ? "SocketException: " + socket.SocketErrorCode : e.GetType().Name);
                log.Write($"[连接 #{id}][{(started ? "传输中断" : "建连失败")}] {LastError}");
                lock (summaryGate) failed++;
                if (e is RelayFault fault) worker.RequestRecovery(fault);
                if (!started)
                {
                    try
                    {
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                        await local.WriteAsync("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), deadline.Token).ConfigureAwait(false);
                    }
                    catch (Exception) { }
                }
            }
            finally { if (tracked && started) { lock (summaryGate) ended++; } }
        }
    }

    private static bool IsLocalHost(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
    private static Task EstablishedAsync(Stream local, CancellationToken token) =>
        local.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), token).AsTask();

    public static async Task RelayStreamsAsync(Stream left, Stream right, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task forward = left.CopyToAsync(right, 32768, lifetime.Token);
        Task backward = right.CopyToAsync(left, 32768, lifetime.Token);
        await Task.WhenAny(forward, backward).ConfigureAwait(false);
        lifetime.Cancel();
        try { await Task.WhenAll(forward, backward).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
    }
}
