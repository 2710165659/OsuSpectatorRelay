using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography.X509Certificates;
using OsuSpectatorRelay;
using OsuSpectatorRelay.Hosting;
using OsuSpectatorRelay.Networking;

static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
static WorkerClient TestClient(AppConfig config, RelayLog log, Func<string?, HttpMessageHandler>? transport = null, PreferredIpCache? cache = null)
    => new(config, log, transport, cache, (_, _) => Task.FromResult<double?>(1));
static SocketsHttpHandler TrustedHandler(TestWorker endpoint, WorkerClient client, string? physicalIp = "127.0.0.1")
{
    var handler = client.CreateHandler(physicalIp);
    handler.SslOptions.CertificateChainPolicy = new X509ChainPolicy
    {
        TrustMode = X509ChainTrustMode.CustomRootTrust,
        RevocationMode = X509RevocationMode.NoCheck
    };
    handler.SslOptions.CertificateChainPolicy.CustomTrustStore.Add(endpoint.Certificate);
    return handler;
}
static int FreePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
}
string temporary = Path.Combine(Path.GetTempPath(), "OsuSpectatorRelayTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
var files = new AppFiles(Path.Combine(temporary, "config.json"));
var log = new RelayLog(files);
var tests = new List<(string, Func<Task>)>();

tests.Add(("configuration: generated key, preservation, exact routing", () =>
{
    var config = AppConfig.Load(files.ConfigPath);
    Assert(config.SharedSecret.Length == 64, "key entropy/length");
    Assert(AppConfig.Load(files.ConfigPath).SharedSecret == config.SharedSecret, "key must survive reload");
    Assert(config.UsesWorker("SPECTATOR.OSU.PPY.SH."), "canonical match");
    Assert(!config.UsesWorker("evil-spectator.osu.ppy.sh") && !config.UsesWorker("spectator.osu.ppy.sh.evil"), "exact match only");
    return Task.CompletedTask;
}));
tests.Add(("CF candidate ranges and sampling match the independent ruleset strategy", () =>
{
    var config = new AppConfig();
    Assert(CloudflareCandidates.Ipv4Ranges.Length == 25, "25 IPv4 ranges");
    Assert(CloudflareCandidates.Ipv4Ranges.Contains("104.16.0.0/12") && CloudflareCandidates.Ipv4Ranges.Contains("172.64.229.0/24"), "same partitioned ranges");
    string[] ipv4 = CloudflareCandidates.Build(config, "192.0.2.1");
    Assert(ipv4.Length == 51 && ipv4.Contains("192.0.2.1"), "two samples per IPv4 range plus current");
    config.EnableIpv6Candidates = true;
    Assert(CloudflareCandidates.Build(config, null).Count(ip => IPAddress.Parse(ip).AddressFamily == AddressFamily.InterNetworkV6) == CloudflareCandidates.Ipv6Ranges.Length, "one IPv6 sample per range");
    return Task.CompletedTask;
}));
tests.Add(("actual Worker HTTP probing rejects 403, redirects, 429, 500 and excludes failed nodes", async () =>
{
    foreach (var status in new[] { 301, 401, 403, 429, 500, 503 })
    {
        int calls = 0;
        var config = new AppConfig { WorkerUrl = "https://relay.invalid", SharedSecret = "A".PadRight(64, 'A'), AutoGenerateCandidates = false, PreferredIps = ["104.16.0.1"] };
        var worker = TestClient(config, log, _ => new FakeHttpHandler(request =>
        {
            Interlocked.Increment(ref calls);
            Assert(request.RequestUri!.AbsolutePath == "/health", "must stop before WebSocket probe");
            Assert(request.Headers.Authorization?.Parameter == config.SharedSecret, "probe authentication");
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("{\"service\":\"osu-spectator-relay\"}") };
        }));
        Assert(!await worker.ProbeAsync(default), $"reject HTTP {status}");
        Assert(calls == 2, "IP and DNS both evaluated");
        try { await worker.ConnectAsync("spectator.osu.ppy.sh", 443, default); throw new Exception("rejected probe was reused"); }
        catch (IOException) { }
        Assert(calls == 2, "failed candidates cannot be reused for tunneling");
    }
    int startupCalls = 0;
    var startupConfig = new AppConfig { WorkerUrl = "https://relay.invalid", SharedSecret = new string('A', 64), AutoGenerateCandidates = false, PreferredIps = ["104.16.0.1"] };
    var startup = TestClient(startupConfig, log, _ => new FakeHttpHandler(request =>
    {
        Interlocked.Increment(ref startupCalls);
        Assert(request.RequestUri!.AbsolutePath == "/health", "startup must verify HTTP before opening tunnels");
        return new HttpResponseMessage(HttpStatusCode.Forbidden);
    }));
    try { await startup.ConnectAsync("spectator.osu.ppy.sh", 443, default); throw new Exception("unverified startup node accepted"); }
    catch (IOException) { }
    Assert(startupCalls == 2, "startup rejects 403 before WebSocket/tunnel attempts");
}));
tests.Add(("preferred IP cache: persists, scopes Worker origin, handles corruption and excludes rejected nodes", async () =>
{
    var cacheFiles = new AppFiles(Path.Combine(temporary, "cache", "config.json"));
    var cacheLog = new RelayLog(cacheFiles);
    var cache = new PreferredIpCache(cacheFiles, cacheLog);
    var config = new AppConfig { WorkerUrl = "https://relay.example", SharedSecret = new string('C', 64), AutoGenerateCandidates = false, PreferredIps = ["104.17.0.1", "104.16.0.1"] };
    cache.Save(config, "104.17.0.1", ["104.17.0.1", null]);
    Assert(cache.Load(config)?.SelectedIp == "104.17.0.1", "saved selection restored");
    config.WorkerUrl = "https://other.example";
    Assert(cache.Load(config) is null, "cache must not cross Worker origins");
    config.WorkerUrl = "https://relay.example";
    var worker = TestClient(config, cacheLog, _ => new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)), cache);
    Assert(worker.SelectedIp == "104.17.0.1", "startup reuses historical IP");
    File.WriteAllText(Path.Combine(cacheFiles.RuntimeDirectory, "status.json"), "{\"selectedIp\":\"104.17.0.1\"}");
    Assert(!await worker.ProbeAsync(default), "403 rejects cached node");
    Assert(cache.Load(config) is null, "failed probe clears cache");
    try { await worker.ConnectAsync("spectator.osu.ppy.sh", 443, default); throw new Exception("rejected cache reused"); }
    catch (IOException) { }
    File.WriteAllText(Path.Combine(cacheFiles.RuntimeDirectory, "preferred-ip.json"), "{");
    Assert(cache.Load(config) is null, "corrupt cache falls back to probing");
    File.Delete(Path.Combine(cacheFiles.RuntimeDirectory, "preferred-ip.json"));
    File.WriteAllText(Path.Combine(cacheFiles.RuntimeDirectory, "status.json"), "{\"selectedIp\":\"104.16.0.1\"}");
    Assert(cache.Load(config)?.SelectedIp == "104.16.0.1", "previous release status migrates");
}));
tests.Add(("Windows proxy: per-scheme endpoints, bypass and PAC DIRECT semantics", async () =>
{
    Assert(WindowsSystemProxy.ParseProxy("http=127.0.0.1:1;https=127.0.0.1:2", "https")!.Port == 2, "https mapping");
    Assert(WindowsSystemProxy.ParseProxy("socks5://127.0.0.1:1080", "https")!.Scheme == "socks5", "socks");
    Assert(WindowsSystemProxy.IsBypassed("localhost", "<local>;*.example.org"), "local bypass");
    Assert(WindowsSystemProxy.IsBypassed("a.example.org", "*.example.org"), "wildcard bypass");
    Assert(!WindowsSystemProxy.IsBypassed("example.org.evil", "*.example.org"), "bypass exact suffix");
    var resolved = await new WindowsSystemProxy().ResolveAsync(new("127.0.0.1:8080", "*.example.org", "", false), new Uri("https://a.example.org"), false, default);
    Assert(resolved.IsSystemRoute && resolved.Proxy is null, "bypassed system route is not Worker fallback");
}));
tests.Add(("retry: bounded failures, fail-fast auth, cancellation", async () =>
{
    int count = 0;
    int value = await RetryPolicy.ExecuteAsync(3, 0, (_, _) => ++count < 3 ? Task.FromException<int>(new IOException()) : Task.FromResult(42), default);
    Assert(count == 3 && value == 42, "retry success");
    count = 0;
    try { await RetryPolicy.ExecuteAsync<int>(3, 0, (_, _) => { count++; throw new NonRetryableException("auth"); }, default); }
    catch (NonRetryableException) { }
    Assert(count == 1, "auth must not retry");
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    try { await RetryPolicy.ExecuteAsync(3, 0, (_, _) => Task.FromResult(0), cancellation.Token); throw new Exception("cancellation lost"); }
    catch (OperationCanceledException) { }
}));
tests.Add(("CONNECT parser preserves pipelined TLS bytes and rejects userinfo", () =>
{
    var request = ProxyRequest.Parse(new HeaderBlock("CONNECT spectator.osu.ppy.sh:443 HTTP/1.1\r\n\r\n", [22, 3, 3]));
    Assert(request.Host == "spectator.osu.ppy.sh" && request.Remainder.SequenceEqual(new byte[] { 22, 3, 3 }), "CONNECT remainder");
    try { ProxyRequest.Parse(new HeaderBlock("CONNECT user@host:443 HTTP/1.1\r\n\r\n", [])); throw new Exception("userinfo accepted"); }
    catch (InvalidDataException) { }
    return Task.CompletedTask;
}));
tests.Add(("real local CONNECT tunnel: byte-for-byte payload", async () =>
{
    using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var target = new TcpListener(IPAddress.Loopback, 0); target.Start();
    int targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
    Task echo = Task.Run(async () =>
    {
        using var accepted = await target.AcceptTcpClientAsync(lifetime.Token);
        byte[] data = new byte[8];
        await accepted.GetStream().ReadExactlyAsync(data, lifetime.Token);
        await accepted.GetStream().WriteAsync(data, lifetime.Token);
    });
    int proxyPort = FreePort();
    var config = new AppConfig { ListenPort = proxyPort, WorkerHosts = [] };
    using var routes = new RouteMonitor(new FakeSystemProxy(), log);
    var server = new LocalProxyServer(config, routes, TestClient(config, log), log);
    Task running = server.RunAsync(lifetime.Token);
    try
    {
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, proxyPort, lifetime.Token);
        var stream = client.GetStream();
        byte[] nonce = [22, 3, 3, 0, 1, 255, 0, 87];
        byte[] header = Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{targetPort} HTTP/1.1\r\nHost: 127.0.0.1:{targetPort}\r\n\r\n");
        await stream.WriteAsync(header.Concat(nonce).ToArray(), lifetime.Token);
        var response = await HeaderBlock.ReadAsync(stream, lifetime.Token);
        Assert(response.Text.StartsWith("HTTP/1.1 200"), "CONNECT 200");
        byte[] received = new byte[8]; response.Remainder.CopyTo(received, 0);
        await stream.ReadExactlyAsync(received.AsMemory(response.Remainder.Length), lifetime.Token);
        Assert(received.SequenceEqual(nonce), "opaque TLS bytes preserved");
        await echo;
    }
    finally
    {
        lifetime.Cancel(); target.Stop();
        try { await running; } catch (OperationCanceledException) { }
    }
}));
tests.Add(("upstream HTTP proxy is used, unavailable VPN never switches to Worker", async () =>
{
    using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var upstream = new TcpListener(IPAddress.Loopback, 0); upstream.Start();
    int upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
    string? connect = null;
    Task upstreamTask = Task.Run(async () =>
    {
        using var accepted = await upstream.AcceptTcpClientAsync(lifetime.Token);
        var stream = accepted.GetStream();
        connect = (await HeaderBlock.ReadAsync(stream, lifetime.Token)).Text;
        await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), lifetime.Token);
        byte[] bytes = new byte[4]; await stream.ReadExactlyAsync(bytes, lifetime.Token);
        await stream.WriteAsync(bytes, lifetime.Token);
    });
    var fake = new FakeSystemProxy { Settings = new($"127.0.0.1:{upstreamPort}", "", "", false) };
    var config = new AppConfig { ListenPort = FreePort(), RetryAttempts = 1 };
    using var routes = new RouteMonitor(fake, log);
    var server = new LocalProxyServer(config, routes, TestClient(config, log), log);
    Task running = server.RunAsync(lifetime.Token);
    try
    {
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, config.ListenPort, lifetime.Token);
        var stream = client.GetStream();
        await stream.WriteAsync("CONNECT spectator.osu.ppy.sh:443 HTTP/1.1\r\n\r\n"u8.ToArray(), lifetime.Token);
        Assert((await HeaderBlock.ReadAsync(stream, lifetime.Token)).Text.Contains("200"), "upstream tunnel");
        await stream.WriteAsync(new byte[] { 1, 2, 3, 4 }, lifetime.Token);
        byte[] reply = new byte[4]; await stream.ReadExactlyAsync(reply, lifetime.Token);
        Assert(reply.SequenceEqual(new byte[] { 1, 2, 3, 4 }), "proxy bytes");
        await upstreamTask;
        Assert(connect!.StartsWith("CONNECT spectator.osu.ppy.sh:443"), "upstream requested target, not Worker");
        upstream.Stop();
        using var failed = new TcpClient(); await failed.ConnectAsync(IPAddress.Loopback, config.ListenPort, lifetime.Token);
        await failed.GetStream().WriteAsync("CONNECT spectator.osu.ppy.sh:443 HTTP/1.1\r\n\r\n"u8.ToArray(), lifetime.Token);
        Assert((await HeaderBlock.ReadAsync(failed.GetStream(), lifetime.Token)).Text.Contains("502"), "unavailable VPN returns 502");
        Assert(!logContainsSecret(), "no secret logging");
    }
    finally { lifetime.Cancel(); upstream.Stop(); try { await running; } catch (OperationCanceledException) { } }
    bool logContainsSecret() => File.ReadAllText(Path.Combine(files.RuntimeDirectory, "relay.log")).Contains(AppConfig.Load(files.ConfigPath).SharedSecret);
}));
tests.Add(("network monitor cancels old connection generation after proxy toggle", async () =>
{
    var fake = new FakeSystemProxy();
    using var routes = new RouteMonitor(fake, log);
    var before = routes.Capture();
    using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    Task monitor = routes.RunAsync(lifetime.Token);
    fake.Settings = new("127.0.0.1:9999", "", "", false);
    try
    {
        await Task.Delay(4500, lifetime.Token);
        Assert(before.Token.IsCancellationRequested, "old generation canceled");
        Assert(routes.Capture().Settings.Proxy.EndsWith(":9999"), "new proxy installed");
    }
    finally { lifetime.Cancel(); try { await monitor; } catch (OperationCanceledException) { } }
}));
tests.Add(("WebSocket tunnel preserves binary stream including fragmented frames", async () =>
{
    using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    int port = FreePort();
    using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
    Task echo = Task.Run(async () =>
    {
        var context = await listener.GetContextAsync().WaitAsync(lifetime.Token);
        using var peer = (await context.AcceptWebSocketAsync(null)).WebSocket;
        byte[] buffer = new byte[1024];
        var received = await peer.ReceiveAsync(buffer.AsMemory(), lifetime.Token);
        Assert(received.Count == 8, "initial payload");
        await peer.SendAsync(buffer.AsMemory(0, 3), WebSocketMessageType.Binary, false, lifetime.Token);
        await peer.SendAsync(buffer.AsMemory(3, 5), WebSocketMessageType.Binary, true, lifetime.Token);
        await peer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", lifetime.Token);
        await Task.Delay(200, lifetime.Token);
    });
    using var ws = new ClientWebSocket(); await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), lifetime.Token);
    var localListener = new TcpListener(IPAddress.Loopback, 0); localListener.Start();
    using var readClient = new TcpClient();
    await readClient.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)localListener.LocalEndpoint).Port, lifetime.Token);
    using var writeClient = await localListener.AcceptTcpClientAsync(lifetime.Token);
    await using var tunnel = new WorkerTunnel(ws, new HttpMessageInvoker(new HttpClientHandler()));
    byte[] nonce = [0, 255, 22, 3, 3, 1, 2, 3];
    Task relay = tunnel.RelayAsync(writeClient.GetStream(), nonce, lifetime.Token);
    byte[] result = new byte[8];
    Task reading = readClient.GetStream().ReadExactlyAsync(result, lifetime.Token).AsTask();
    Task first = await Task.WhenAny(reading, relay, echo);
    await first;
    if (echo.IsFaulted) await echo;
    if (relay.IsFaulted) await relay;
    await reading;
    Assert(result.SequenceEqual(nonce), "binary frames reassembled without mutation");
    await relay; await echo; localListener.Stop();
}));
tests.Add(("preferred IP: real HTTPS + WSS + CONNECT retain Worker SNI, certificate validation and authentication", async () =>
{
    using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    string key = "B".PadRight(64, 'B');
    await using var endpoint = new TestWorker(key);
    var config = new AppConfig
    {
        WorkerUrl = $"https://relay.test:{endpoint.Port}", SharedSecret = key,
        AutoGenerateCandidates = false, PreferredIps = ["127.0.0.1"], ConnectTimeoutSeconds = 5, ListenPort = FreePort()
    };
    var cacheFiles = new AppFiles(Path.Combine(temporary, "tls-cache", "config.json"));
    var cache = new PreferredIpCache(cacheFiles, log);
    cache.Save(config, "127.0.0.1", ["127.0.0.1"]);
    WorkerClient? worker = null;
    worker = TestClient(config, log, ip =>
    {
        var handler = worker!.CreateHandler(ip);
        handler.SslOptions.CertificateChainPolicy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck
        };
        handler.SslOptions.CertificateChainPolicy.CustomTrustStore.Add(endpoint.Certificate);
        return handler;
    }, cache);
    Assert(worker.SelectedIp == "127.0.0.1", "historical IP immediately selected");
    endpoint.HealthGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task<bool> probing = worker.ProbeAsync(lifetime.Token);
    await endpoint.HealthRequested.Task.WaitAsync(lifetime.Token);
    using var routes = new RouteMonitor(new FakeSystemProxy(), log);
    var proxy = new LocalProxyServer(config, routes, worker, log);
    Task running = proxy.RunAsync(lifetime.Token);
    try
    {
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, config.ListenPort, lifetime.Token);
        var stream = client.GetStream();
        byte[] nonce = [22, 3, 3, 0, 255, 0, 1, 2];
        byte[] header = "CONNECT spectator.osu.ppy.sh:443 HTTP/1.1\r\n\r\n"u8.ToArray();
        await stream.WriteAsync(header.Concat(nonce).ToArray(), lifetime.Token);
        var response = await HeaderBlock.ReadAsync(stream, lifetime.Token);
        Assert(response.Text.Contains("200"), "Worker tunnel must establish");
        byte[] received = new byte[8]; response.Remainder.CopyTo(received, 0);
        await stream.ReadExactlyAsync(received.AsMemory(response.Remainder.Length), lifetime.Token);
        Assert(received.SequenceEqual(nonce), "TLS payload preserved through WSS");
        Assert(endpoint.TunnelHeaders.Single().Contains("X-Relay-Host: spectator.osu.ppy.sh", StringComparison.OrdinalIgnoreCase), "authenticated target metadata");
        Assert(!probing.IsCompleted, "cached tunnel works while background probe is still pending");
        endpoint.HealthGate.SetResult();
        Assert(await probing, "HTTPS and binary WebSocket probe must succeed");
        Assert(endpoint.SniNames.Count >= 3 && endpoint.SniNames.All(n => n == "relay.test"), "SNI must be domain, never pinned IP");
        Assert(cache.Load(config)?.SelectedIp == "127.0.0.1", "new result persists");
    }
    finally { lifetime.Cancel(); try { await running; } catch (OperationCanceledException) { } }
}));
tests.Add(("HTTP ranking is separate from TCP latency and WebSocket confirmation time", async () =>
{
    using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    await using var endpoint = new TestWorker(new string('D', 64));
    var config = new AppConfig
    {
        WorkerUrl = $"https://relay.test:{endpoint.Port}", SharedSecret = new string('D', 64),
        AutoGenerateCandidates = false, PreferredIps = ["127.0.0.1", "127.0.0.2"]
    };
    WorkerClient? worker = null;
    worker = new WorkerClient(config, log, ip => new DelayedHandler(TrustedHandler(endpoint, worker!),
        request => request.RequestUri!.AbsolutePath == "/health" ? (ip == "127.0.0.1" ? 20 : 180) : (ip == "127.0.0.1" ? 300 : 0)),
        tcpProbe: (ip, _) => Task.FromResult<double?>(ip == "127.0.0.1" ? 50 : 1));
    Assert(await worker.ProbeAsync(lifetime.Token), "scan completes");
    Assert(worker.SelectedIp == "127.0.0.1", "HTTP-fast IP wins despite slower TCP and WS confirmation");
}));

