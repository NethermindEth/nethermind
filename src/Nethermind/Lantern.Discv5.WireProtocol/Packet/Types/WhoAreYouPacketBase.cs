using Lantern.Discv5.Rlp;

namespace Lantern.Discv5.WireProtocol.Packet.Types;

public class WhoAreYouPacketBase(byte[] idNonce, ulong enrSeq) : PacketBase(PreparePacketBase(idNonce, enrSeq))
{
    public byte[] IdNonce { get; } = idNonce;

    public ulong EnrSeq { get; } = enrSeq;

    private static byte[] PreparePacketBase(byte[] idNonce, ulong enrSeq)
    {
        var enrSeqArray = new byte[PacketConstants.EnrSeqSize];
        var enrSeqBytes = ByteArrayUtils.ToBigEndianBytesTrimmed(enrSeq);
        Array.Copy(enrSeqBytes, 0, enrSeqArray, PacketConstants.EnrSeqSize - enrSeqBytes.Length, enrSeqBytes.Length);

        return ByteArrayUtils.JoinByteArrays(idNonce, enrSeqArray);
    }

    public static WhoAreYouPacketBase DecodeAuthData(byte[] authData)
    {
        var index = 0;
        var idNonce = authData[..PacketConstants.IdNonceSize];
        index += PacketConstants.IdNonceSize;

        var enrSeq = (ulong)RlpExtensions.ByteArrayToInt64(authData[index..PacketConstants.WhoAreYou]);
        return new WhoAreYouPacketBase(idNonce, enrSeq);
    }
}