using Lantern.Discv5.WireProtocol.Packet.Types;

namespace Lantern.Discv5.WireProtocol.Packet.Handlers;

public interface IPacketHandlerFactory
{
    IPacketHandler GetPacketHandler(PacketType packetType);
}