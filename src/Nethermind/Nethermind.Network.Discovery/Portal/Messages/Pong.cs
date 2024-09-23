using Nethermind.Serialization.Ssz;

namespace Nethermind.Network.Discovery.Portal.Messages;

[SszSerializable]
public class Pong
{
    public ulong EnrSeq { get; set; }

    [SszList(64000)] // TODO: Check limit
    public byte[] CustomPayload { get; set; } = [];
}