tests.Add(("recovery confirms transient failures, preserves a good IP, then scans and switches on repeated failure", async () =>
{
    using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    await using var endpoint = new TestWorker(new string('E', 64));
    var config = new AppConfig
    {
        WorkerUrl = $"https://relay.test:{endpoint.Port}", SharedSecret = new string('E', 64),
        AutoGenerateCandidates = false, PreferredIps = ["127.0.0.1", "127.0.0.2"]
    };
    var cacheFiles = new AppFiles(Path.Combine(temporary, "recovery-cache", "config.json"));
    var cache = new PreferredIpCache(cacheFiles, log);
    cache.Save(config, "127.0.0.1", config.PreferredIps);
    int tcpProbes = 0;
    WorkerClient? worker = null;
    worker = new WorkerClient(config, log, _ => TrustedHandler(endpoint, worker!), cache,
        (_, _) => { Interlocked.Increment(ref tcpProbes); return Task.FromResult<double?>(1); });
    endpoint.HealthStatuses.Enqueue(403);
    endpoint.HealthStatuses.Enqueue(200);
    Assert(await worker.MaintainAsync(ProbeTrigger.Failure, lifetime.Token), "second insurance check recovers");
    Assert(worker.SelectedIp == "127.0.0.1" && tcpProbes == 0, "transient failure does not trigger full TCP scan or switch");
    Assert(await worker.MaintainAsync(ProbeTrigger.Failure, lifetime.Token), "second recovery keeps healthy IP");
    Assert(tcpProbes == 0, "healthy IP still avoids a full scan");
    Assert(await worker.MaintainAsync(ProbeTrigger.Failure, lifetime.Token), "third recovery runs full scan");
    Assert(worker.SelectedIp == "127.0.0.2" && tcpProbes == 2, "different explicit IP chosen, DNS remains fallback");
    Assert(cache.Load(config)?.SelectedIp == "127.0.0.2", "replacement persisted for next launch");
    endpoint.HealthStatuses.Enqueue(403);
    endpoint.HealthStatuses.Enqueue(403);
    Assert(await worker.MaintainAsync(ProbeTrigger.Periodic, lifetime.Token), "two failed checks trigger recovery scan");
    Assert(tcpProbes == 4, "confirmed failure rescans candidates");
}));

