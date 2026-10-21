using System.Text.Json.Serialization;
using OsuSpectatorRelay.Hosting;
using OsuSpectatorRelay.Networking;

namespace OsuSpectatorRelay;

[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(ControlRequest))]
[JsonSerializable(typeof(RelayStatus))]
[JsonSerializable(typeof(PreferredIpState))]
internal partial class RelayJsonContext : JsonSerializerContext;
