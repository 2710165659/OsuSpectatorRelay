using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using OsuSpectatorRelay.Networking;

// An actual TLS + HTTP/1.1 WebSocket peer. Its certificate is trusted only by the
// per-test handler; this never changes the machine/user certificate store.
sealed class TestWorker : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentBag<Task> clients = [];
    private readonly Task accepting;
    private readonly string secret;
    public X509Certificate2 Certificate { get; }
    public ConcurrentBag<string> SniNames { get; } = [];
    public ConcurrentBag<string> TunnelHeaders { get; } = [];
    public ConcurrentQueue<int> HealthStatuses { get; } = [];
    public bool FailUpstreamConnect { get; set; }
    public WebSocketCloseStatus? TunnelCloseStatus { get; set; }
    public TaskCompletionSource? HealthGate { get; set; }
    public TaskCompletionSource HealthRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public TestWorker(string secret)
    {
        this.secret = secret;
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=relay.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("relay.test");
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        Certificate = new X509Certificate2(created.Export(X509ContentType.Pfx));
        listener.Start();
        accepting = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(lifetime.Token);
            clients.Add(HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        await using (var tls = new SslStream(client.GetStream()))
        {
            try
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ServerCertificateSelectionCallback = (_, name) => { SniNames.Add(name ?? ""); return Certificate; }
                }, lifetime.Token);
                var header = await HeaderBlock.ReadAsync(tls, lifetime.Token);
                if (!header.Text.Contains("Authorization: Bearer " + secret, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("test authentication missing");
                string path = header.Text.Split(' ')[1];
                if (path == "/health")
                {
                    HealthRequested.TrySetResult();
                    if (HealthGate is { } gate) await gate.Task.WaitAsync(lifetime.Token);
                    int status = HealthStatuses.TryDequeue(out int queued) ? queued : 200;
                    byte[] body = Encoding.UTF8.GetBytes("{\"service\":\"osu-spectator-relay\",\"version\":1}");
                    await tls.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), lifetime.Token);
                    await tls.WriteAsync(body, lifetime.Token);
                    return;
                }
                if (path == "/tunnel")
                {
                    TunnelHeaders.Add(header.Text);
                    if (FailUpstreamConnect)
                    {
                        await tls.WriteAsync("HTTP/1.1 502 Bad Gateway\r\nX-Relay-Error-Stage: worker-upstream\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), lifetime.Token);
                        return;
                    }
                }
                string key = header.Text.Split("\r\n").First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
                string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                await tls.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), lifetime.Token);
                using var ws = WebSocket.CreateFromStream(tls, new WebSocketCreationOptions { IsServer = true });
                if (path == "/tunnel" && TunnelCloseStatus is { } closeStatus)
                {
                    await ws.CloseOutputAsync(closeStatus, "test fault", lifetime.Token);
                    return;
                }
                byte[] buffer = new byte[32768];
                while (!lifetime.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(buffer.AsMemory(), lifetime.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    await ws.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, lifetime.Token);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or WebSocketException or AuthenticationException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); listener.Stop();
        try { await accepting; } catch (OperationCanceledException) { }
        await Task.WhenAll(clients);
        Certificate.Dispose(); lifetime.Dispose();
    }
}
