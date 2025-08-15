using Lantern.Discv5.WireProtocol.Packet.Headers;

namespace Lantern.Discv5.WireProtocol.Packet.Types;

public class PacketResult(byte[] packet, StaticHeader header)
{
    public byte[] Packet { get; } = packet;
    public StaticHeader Header { get; } = header;
}