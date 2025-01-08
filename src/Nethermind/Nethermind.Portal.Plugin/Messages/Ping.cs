using Nethermind.Serialization.Ssz;

namespace Nethermind.Network.Portal.Messages;

[SszSerializable]
public class Ping
{
    public ulong EnrSeq { get; set; }

    [SszList(64000)] // TODO: Check limit
    public byte[] CustomPayload { get; set; } = [];
}
