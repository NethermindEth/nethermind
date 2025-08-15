using Lantern.Discv5.WireProtocol.Identity;
using Lantern.Discv5.WireProtocol.Packet.Headers;
using Lantern.Discv5.WireProtocol.Session;
using System.Diagnostics.CodeAnalysis;

namespace Lantern.Discv5.WireProtocol.Packet;

public class PacketProcessor(IIdentityManager identityManager, IAesCrypto aesCrypto) : IPacketProcessor
{
    public bool TryGetStaticHeader(byte[] rawPacket, [NotNullWhen(true)] out StaticHeader? staticHeader)
    {
        var decryptedPacket = aesCrypto.AesCtrDecrypt(identityManager.Record.NodeId[..16], rawPacket[..16], rawPacket[16..]);
        return StaticHeader.TryDecodeFromBytes(decryptedPacket!, out staticHeader);
    }

    public byte[] GetMaskingIv(byte[] rawPacket)
    {
        return rawPacket.AsSpan()[..16].ToArray();
    }

    public byte[]? GetEncryptedMessage(byte[] rawPacket)
    {
        if (!TryGetStaticHeader(rawPacket, out StaticHeader? staticHeader))
        {
            return null;
        }

        return rawPacket[^staticHeader.EncryptedMessageLength..];
    }
}
