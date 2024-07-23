namespace Nethermind.Network.Discovery.Portal;

public class Ping
{
    public ulong EnrSeq { get; set; }
    public byte[] CustomPayload { get; set; } = null!;
}
