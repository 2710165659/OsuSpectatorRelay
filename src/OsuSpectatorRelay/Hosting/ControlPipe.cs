using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace OsuSpectatorRelay.Hosting;

public sealed record ControlRequest(string Command, int Pid = 0);
public sealed record RelayStatus(int Pid, int Port, string Mode, string SelectedIp, int Connections, string? LastError);

public static class ControlPipe
{
    public static async Task<RelayStatus?> SendAsync(AppFiles files, ControlRequest request, int timeoutMs = 1000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", files.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, RelayJsonContext.Default.ControlRequest).AsMemory(), timeout.Token).ConfigureAwait(false);
            string? response = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            return response is null ? null : JsonSerializer.Deserialize(response, RelayJsonContext.Default.RelayStatus);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or JsonException) { return null; }
    }

    public static async Task ListenAsync(AppFiles files, Func<ControlRequest, RelayStatus> handle, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(files.PipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                string? line = await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false);
                if (line is null || line.Length > 4096) continue;
                var request = JsonSerializer.Deserialize(line, RelayJsonContext.Default.ControlRequest);
                if (request is null) continue;
                var result = handle(request);
                await writer.WriteLineAsync(JsonSerializer.Serialize(result, RelayJsonContext.Default.RelayStatus).AsMemory(), deadline.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or JsonException or OperationCanceledException) { }
        }
    }
}