tests.Add(("upstream 502 is attributed without replacing a healthy CF entry", async () =>
{
    using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    await using var endpoint = new TestWorker(new string('F', 64)) { FailUpstreamConnect = true };
    var config = new AppConfig
    {
        WorkerUrl = $"https://relay.test:{endpoint.Port}", SharedSecret = new string('F', 64),
        AutoGenerateCandidates = false, PreferredIps = ["127.0.0.1"], RetryAttempts = 2, RetryDelayMilliseconds = 0
    };
    var cache = new PreferredIpCache(new AppFiles(Path.Combine(temporary, "upstream-cache", "config.json")), log);
    cache.Save(config, "127.0.0.1", config.PreferredIps);
    int tcpProbes = 0;
    WorkerClient? worker = null;
    worker = new WorkerClient(config, log, _ => TrustedHandler(endpoint, worker!), cache,
        (_, _) => { Interlocked.Increment(ref tcpProbes); return Task.FromResult<double?>(1); });
    try { await worker.ConnectAsync("spectator.osu.ppy.sh", 443, lifetime.Token); throw new Exception("expected upstream failure"); }
    catch (RelayFault fault) { Assert(fault.Area == RelayFaultArea.WorkerUpstream, "handshake stage header is preserved"); }
    Assert(endpoint.TunnelHeaders.Count == 2, "upstream errors retry within the budget");
    Assert(worker.HasPendingRecovery && await worker.RecoverPendingAsync(lifetime.Token), "pending recovery checks the entry");
    Assert(worker.SelectedIp == "127.0.0.1" && tcpProbes == 0, "upstream failure keeps a healthy entry without full scan");
}));

