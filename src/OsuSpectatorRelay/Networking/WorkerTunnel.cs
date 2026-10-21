using System.Net.WebSockets;

namespace OsuSpectatorRelay.Networking;

public sealed class WorkerTunnel(ClientWebSocket socket, HttpMessageInvoker invoker, string? entryIp = null) : IAsyncDisposable
{
    public string? EntryIp { get; } = entryIp;
    public string EntryLabel => EntryIp ?? "DNS（域名解析，无固定 IP）";

    public async Task RelayAsync(Stream local, byte[] initialData, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task send = SendAsync(local, initialData, lifetime.Token);
        Task receive = ReceiveAsync(local, lifetime.Token);
        try
        {
            var completed = await Task.WhenAny(send, receive).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
        }
        finally
        {
            lifetime.Cancel(); socket.Abort();
            try { await Task.WhenAll(send, receive).ConfigureAwait(false); }
            catch (Exception) when (!token.IsCancellationRequested) { /* first completion/error is preserved */ }
        }
    }

    private async Task SendAsync(Stream local, byte[] initial, CancellationToken token)
    {
        if (initial.Length > 0) await SendFrameAsync(initial, token).ConfigureAwait(false);
        byte[] buffer = new byte[32768];
        while (true)
        {
            int count;
            try { count = await local.ReadAsync(buffer, token).ConfigureAwait(false); }
            catch (IOException) { throw new RelayFault(RelayFaultArea.LocalClient, "游戏关闭或中断本地连接。", EntryIp); }
            if (count == 0) return;
            await SendFrameAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
    }

    private async Task SendFrameAsync(ReadOnlyMemory<byte> data, CancellationToken token)
    {
        try { await socket.SendAsync(data, WebSocketMessageType.Binary, true, token).ConfigureAwait(false); }
        catch (WebSocketException) { throw new RelayFault(RelayFaultArea.ClientWorker, "外层 WebSocket 发送失败。", EntryIp); }
    }

    private async Task ReceiveAsync(Stream local, CancellationToken token)
    {
        byte[] buffer = new byte[32768];
        while (true)
        {
            ValueWebSocketReceiveResult result;
            try { result = await socket.ReceiveAsync(buffer.AsMemory(), token).ConfigureAwait(false); }
            catch (WebSocketException)
            { throw new RelayFault(RelayFaultArea.ClientWorker, "外层 WebSocket 意外断开，未收到 Worker 的上游故障报告。", EntryIp); }
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (RelayFault.FromClose(socket.CloseStatus, EntryIp) is { } fault) throw fault;
                return;
            }
            if (result.MessageType != WebSocketMessageType.Binary)
                throw new RelayFault(RelayFaultArea.ClientWorker, "Worker 返回了非二进制隧道数据。", EntryIp);
            try { await local.WriteAsync(buffer.AsMemory(0, result.Count), token).ConfigureAwait(false); }
            catch (IOException) { throw new RelayFault(RelayFaultArea.LocalClient, "游戏不再接收本地数据。", EntryIp); }
        }
    }

    public ValueTask DisposeAsync() { socket.Dispose(); invoker.Dispose(); return ValueTask.CompletedTask; }
}
