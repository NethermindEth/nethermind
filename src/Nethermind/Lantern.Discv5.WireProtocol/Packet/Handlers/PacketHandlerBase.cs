using System.Net.Sockets;
using Lantern.Discv5.WireProtocol.Packet.Types;

namespace Lantern.Discv5.WireProtocol.Packet.Handlers;

public abstract class PacketHandlerBase : IPacketHandler
{
    public abstract PacketType PacketType { get; }

    public abstract Task HandlePacket(UdpReceiveResult returnedResult);
}