using System.Text.Json.Serialization;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.Core.Crypto;

namespace Nethermind.JsonRpc.Modules.Subscribe;

public class PeerAddDropResponse(PeerInfo peerInfo, string subscriptionType, string? error)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Type { get; set; } = subscriptionType;

    public PublicKey Peer { get; set; } = peerInfo.Id;

    public string Local { get; set; } = peerInfo.Network.LocalHost;

    public string Remote { get; set; } = peerInfo.Network.RemoteAddress;

    public string? Error { get; set; } = error;
}
