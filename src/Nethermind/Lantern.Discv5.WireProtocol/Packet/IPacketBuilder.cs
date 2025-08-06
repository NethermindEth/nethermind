using Lantern.Discv5.Enr;
using Lantern.Discv5.WireProtocol.Packet.Types;

namespace Lantern.Discv5.WireProtocol.Packet;

public interface IPacketBuilder
{
    PacketResult BuildRandomOrdinaryPacket(byte[] destNodeId);

    PacketResult BuildOrdinaryPacket(byte[] message, byte[] destNodeId, byte[] maskingIv, byte[] messageCount);

    PacketResult BuildWhoAreYouPacketWithoutEnr(byte[] destNodeId, byte[] packetNonce,
        byte[] maskingIv);

    PacketResult BuildWhoAreYouPacket(byte[] destNodeId, byte[] packetNonce,
        IEnr dest, byte[] maskingIv);

    PacketResult BuildHandshakePacket(byte[] idSignature, byte[] ephemeralPubKey, byte[] destNodeId, byte[] maskingIv, byte[] messageCount);
}