using System.Net.Sockets;
using Lantern.Discv5.WireProtocol.Packet.Types;

namespace Lantern.Discv5.WireProtocol.Packet.Handlers;

public interface IPacketHandler
{
    PacketType PacketType { get; }

    Task HandlePacket(UdpReceiveResult returnedResult);
}