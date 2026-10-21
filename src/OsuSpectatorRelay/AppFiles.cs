using System.Security.Cryptography;
using System.Text;

namespace OsuSpectatorRelay;

public sealed class AppFiles
{
    public string ConfigPath { get; }
    public string RuntimeDirectory { get; }
    public string InstanceId { get; }
    public string PipeName => "OsuSpectatorRelay-" + InstanceId;

    public AppFiles(string configPath)
    {
        ConfigPath = Path.GetFullPath(configPath);
        RuntimeDirectory = Path.Combine(Path.GetDirectoryName(ConfigPath)!, "runtime");
        InstanceId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ConfigPath.ToUpperInvariant())))[..20];
    }
}

public sealed class RelayLog
{
    private readonly string path;
    private readonly object gate = new();
    public RelayLog(AppFiles files)
    {
        Directory.CreateDirectory(files.RuntimeDirectory);
        path = Path.Combine(files.RuntimeDirectory, "relay.log");
    }
    public void Write(string message)
    {
        lock (gate)
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > 2_000_000)
                    File.Move(path, path + ".previous", true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {message}{Environment.NewLine}");
            }
            catch (IOException) { }
        }
    }
}
