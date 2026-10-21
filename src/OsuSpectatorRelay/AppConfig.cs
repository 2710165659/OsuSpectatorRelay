using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OsuSpectatorRelay;

public sealed class AppConfig
{
    public string OsuPath { get; set; } = "";
    public string[] OsuArguments { get; set; } = [];
    public string WorkerUrl { get; set; } = "https://your-worker.example.workers.dev";
    public string SharedSecret { get; set; } = "";
    public int ListenPort { get; set; } = 17890;
    public string[] WorkerHosts { get; set; } = ["spectator.osu.ppy.sh"];
    public bool RouteAllThroughWorker { get; set; }
    public string[] PreferredIps { get; set; } = [];
    public bool AutoGenerateCandidates { get; set; } = true;
    public bool EnableIpv6Candidates { get; set; }
    public int ProbeIntervalSeconds { get; set; } = 300;
    public int ConnectTimeoutSeconds { get; set; } = 8;
    public int RetryAttempts { get; set; } = 3;
    public int RetryDelayMilliseconds { get; set; } = 500;
    public int ConnectionBudgetSeconds { get; set; } = 25;
    public int IdleExitSeconds { get; set; } = 180;
    public bool EnableWpad { get; set; }
    public bool CheckBeforeLaunch { get; set; } = true;

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    internal static readonly RelayJsonContext JsonContext = new(Json);

    public bool UsesWorker(string host) => RouteAllThroughWorker || WorkerHosts.Any(h =>
        string.Equals(h.Trim().TrimEnd('.'), host.TrimEnd('.'), StringComparison.OrdinalIgnoreCase));

    public Uri GetWorkerUri(string path)
    {
        if (!Uri.TryCreate(WorkerUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || uri.Query != "" || uri.Fragment != "" ||
            uri.Host == "your-worker.example.workers.dev")
            throw new InvalidOperationException("请在 config.json 中填写实际的 HTTPS Worker 根地址（不带路径）。");
        return new Uri(uri, path);
    }

    public void Validate()
    {
        if (ListenPort is < 1024 or > 65535) throw new InvalidOperationException("listenPort 必须介于 1024 与 65535。");
        if (string.IsNullOrEmpty(SharedSecret) || SharedSecret.Length is < 32 or > 512 || SharedSecret.Any(c => c < 33 || c > 126))
            throw new InvalidOperationException("config.json 的 sharedSecret 需要 32–512 个不含空白的 ASCII 字符。");
        if (ConnectTimeoutSeconds is < 1 or > 60 || ProbeIntervalSeconds < 30 || IdleExitSeconds < 15)
            throw new InvalidOperationException("连接超时范围为 1–60 秒；测速间隔至少 30 秒；空闲退出至少 15 秒。");
        if (WorkerHosts is null || PreferredIps is null || OsuArguments is null)
            throw new InvalidOperationException("数组配置不能为 null。");
        if (RetryAttempts is < 1 or > 10 || RetryDelayMilliseconds is < 0 or > 4000 || ConnectionBudgetSeconds is < 1 or > 120)
            throw new InvalidOperationException("retryAttempts 范围 1–10，retryDelayMilliseconds 范围 0–4000，connectionBudgetSeconds 范围 1–120。");
        if (PreferredIps.Length > 32) throw new InvalidOperationException("preferredIps 最多允许 32 个候选。");
        foreach (string host in WorkerHosts)
            if (string.IsNullOrWhiteSpace(host) || Uri.CheckHostName(host.Trim().TrimEnd('.')) == UriHostNameType.Unknown)
                throw new InvalidOperationException("workerHosts 必须是有效的精确主机名，不支持通配符或 URL。");
        foreach (var ip in PreferredIps)
            if (!System.Net.IPAddress.TryParse(ip, out _)) throw new InvalidOperationException("preferredIps 中存在无效 IP。");
    }

    public static AppConfig Load(string path)
    {
        if (!File.Exists(path)) Initialize(path);
        var config = JsonSerializer.Deserialize(File.ReadAllText(path), JsonContext.AppConfig)
                     ?? throw new InvalidOperationException("配置文件为空。");
        if (string.IsNullOrEmpty(config.SharedSecret))
        {
            config.SharedSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            File.WriteAllText(path, JsonSerializer.Serialize(config, JsonContext.AppConfig));
        }
        config.Validate();
        return config;
    }

    public static void Initialize(string path)
    {
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var config = new AppConfig { SharedSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) };
        // CreateNew prevents simultaneous launchers from overwriting a user's key.
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(stream, config, JsonContext.AppConfig);
        }
        catch (IOException) when (File.Exists(path)) { }
    }
}
