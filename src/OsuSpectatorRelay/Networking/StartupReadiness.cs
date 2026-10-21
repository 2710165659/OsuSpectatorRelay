using System.Net;

namespace OsuSpectatorRelay.Networking;

public static class StartupReadiness
{
    public static async Task WarmUpAsync(AppConfig config, int port, RelayLog log, CancellationToken token = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(config.ConnectionBudgetSeconds));
        using var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{port}"), UseProxy = true,
            AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(config.ConnectTimeoutSeconds)
        };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        log.Write("启动 osu 前预热实时服务线路（不读取游戏账号或登录凭据）。");
        try
        {
            await RetryPolicy.ExecuteAsync(config.RetryAttempts, config.RetryDelayMilliseconds, async (_, retryToken) =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    "https://spectator.osu.ppy.sh/multiplayer/negotiate?negotiateVersion=1")
                {
                    Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                    Content = new ByteArrayContent([])
                };
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, retryToken).ConfigureAwait(false);
                // No osu credentials are sent. 401 is the expected official response;
                // unlike a CF candidate's 403, this verifies reaching the inner TLS endpoint.
                if (response.StatusCode != HttpStatusCode.Unauthorized)
                    throw new HttpRequestException($"实时服务预热返回 HTTP {(int)response.StatusCode}。");
                return true;
            }, budget.Token).ConfigureAwait(false);
            log.Write("实时服务线路预热通过：官方 TLS 与 negotiate 接口可达；游戏仍需自行完成鉴权和 SignalR 握手。");
        }
        catch (Exception e) when (!token.IsCancellationRequested && e is HttpRequestException or IOException or OperationCanceledException)
        {
            // Preserve offline use and never hold the launcher indefinitely.
            log.Write("实时服务预热未通过，将继续启动 osu；多人连接可能需要在线路恢复后重新启动游戏。");
        }
    }
}
