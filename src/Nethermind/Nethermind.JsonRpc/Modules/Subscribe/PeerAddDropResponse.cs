using System.Text.Json.Serialization;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Modules.Subscribe;

public class PeerAddDropResponse
{
    public PeerAddDropResponse(PeerInfo peerInfo, string subscriptionType, string? error)
    {
        Type = subscriptionType;
        Peer = peerInfo.Id;
        Local = peerInfo.Network.LocalHost;
        Remote = peerInfo.Network.RemoteAddress;
        Error = error;
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Type { get; set; }

    [JsonConverter(typeof(PublicKeyHashedConverter))]
    public PublicKey Peer { get; set; }

    public string Local { get; set; }

    public string Remote { get; set; }

    public string? Error { get; set; }
}
