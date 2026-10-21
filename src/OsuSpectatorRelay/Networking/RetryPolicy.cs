namespace OsuSpectatorRelay.Networking;

public sealed class NonRetryableException(string message, bool requestProbe = false) : IOException(message)
{
    public bool RequestProbe { get; } = requestProbe;
}

public static class RetryPolicy
{
    public static async Task<T> ExecuteAsync<T>(int attempts, int delayMs, Func<int, CancellationToken, Task<T>> action,
        CancellationToken token, Action<int>? onRetry = null)
    {
        for (int attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { return await action(attempt, token).ConfigureAwait(false); }
            catch (Exception e) when (e is not NonRetryableException && !token.IsCancellationRequested &&
                attempt + 1 < attempts && (e is IOException or System.Net.Sockets.SocketException or
                    System.Net.Http.HttpRequestException or System.Net.WebSockets.WebSocketException or OperationCanceledException))
            {
                onRetry?.Invoke(attempt + 1);
                int backoff = Math.Min(4000, delayMs * (1 << Math.Min(attempt, 4)));
                await Task.Delay(backoff + Random.Shared.Next(0, Math.Max(1, delayMs / 4)), token).ConfigureAwait(false);
            }
        }
    }
}