tests.Add(("WebSocket fault reports distinguish upstream, outer transport and legacy unknown errors", async () =>
{
    using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    await using var endpoint = new TestWorker(new string('G', 64));
    var config = new AppConfig
    {
        WorkerUrl = $"https://relay.test:{endpoint.Port}", SharedSecret = new string('G', 64),
        AutoGenerateCandidates = false, PreferredIps = ["127.0.0.1"]
    };
    var cache = new PreferredIpCache(new AppFiles(Path.Combine(temporary, "close-cache", "config.json")), log);
    cache.Save(config, "127.0.0.1", config.PreferredIps);
    WorkerClient? worker = null;
    worker = TestClient(config, log, _ => TrustedHandler(endpoint, worker!), cache);
    foreach (var (code, area) in new[] { (4001, RelayFaultArea.WorkerUpstream), (4004, RelayFaultArea.ClientWorker), (1011, RelayFaultArea.Unknown) })
    {
        endpoint.TunnelCloseStatus = (WebSocketCloseStatus)code;
        await using var tunnel = await worker.ConnectAsync("spectator.osu.ppy.sh", 443, lifetime.Token);
        var localListener = new TcpListener(IPAddress.Loopback, 0); localListener.Start();
        try
        {
            using var readClient = new TcpClient();
            await readClient.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)localListener.LocalEndpoint).Port, lifetime.Token);
            using var writeClient = await localListener.AcceptTcpClientAsync(lifetime.Token);
            try { await tunnel.RelayAsync(writeClient.GetStream(), [], lifetime.Token); throw new Exception("expected close fault"); }
            catch (RelayFault fault) { Assert(fault.Area == area && fault.EntryIp == "127.0.0.1", "correct failure leg and actual entry"); }
        }
        finally { localListener.Stop(); }
    }
}));

