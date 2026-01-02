using Lantern.Discv5.Rlp;
using System.Diagnostics.CodeAnalysis;

namespace Lantern.Discv5.WireProtocol.Packet.Headers;

public class StaticHeader
{
    public StaticHeader(byte[] version, byte[] authData, byte flag, byte[] nonce, int encryptedMessageLength = 0)
    {
        Version = version;
        AuthData = authData;
        AuthDataSize = AuthData.Length;
        Flag = flag;
        Nonce = nonce;
        EncryptedMessageLength = encryptedMessageLength;
    }

    public byte[] Version { get; }

    public byte[] AuthData { get; }

    public int AuthDataSize { get; }

    public byte Flag { get; }

    public byte[] Nonce { get; }

    public int EncryptedMessageLength { get; }

    public byte[] GetHeader()
    {
        var protocolId = ProtocolConstants.ProtocolIdBytes;
        var authDataSize = ByteArrayUtils.ToBigEndianBytesTrimmed(AuthDataSize);
        var authDataBytes = new byte[PacketConstants.AuthDataSizeBytesLength];
        Array.Copy(authDataSize, 0, authDataBytes, PacketConstants.AuthDataSizeBytesLength - authDataSize.Length, authDataSize.Length);

        return ByteArrayUtils.Concatenate(protocolId, Version, new[] { Flag }, Nonce, authDataBytes, AuthData);
    }

    public static bool TryDecodeFromBytes(byte[] decryptedData, [NotNullWhen(true)] out StaticHeader? staticHeader)
    {
        if (decryptedData.Length < PacketConstants.MimimalPacketSize)
        {
            staticHeader = null;
            return false;
        }

        var index = 0;

        if (!ProtocolConstants.ProtocolIdBytes.AsSpan().SequenceEqual(decryptedData.AsSpan(0, PacketConstants.ProtocolIdSize)))
        {
            staticHeader = null;
            return false;
        }

        index += PacketConstants.ProtocolIdSize;

        var version = decryptedData[index..(index + PacketConstants.VersionSize)];
        index += PacketConstants.VersionSize;

        var flag = decryptedData[index];
        index += 1;

        var nonce = decryptedData[index..(index + PacketConstants.HeaderNonce)];
        index += PacketConstants.HeaderNonce;

        var authDataSize = RlpExtensions.ByteArrayToInt32(decryptedData[index..(index + PacketConstants.AuthDataSizeBytesLength)]);
        index += PacketConstants.AuthDataSizeBytesLength;

        if (decryptedData.Length < index + authDataSize)
        {
            staticHeader = null;
            return false;
        }

        // Based on the flag, it should retrieve the authdata correctly
        var authData = decryptedData[index..(index + authDataSize)];
        var encryptedMessage = decryptedData[(index + authDataSize)..];

        staticHeader = new StaticHeader(version, authData, flag, nonce, encryptedMessage.Length);
        return true;
    }
}
