using Lantern.Discv5.WireProtocol.Packet.Headers;
using System.Diagnostics.CodeAnalysis;

namespace Lantern.Discv5.WireProtocol.Packet;

public interface IPacketProcessor
{
    bool TryGetStaticHeader(byte[] rawPacket, [NotNullWhen(true)] out StaticHeader? staticHeader);

    byte[] GetMaskingIv(byte[] rawPacket);

    byte[]? GetEncryptedMessage(byte[] rawPacket);
}