tests.Add(("real hidden service: readiness IPC, status, stop, no Windows proxy mutation", (Func<Task>)(async () =>
{
    var before = new WindowsSystemProxy().Read();
    var config = AppConfig.Load(files.ConfigPath); config.ListenPort = FreePort();
    File.WriteAllText(files.ConfigPath, JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    string executable = Path.ChangeExtension(typeof(AppConfig).Assembly.Location, ".exe");
    using var process = Process.Start(new ProcessStartInfo(executable)
    {
        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
        ArgumentList = { "serve", "--config", files.ConfigPath }
    })!;
    try
    {
        RelayStatus? status = null;
        for (int i = 0; i < 30 && status is null; i++)
        {
            await Task.Delay(100);
            status = await ControlPipe.SendAsync(files, new("ping"), 200);
        }
        Assert(status?.Pid == process.Id && status.Port == config.ListenPort, "IPC ready and correct process");
        await ControlPipe.SendAsync(files, new("stop"));
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
        Assert(process.ExitCode == 0, "clean service stop");
        Assert(new WindowsSystemProxy().Read() == before, "Windows proxy untouched");
    }
    finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
})));

tests.Add(("startup preflight uses the local proxy, retries and keeps offline launching bounded", async () =>
{
    using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    int connects = 0;
    Task failingProxy = Task.Run(async () =>
    {
        while (!lifetime.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(lifetime.Token);
            var header = await HeaderBlock.ReadAsync(client.GetStream(), lifetime.Token);
            Assert(header.Text.StartsWith("CONNECT spectator.osu.ppy.sh:443"), "preflight must traverse configured local proxy");
            Interlocked.Increment(ref connects);
            await client.GetStream().WriteAsync("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), lifetime.Token);
        }
    });
    try
    {
        var config = new AppConfig { RetryAttempts = 2, RetryDelayMilliseconds = 0, ConnectionBudgetSeconds = 2, ConnectTimeoutSeconds = 1 };
        await StartupReadiness.WarmUpAsync(config, port, log, lifetime.Token);
        Assert(connects >= 2, "preflight retries transport failures");
    }
    finally { lifetime.Cancel(); listener.Stop(); try { await failingProxy; } catch (OperationCanceledException) { } }
}));

tests.Add(("game exit stops service despite residual clients; executable handoff keeps it alive", async () =>
{
    string gameExe = Path.Combine(AppContext.BaseDirectory, "TestGame", "OsuSpectatorRelay.TestGame.exe");
    Process NewGame() => Process.Start(new ProcessStartInfo(gameExe)
    {
        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
    })!;
    using var bootstrap = NewGame();
    using var actualGame = NewGame();
    var sessionFiles = new AppFiles(Path.Combine(temporary, "lifetime", "config.json"));
    var config = AppConfig.Load(sessionFiles.ConfigPath);
    config.OsuPath = gameExe; config.ListenPort = FreePort(); config.IdleExitSeconds = 180;
    File.WriteAllText(sessionFiles.ConfigPath, JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    string serviceExe = Path.ChangeExtension(typeof(AppConfig).Assembly.Location, ".exe");
    using var service = Process.Start(new ProcessStartInfo(serviceExe)
    {
        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
        ArgumentList = { "serve", "--config", sessionFiles.ConfigPath }
    })!;
    try
    {
        RelayStatus? status = null;
        for (int i = 0; i < 40 && status is null; i++)
        {
            await Task.Delay(100);
            status = await ControlPipe.SendAsync(sessionFiles, new("ping"), 200);
        }
        Assert(status?.Pid == service.Id, "test service ready");
        Assert(await ControlPipe.SendAsync(sessionFiles, new("register", bootstrap.Id)) is not null, "game registered");
        bootstrap.Kill(); await bootstrap.WaitForExitAsync();
        await Task.Delay(4500);
        Assert(!service.HasExited, "real game discovered after bootstrap exit");
        using var residual = new TcpClient();
        await residual.ConnectAsync(IPAddress.Loopback, config.ListenPort);
        await Task.Delay(100);
        Assert((await ControlPipe.SendAsync(sessionFiles, new("ping")))?.Connections > 0, "residual local connection present");
        actualGame.Kill(); await actualGame.WaitForExitAsync();
        await service.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
        Assert(service.ExitCode == 0, "game exit shuts service down cleanly before idle timeout");
        byte[] buffer = new byte[1];
        Assert(await residual.GetStream().ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(2)) == 0, "residual connection closed");
    }
    finally
    {
        foreach (var process in new[] { bootstrap, actualGame, service })
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
    }
}));

int failures = 0;
try
{
    foreach (var (name, action) in tests)
    {
        try { await action(); Console.WriteLine("PASS " + name); }
        catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + "\n" + e); }
    }
}
finally { /* Leave test logs in the uniquely named temp directory for diagnostics. */ }
Console.WriteLine($"{tests.Count - failures}/{tests.Count} tests passed. Logs: {temporary}");
return failures == 0 ? 0 : 1;

sealed class FakeSystemProxy : ISystemProxy
{
    public ProxySettings Settings { get; set; } = new("", "", "", false);
    public ProxySettings Read() => Settings;
    public Task<ProxyRoute> ResolveAsync(ProxySettings settings, Uri target, bool enableWpad, CancellationToken token)
        => new WindowsSystemProxy().ResolveAsync(settings, target, enableWpad, token);
}

sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        => Task.FromResult(respond(request));
}

sealed class DelayedHandler(HttpMessageHandler inner, Func<HttpRequestMessage, int> delay) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        await Task.Delay(delay(request), token);
        return await base.SendAsync(request, token);
    }
}
