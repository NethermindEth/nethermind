using System;
using Nethermind.Serialization.Ssz;
namespace Nethermind.Network.Discovery.Portal;

public class Pong
{
    public ulong EnrSeq { get; set; }
    public byte[] CustomPayload { get; set; } = null!;
}
