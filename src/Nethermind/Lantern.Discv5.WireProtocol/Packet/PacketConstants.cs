namespace Lantern.Discv5.WireProtocol.Packet;

public static class PacketConstants
{
    public const int MaxPacketSize = 1280;
    public const int MinPacketSize = 63;
    public const int Ordinary = 32;
    public const int WhoAreYou = 24;
    public const int HeaderNonce = 12;
    public const int IdNonceSize = 16;
    public const int PartialNonceSize = 8;
    public const int EnrSeqSize = 8;
    public const int SigSize = 1;
    public const int EphemeralKeySize = 1;
    public const int NodeIdSize = 32;
    public const int AuthDataSizeBytesLength = 2;
    public const int ProtocolIdSize = 6;
    public const int MimimalPacketSize = ProtocolIdSize + VersionSize + 1 + HeaderNonce + AuthDataSizeBytesLength;

    public const int VersionSize = 2;
    public const int MaskingIvSize = 16;
    public const int NonceSize = 12;
    public const int RandomDataSize = 40;
}
