using System.Net.WebSockets;

namespace OsuSpectatorRelay.Networking;

public enum RelayFaultArea { ClientWorker, WorkerUpstream, LocalClient, Unknown }

public sealed class RelayFault(RelayFaultArea area, string detail, string? entryIp = null) : IOException(detail)
{
    public RelayFaultArea Area { get; } = area;
    public string? EntryIp { get; } = entryIp;
    public string Description => $"[{AreaLabel(Area)}] {Message}";
    public static string AreaLabel(RelayFaultArea area) => area switch
    {
        RelayFaultArea.ClientWorker => "客户端→CF Worker",
        RelayFaultArea.WorkerUpstream => "CF Worker→osu 服务器",
        RelayFaultArea.LocalClient => "osu→本地代理",
        _ => "故障段未确定"
    };

    public static RelayFault? FromClose(WebSocketCloseStatus? status, string? entryIp) => (int?)status switch
    {
        4001 => new(RelayFaultArea.WorkerUpstream, "Worker 读取官方服务器数据失败。", entryIp),
        4002 => new(RelayFaultArea.WorkerUpstream, "Worker 写入官方服务器失败。", entryIp),
        4003 => new(RelayFaultArea.WorkerUpstream, "Worker 的上游 TCP 连接异常。", entryIp),
        4004 => new(RelayFaultArea.ClientWorker, "Worker 发送数据至客户端失败。", entryIp),
        4005 => new(RelayFaultArea.ClientWorker, "Worker 收到客户端 WebSocket 错误。", entryIp),
        1000 or null => null,
        1011 => new(RelayFaultArea.Unknown, "Worker 返回通用隧道错误；旧版 Worker 无法区分故障段，请更新 worker.js。", entryIp),
        _ => new(RelayFaultArea.Unknown, $"隧道以 WebSocket 状态 {(int?)status} 关闭。", entryIp)
    };
}
