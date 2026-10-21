using System.Net.NetworkInformation;

namespace OsuSpectatorRelay.Networking;

public sealed class RouteMonitor : IDisposable
{
    private readonly ISystemProxy systemProxy;
    private readonly RelayLog log;
    private readonly object gate = new();
    private ProxySettings settings;
    private CancellationTokenSource generation = new();
    private readonly List<CancellationTokenSource> retired = [];
    private int networkChanged;

    public RouteMonitor(ISystemProxy systemProxy, RelayLog log)
    {
        this.systemProxy = systemProxy;
        this.log = log;
        settings = systemProxy.Read();
        NetworkChange.NetworkAddressChanged += NetworkChanged;
    }

    private void NetworkChanged(object? sender, EventArgs e) => Interlocked.Exchange(ref networkChanged, 1);

    public (ProxySettings Settings, CancellationToken Token) Capture()
    {
        lock (gate) return (settings, generation.Token);
    }

    public async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        ProxySettings? pending = null;
        bool networkPending = false;
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            try
            {
                var next = systemProxy.Read();
                networkPending |= Interlocked.Exchange(ref networkChanged, 0) != 0;
                var current = Capture().Settings;
                if (next == current && !networkPending) { pending = null; continue; }
                // Require one stable poll before switching (avoid transient changes while VPN starts).
                if (pending != next) { pending = next; continue; }
                CancellationTokenSource previous;
                lock (gate)
                {
                    settings = next;
                    previous = generation;
                    generation = new CancellationTokenSource();
                    retired.Add(previous);
                }
                previous.Cancel();
                log.Write("系统代理或网络发生变化，已更新线路并关闭旧连接，等待 osu 自动重连。");
                pending = null;
                networkPending = false;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.Write($"系统代理检测失败，保留当前配置：{e.GetType().Name}");
            }
        }
    }

    public Task<ProxyRoute> ResolveAsync(ProxySettings snapshot, Uri target, bool enableWpad, CancellationToken token)
        => systemProxy.ResolveAsync(snapshot, target, enableWpad, token);

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= NetworkChanged;
        generation.Cancel();
        generation.Dispose();
        foreach (var source in retired) source.Dispose();
    }
}
