namespace Nethermind.Network.Discovery.Portal.Messages;

public class Ping
{
    public ulong EnrSeq { get; set; }
    public byte[] CustomPayload { get; set; } = [];
}
