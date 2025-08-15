using Lantern.Discv5.Enr;
using Lantern.Discv5.WireProtocol.Packet.Headers;
using Lantern.Discv5.WireProtocol.Packet.Types;

namespace Lantern.Discv5.WireProtocol.Session;

public interface ISessionMain
{
    bool IsEstablished { get; }

    byte[] MessageCount { get; }

    byte[] PublicKey { get; }

    byte[] EphemeralPublicKey { get; }

    void SetChallengeData(byte[] maskingIv, byte[] header);

    byte[]? GenerateIdSignature(byte[] destNodeId);

    bool VerifyIdSignature(HandshakePacketBase handshakePacket, byte[] publicKey, byte[] selfNodeId);

    byte[]? EncryptMessageWithNewKeys(IEnr dest, StaticHeader header, byte[] selfNodeId, byte[] message,
        byte[] maskingIv);

    byte[]? DecryptMessageWithNewKeys(StaticHeader header, byte[] maskingIv, byte[] encryptedMessage,
        HandshakePacketBase handshakePacket, byte[] selfNodeId);

    byte[]? EncryptMessage(StaticHeader header, byte[] maskingIv, byte[] rawMessage);

    byte[]? DecryptMessage(StaticHeader header, byte[] maskingIv, byte[] encryptedMessage);
}