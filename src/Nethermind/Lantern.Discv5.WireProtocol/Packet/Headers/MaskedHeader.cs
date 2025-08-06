using Lantern.Discv5.WireProtocol.Session;

namespace Lantern.Discv5.WireProtocol.Packet.Headers;

public class MaskedHeader
{
    private const int MaskingKeyLength = 16;

    public MaskedHeader(byte[] destNodeId, byte[]? maskingIv = default)
    {
        if (destNodeId.Length < MaskingKeyLength)
        {
            throw new ArgumentException($"destNodeId must be at least {MaskingKeyLength} bytes long.", nameof(destNodeId));
        }

        MaskingKey = destNodeId[..MaskingKeyLength];
        MaskingIv = maskingIv ?? new byte[MaskingKeyLength];
    }

    private byte[] MaskingKey { get; }

    private byte[] MaskingIv { get; }

    public byte[] GetMaskedHeader(byte[] header, IAesCrypto aesCrypto) => aesCrypto.AesCtrEncrypt(MaskingKey, MaskingIv, header);
}