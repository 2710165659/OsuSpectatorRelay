using System.Net;
using System.Text.Json;

namespace OsuSpectatorRelay.Networking;

public sealed record PreferredIpState(string WorkerAuthority, string? SelectedIp, string?[] Candidates, DateTimeOffset UpdatedAt);

public sealed class PreferredIpCache(AppFiles files, RelayLog log)
{
    private string CachePath => Path.Combine(files.RuntimeDirectory, "preferred-ip.json");

    public PreferredIpState? Load(AppConfig config)
    {
        try
        {
            string authority = config.GetWorkerUri("/").GetLeftPart(UriPartial.Authority);
            if (!File.Exists(CachePath)) return ImportPreviousStatus(config);
            var state = JsonSerializer.Deserialize(File.ReadAllText(CachePath), AppConfig.JsonContext.PreferredIpState);
            if (state is null || !string.Equals(state.WorkerAuthority, authority, StringComparison.OrdinalIgnoreCase) ||
                state.Candidates is null || state.Candidates.Length is 0 or > 33 ||
                !state.Candidates.Contains(state.SelectedIp) || state.Candidates.Any(ip => ip is not null && !IPAddress.TryParse(ip, out _)))
                return null;
            return state;
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
            log.Write("未能读取上次优选 IP，将重新探测。");
            return null;
        }
    }

    public void Save(AppConfig config, string? selectedIp, string?[] candidates)
    {
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(files.RuntimeDirectory);
            var state = new PreferredIpState(config.GetWorkerUri("/").GetLeftPart(UriPartial.Authority), selectedIp,
                new[] { selectedIp }.Concat(candidates).Distinct().Take(33).ToArray(), DateTimeOffset.UtcNow);
            // Unique temporary names also support concurrent manual checks and the background service.
            temporary = CachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, AppConfig.JsonContext.PreferredIpState));
            File.Move(temporary, CachePath, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { log.Write("无法保存优选 IP；当前连接继续使用内存中的结果。"); }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    public void Clear()
    {
        // Keep a tombstone so a stale status.json cannot resurrect a rejected IP.
        try { File.WriteAllText(CachePath, "null"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.Write("无法清除失效的优选 IP 缓存。"); }
    }

    private PreferredIpState? ImportPreviousStatus(AppConfig config)
    {
        // One-time migration for the previous release, which only saved the IP in status.json.
        string statusPath = Path.Combine(files.RuntimeDirectory, "status.json");
        if (!File.Exists(statusPath)) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(statusPath));
        if (!document.RootElement.TryGetProperty("selectedIp", out var value) || value.ValueKind != JsonValueKind.String ||
            !IPAddress.TryParse(value.GetString(), out var ip) || !config.PreferredIps.Contains(ip.ToString())) return null;
        var state = new PreferredIpState(config.GetWorkerUri("/").GetLeftPart(UriPartial.Authority), ip.ToString(),
            [ip.ToString()], DateTimeOffset.UtcNow);
        Save(config, state.SelectedIp, state.Candidates);
        return state;
    }
}
