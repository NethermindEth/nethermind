using Lantern.Discv5.Rlp;
using Lantern.Discv5.WireProtocol.Packet.Headers;

namespace Lantern.Discv5.WireProtocol.Packet.Types;

public class HandshakePacketBase(byte[] idSignature, byte[] ephPubkey, byte[] srcId, byte[]? record = null)
    : PacketBase(PreparePacketBase(idSignature, ephPubkey, srcId, record))
{
    public byte[] IdSignature { get; } = idSignature;

    public byte[] EphPubkey { get; } = ephPubkey;

    public byte[]? SrcId { get; } = srcId;

    public byte[]? Record { get; } = record;

    private static byte[] PreparePacketBase(byte[] idSignature, byte[] ephPubkey, byte[] srcId, byte[]? record = null)
    {
        var authDataHead = ByteArrayUtils.Concatenate(srcId, ByteArrayUtils.ToBigEndianBytesTrimmed(idSignature.Length),
            ByteArrayUtils.ToBigEndianBytesTrimmed(ephPubkey.Length));

        byte[] packetBase;

        if (record != null)
        {
            packetBase = ByteArrayUtils.Concatenate(authDataHead, idSignature, ephPubkey, record);
        }
        else
        {
            packetBase = ByteArrayUtils.Concatenate(authDataHead, idSignature, ephPubkey);
        }

        return packetBase;
    }

    public static HandshakePacketBase CreateFromStaticHeader(StaticHeader header)
    {
        var authData = header.AuthData;
        var index = 0;
        var srcId = authData[..PacketConstants.NodeIdSize];
        index += PacketConstants.NodeIdSize;

        var sigSize = RlpExtensions.ByteArrayToInt32(authData[index..(index + PacketConstants.SigSize)].ToArray());
        index += PacketConstants.SigSize;

        var ephKeySize = RlpExtensions.ByteArrayToInt32(authData[index..(index + PacketConstants.EphemeralKeySize)].ToArray());
        index += PacketConstants.EphemeralKeySize;

        var idSignature = authData[index..(index + sigSize)];
        index += sigSize;

        var ephPubkey = authData[index..(index + ephKeySize)];
        index += ephKeySize;

        var record = authData[index..];

        return new HandshakePacketBase(idSignature, ephPubkey, srcId, record);
    }
}