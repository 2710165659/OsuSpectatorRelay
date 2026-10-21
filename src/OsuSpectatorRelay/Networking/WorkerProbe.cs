using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;

namespace OsuSpectatorRelay.Networking;

public sealed record ProbeResult(string? Ip, double HttpMs, double TcpMs, string? Failure = null)
{
    public bool Success => Failure is null;
}

public sealed class WorkerProbe(AppConfig config, Func<string?, HttpMessageHandler> transport,
    Func<string, CancellationToken, Task<double?>>? tcpProbe = null)
{
    public async Task<(int Total, int TcpPassed, ProbeResult[] Results)> ScanAsync(string? current, CancellationToken token)
    {
        string[] candidates = CloudflareCandidates.Build(config, current);
        using var tcpConcurrency = new SemaphoreSlim(16);
        var tcp = await Task.WhenAll(candidates.Select(async ip =>
        {
            await tcpConcurrency.WaitAsync(token).ConfigureAwait(false);
            try { return (Ip: ip, Ms: await (tcpProbe?.Invoke(ip, token) ?? TcpAsync(ip, token)).ConfigureAwait(false)); }
            finally { tcpConcurrency.Release(); }
        })).ConfigureAwait(false);
        // HTTP latency decides the ranking; TCP is only the first-stage filter.
        var finalists = tcp.Where(r => r.Ms.HasValue).OrderBy(r => r.Ms).Take(8).ToList();
        using var httpConcurrency = new SemaphoreSlim(6);
        var results = await Task.WhenAll(finalists.Select(async candidate =>
        {
            await httpConcurrency.WaitAsync(token).ConfigureAwait(false);
            try { return await HttpAsync(candidate.Ip, candidate.Ms!.Value, token).ConfigureAwait(false); }
            finally { httpConcurrency.Release(); }
        })).ConfigureAwait(false);
        // DNS is an independent fallback, never a competitor to a usable explicit IP.
        var dns = await HttpAsync(null, 0, token).ConfigureAwait(false);
        return (candidates.Length, tcp.Count(r => r.Ms.HasValue), results.Append(dns).ToArray());
    }

    private async Task<double?> TcpAsync(string ip, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(1200));
        var address = IPAddress.Parse(ip);
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        var watch = Stopwatch.StartNew();
        try
        {
            await socket.ConnectAsync(address, config.GetWorkerUri("/").Port, timeout.Token).ConfigureAwait(false);
            return watch.Elapsed.TotalMilliseconds;
        }
        catch (Exception e) when (!token.IsCancellationRequested && e is SocketException or IOException or OperationCanceledException)
        { return null; }
    }

    public async Task<ProbeResult> HttpAsync(string? ip, double tcpMs, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(2500, config.ConnectTimeoutSeconds * 1000)));
        try
        {
            using var http = new HttpClient(transport(ip));
            using var request = new HttpRequestMessage(HttpMethod.Get, config.GetWorkerUri("/health"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.SharedSecret);
            request.Headers.UserAgent.ParseAdd("OsuSpectatorRelay-SpeedTest");
            var watch = Stopwatch.StartNew();
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            double elapsed = watch.Elapsed.TotalMilliseconds;
            if (!WorkerClient.IsUsableProbeStatus(response.StatusCode))
                return new(ip, elapsed, tcpMs, $"HTTP {(int)response.StatusCode}");
            // Validate identity without including body transfer or WS in the HTTP timing.
            using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            byte[] buffer = new byte[4096];
            int count = 0, read;
            while (count < buffer.Length && (read = await body.ReadAsync(buffer.AsMemory(count), timeout.Token).ConfigureAwait(false)) > 0) count += read;
            if (!System.Text.Encoding.UTF8.GetString(buffer, 0, count).Contains("osu-spectator-relay", StringComparison.Ordinal))
                return new(ip, elapsed, tcpMs, "响应不是预期 Worker");
            return new(ip, elapsed, tcpMs);
        }
        catch (Exception e) when (!token.IsCancellationRequested && e is HttpRequestException or IOException or OperationCanceledException)
        { return new(ip, 0, tcpMs, e is OperationCanceledException ? "HTTP 超时" : "HTTPS 连接失败"); }
    }

    public async Task<bool> VerifyAsync(string? ip, CancellationToken token)
    {
        if (!(await HttpAsync(ip, 0, token).ConfigureAwait(false)).Success) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(config.ConnectTimeoutSeconds));
        try
        {
            using var socket = new ClientWebSocket();
            using var invoker = new HttpMessageInvoker(transport(ip));
            socket.Options.SetRequestHeader("Authorization", "Bearer " + config.SharedSecret);
            await socket.ConnectAsync(new UriBuilder(config.GetWorkerUri("/probe")) { Scheme = "wss" }.Uri, invoker, timeout.Token).ConfigureAwait(false);
            byte[] nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
            await socket.SendAsync(nonce.AsMemory(), WebSocketMessageType.Binary, true, timeout.Token).ConfigureAwait(false);
            byte[] reply = new byte[nonce.Length];
            int received = 0;
            while (true)
            {
                if (received == reply.Length) return false;
                var result = await socket.ReceiveAsync(reply.AsMemory(received), timeout.Token).ConfigureAwait(false);
                if (result.MessageType != WebSocketMessageType.Binary) return false;
                received += result.Count;
                if (result.EndOfMessage) break;
            }
            socket.Abort();
            return received == nonce.Length && nonce.SequenceEqual(reply);
        }
        catch (Exception e) when (!token.IsCancellationRequested && e is HttpRequestException or WebSocketException or IOException or OperationCanceledException)
        { return false; }
    }
}
