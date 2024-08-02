namespace Nethermind.Network.Discovery.Portal.Messages;

public class Pong
{
    public ulong EnrSeq { get; set; }
    public byte[] CustomPayload { get; set; } = null!;
}
