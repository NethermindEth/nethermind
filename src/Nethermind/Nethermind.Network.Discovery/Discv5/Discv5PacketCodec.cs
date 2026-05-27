// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Autofac.Features.AttributeFilters;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Network.Enr;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Discv5;

internal enum Discv5PacketFlag : byte
{
    Ordinary = 0,
    WhoAreYou = 1,
    Handshake = 2
}

internal sealed record Discv5Packet(
    Discv5PacketFlag Flag,
    byte[] MaskingIv,
    byte[] Nonce,
    byte[] Header,
    byte[] AuthData,
    byte[] Message,
    byte[] MessageAd)
{
    public byte[] ChallengeData => MessageAd;
}

internal sealed record Discv5Challenge(byte[] RequestNonce, byte[] IdNonce, ulong EnrSequence, byte[] ChallengeData);

internal sealed record Discv5Session(PublicKey RemotePublicKey, byte[] ReadKey, byte[] WriteKey)
{
    private long _nonceCounter;

    public byte[] RemoteNodeId => RemotePublicKey.Hash.BytesToArray();

    public byte[] GetNextNonce(ICryptoRandom random)
    {
        byte[] nonce = new byte[Discv5PacketCodec.NonceSize];
        BinaryPrimitives.WriteUInt32BigEndian(nonce, unchecked((uint)Interlocked.Increment(ref _nonceCounter)));
        random.GenerateRandomBytes(nonce.AsSpan(sizeof(uint)));
        return nonce;
    }
}

public sealed class Discv5PacketCodec(
    [KeyFilter(IProtectedPrivateKey.NodeKey)] IProtectedPrivateKey nodeKey,
    INodeRecordProvider nodeRecordProvider,
    ICryptoRandom cryptoRandom,
    IEcdsa ecdsa) : IDisposable
{
    public const int NonceSize = 12;

    private const int MaskingIvSize = 16;
    private const int StaticHeaderSize = 23;
    private const int NodeIdSize = 32;
    private const int WhoAreYouAuthDataSize = 24;
    private const int IdNonceSize = 16;
    private const int AesKeySize = 16;
    private const int AesGcmTagSize = 16;
    private const int Version = 1;
    private const int IdSignatureSize = 64;
    private const int EphemeralPublicKeySize = 33;
    private const int HandshakeAuthDataHeadSize = NodeIdSize + 2;

    private static readonly byte[] ProtocolId = "discv5"u8.ToArray();
    private static readonly byte[] KeyAgreementInfoPrefix = Encoding.ASCII.GetBytes("discovery v5 key agreement");
    private static readonly byte[] IdentityProofText = Encoding.ASCII.GetBytes("discovery v5 identity proof");

    private readonly PrivateKey _privateKey = nodeKey.Unprotect();
    private readonly PublicKey _publicKey = nodeKey.PublicKey;
    private readonly INodeRecordProvider _nodeRecordProvider = nodeRecordProvider;
    private readonly ICryptoRandom _cryptoRandom = cryptoRandom;
    private readonly IEcdsa _ecdsa = ecdsa;

    internal byte[] LocalNodeId => _publicKey.Hash.BytesToArray();

    public void Dispose() => _privateKey.Dispose();

    internal byte[] EncodeOrdinary(PublicKey destination, byte[] encryptionKey, Discv5Message message, byte[]? nonce = null)
    {
        byte[] actualNonce = nonce ?? CreateNonce();
        byte[] authData = LocalNodeId.ToArray();
        return EncodePacket(destination.Hash.BytesToArray(), Discv5PacketFlag.Ordinary, actualNonce, authData, encryptionKey, message);
    }

    internal byte[] EncodeWhoAreYou(byte[] destinationNodeId, byte[] requestNonce, ulong enrSequence, out Discv5Challenge challenge)
    {
        byte[] idNonce = _cryptoRandom.GenerateRandomBytes(IdNonceSize);
        byte[] authData = new byte[WhoAreYouAuthDataSize];
        idNonce.CopyTo(authData, 0);
        BinaryPrimitives.WriteUInt64BigEndian(authData.AsSpan(IdNonceSize), enrSequence);

        byte[] packet = EncodePacket(destinationNodeId, Discv5PacketFlag.WhoAreYou, requestNonce, authData, null, null, out byte[] challengeData);
        challenge = new Discv5Challenge(requestNonce.ToArray(), idNonce, enrSequence, challengeData);
        return packet;
    }

    internal byte[] EncodeHandshake(PublicKey destination, Discv5Challenge challenge, Discv5Message message, out Discv5Session session)
    {
        using PrivateKey ephemeralKey = new PrivateKeyGenerator(_cryptoRandom).Generate();
        DeriveKeys(
            destination,
            ephemeralKey,
            LocalNodeId,
            destination.Hash.Bytes,
            challenge.ChallengeData,
            out byte[] initiatorKey,
            out byte[] recipientKey);

        byte[] ephemeralPublicKey = ephemeralKey.CompressedPublicKey.Bytes;
        byte[] idSignature = SignIdNonce(challenge.ChallengeData, ephemeralPublicKey, destination.Hash.BytesToArray());
        byte[] record = challenge.EnrSequence < _nodeRecordProvider.Current.EnrSequence
            ? _nodeRecordProvider.Current.ToRlpBytes()
            : [];

        byte[] authData = new byte[HandshakeAuthDataHeadSize + idSignature.Length + ephemeralPublicKey.Length + record.Length];
        LocalNodeId.CopyTo(authData, 0);
        authData[NodeIdSize] = IdSignatureSize;
        authData[NodeIdSize + 1] = EphemeralPublicKeySize;
        idSignature.CopyTo(authData.AsSpan(HandshakeAuthDataHeadSize));
        ephemeralPublicKey.CopyTo(authData.AsSpan(HandshakeAuthDataHeadSize + idSignature.Length));
        record.CopyTo(authData.AsSpan(HandshakeAuthDataHeadSize + idSignature.Length + ephemeralPublicKey.Length));

        session = new Discv5Session(destination, recipientKey, initiatorKey);
        return EncodePacket(destination.Hash.BytesToArray(), Discv5PacketFlag.Handshake, CreateNonce(), authData, initiatorKey, message);
    }

    internal bool TryDecode(ReadOnlySpan<byte> packet, out Discv5Packet decoded)
        => TryDecode(packet, LocalNodeId, out decoded);

    internal static bool TryDecode(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> localNodeId, out Discv5Packet decoded)
    {
        decoded = null!;
        if (packet.Length < MaskingIvSize + StaticHeaderSize)
        {
            return false;
        }

        byte[] maskingIv = packet[..MaskingIvSize].ToArray();
        byte[] staticHeader = AesCtrTransform(localNodeId[..AesKeySize], maskingIv, packet.Slice(MaskingIvSize, StaticHeaderSize));
        if (!staticHeader.AsSpan(0, ProtocolId.Length).SequenceEqual(ProtocolId))
        {
            return false;
        }

        int authDataSize = BinaryPrimitives.ReadUInt16BigEndian(staticHeader.AsSpan(StaticHeaderSize - sizeof(ushort)));
        int headerSize = StaticHeaderSize + authDataSize;
        if (packet.Length < MaskingIvSize + headerSize)
        {
            return false;
        }

        byte[] header = AesCtrTransform(localNodeId[..AesKeySize], maskingIv, packet.Slice(MaskingIvSize, headerSize));
        if (!header.AsSpan(0, ProtocolId.Length).SequenceEqual(ProtocolId))
        {
            return false;
        }

        int version = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(ProtocolId.Length));
        if (version != Version)
        {
            return false;
        }

        Discv5PacketFlag flag = (Discv5PacketFlag)header[ProtocolId.Length + sizeof(ushort)];
        byte[] nonce = header.AsSpan(ProtocolId.Length + sizeof(ushort) + sizeof(byte), NonceSize).ToArray();
        byte[] authData = header.AsSpan(StaticHeaderSize, authDataSize).ToArray();
        byte[] message = packet[(MaskingIvSize + headerSize)..].ToArray();
        byte[] messageAd = new byte[MaskingIvSize + header.Length];
        maskingIv.CopyTo(messageAd, 0);
        header.CopyTo(messageAd.AsSpan(MaskingIvSize));

        decoded = new Discv5Packet(flag, maskingIv, nonce, header, authData, message, messageAd);
        return true;
    }

    internal bool TryDecryptMessage(Discv5Packet packet, byte[] encryptionKey, out Discv5Message message)
        => TryDecryptMessageForTest(packet, encryptionKey, out message);

    internal static bool TryDecryptMessageForTest(Discv5Packet packet, byte[] encryptionKey, out Discv5Message message)
    {
        message = null!;
        if (packet.Message.Length < AesGcmTagSize)
        {
            return false;
        }

        byte[] plaintext = new byte[packet.Message.Length - AesGcmTagSize];
        try
        {
            using AesGcm aesGcm = new(encryptionKey, AesGcmTagSize);
            aesGcm.Decrypt(
                packet.Nonce,
                packet.Message.AsSpan(0, plaintext.Length),
                packet.Message.AsSpan(plaintext.Length, AesGcmTagSize),
                plaintext,
                packet.MessageAd);
        }
        catch (CryptographicException)
        {
            return false;
        }

        message = Discv5MessageCodec.Decode(plaintext);
        return true;
    }

    internal Discv5Challenge DecodeWhoAreYou(Discv5Packet packet)
    {
        if (packet.AuthData.Length != WhoAreYouAuthDataSize)
        {
            throw new RlpException("Invalid WHOAREYOU authdata length.");
        }

        byte[] idNonce = packet.AuthData.AsSpan(0, IdNonceSize).ToArray();
        ulong enrSequence = BinaryPrimitives.ReadUInt64BigEndian(packet.AuthData.AsSpan(IdNonceSize));
        return new Discv5Challenge(packet.Nonce, idNonce, enrSequence, packet.ChallengeData);
    }

    internal bool TryDecryptHandshake(
        Discv5Packet packet,
        Discv5Challenge challenge,
        NodeRecord? knownRecord,
        out Discv5Session session,
        out Discv5Message message,
        out NodeRecord? nodeRecord)
    {
        session = null!;
        message = null!;
        nodeRecord = null;

        if (!TryReadHandshakeAuthData(packet.AuthData, out byte[] sourceNodeId, out byte[] idSignature, out CompressedPublicKey ephemeralPublicKey, out byte[] recordBytes))
        {
            return false;
        }

        if (recordBytes.Length > 0)
        {
            try
            {
                nodeRecord = NodeRecord.FromBytes(recordBytes);
            }
            catch (Exception)
            {
                return false;
            }
        }

        NodeRecord? record = nodeRecord ?? knownRecord;
        CompressedPublicKey? remoteCompressedPublicKey = record?.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1);
        if (remoteCompressedPublicKey is null || !remoteCompressedPublicKey.Decompress().Hash.Bytes.SequenceEqual(sourceNodeId))
        {
            return false;
        }

        if (!VerifyIdSignature(remoteCompressedPublicKey, idSignature, challenge.ChallengeData, ephemeralPublicKey.Bytes, LocalNodeId))
        {
            return false;
        }

        PublicKey remotePublicKey = remoteCompressedPublicKey.Decompress();
        DeriveKeys(ephemeralPublicKey, sourceNodeId, LocalNodeId, challenge.ChallengeData, out byte[] initiatorKey, out byte[] recipientKey);

        if (!TryDecryptMessage(packet, initiatorKey, out message))
        {
            return false;
        }

        session = new Discv5Session(remotePublicKey, initiatorKey, recipientKey);
        return true;
    }

    internal static bool TryGetSourceNodeId(Discv5Packet packet, out byte[] sourceNodeId)
    {
        sourceNodeId = [];
        switch (packet.Flag)
        {
            case Discv5PacketFlag.Ordinary when packet.AuthData.Length == NodeIdSize:
                sourceNodeId = packet.AuthData.ToArray();
                return true;
            case Discv5PacketFlag.Handshake when packet.AuthData.Length >= HandshakeAuthDataHeadSize:
                sourceNodeId = packet.AuthData.AsSpan(0, NodeIdSize).ToArray();
                return true;
            default:
                return false;
        }
    }

    private byte[] EncodePacket(
        byte[] destinationNodeId,
        Discv5PacketFlag flag,
        byte[] nonce,
        byte[] authData,
        byte[]? encryptionKey,
        Discv5Message? message)
        => EncodePacket(destinationNodeId, flag, nonce, authData, encryptionKey, message, out _);

    private byte[] EncodePacket(
        byte[] destinationNodeId,
        Discv5PacketFlag flag,
        byte[] nonce,
        byte[] authData,
        byte[]? encryptionKey,
        Discv5Message? message,
        out byte[] messageAd)
    {
        byte[] maskingIv = _cryptoRandom.GenerateRandomBytes(MaskingIvSize);
        byte[] header = CreateHeader(flag, nonce, authData);
        messageAd = new byte[MaskingIvSize + header.Length];
        maskingIv.CopyTo(messageAd, 0);
        header.CopyTo(messageAd.AsSpan(MaskingIvSize));

        byte[] encryptedMessage = [];
        if (message is not null)
        {
            ArgumentNullException.ThrowIfNull(encryptionKey);

            encryptedMessage = EncryptMessage(encryptionKey, nonce, Discv5MessageCodec.Encode(message), messageAd);
        }

        byte[] maskedHeader = AesCtrTransform(destinationNodeId.AsSpan(0, AesKeySize), maskingIv, header);
        byte[] packet = new byte[MaskingIvSize + maskedHeader.Length + encryptedMessage.Length];
        maskingIv.CopyTo(packet, 0);
        maskedHeader.CopyTo(packet.AsSpan(MaskingIvSize));
        encryptedMessage.CopyTo(packet.AsSpan(MaskingIvSize + maskedHeader.Length));
        return packet;
    }

    private static byte[] CreateHeader(Discv5PacketFlag flag, byte[] nonce, byte[] authData)
    {
        if (nonce.Length != NonceSize)
        {
            throw new ArgumentException($"Nonce must be {NonceSize} bytes.", nameof(nonce));
        }

        byte[] header = new byte[StaticHeaderSize + authData.Length];
        ProtocolId.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(ProtocolId.Length), Version);
        header[ProtocolId.Length + sizeof(ushort)] = (byte)flag;
        nonce.CopyTo(header.AsSpan(ProtocolId.Length + sizeof(ushort) + sizeof(byte)));
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(StaticHeaderSize - sizeof(ushort)), checked((ushort)authData.Length));
        authData.CopyTo(header.AsSpan(StaticHeaderSize));
        return header;
    }

    private byte[] CreateNonce() => _cryptoRandom.GenerateRandomBytes(NonceSize);

    private static byte[] EncryptMessage(byte[] encryptionKey, byte[] nonce, byte[] plaintext, byte[] messageAd)
    {
        byte[] encrypted = new byte[plaintext.Length + AesGcmTagSize];
        using AesGcm aesGcm = new(encryptionKey, AesGcmTagSize);
        aesGcm.Encrypt(
            nonce,
            plaintext,
            encrypted.AsSpan(0, plaintext.Length),
            encrypted.AsSpan(plaintext.Length, AesGcmTagSize),
            messageAd);
        return encrypted;
    }

    private static byte[] AesCtrTransform(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> input)
    {
        byte[] output = new byte[input.Length];
        using Aes aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key.ToArray();

        using ICryptoTransform encryptor = aes.CreateEncryptor();
        Span<byte> counter = stackalloc byte[MaskingIvSize];
        iv.CopyTo(counter);
        Span<byte> keyStream = stackalloc byte[MaskingIvSize];
        byte[] counterBlock = new byte[MaskingIvSize];
        byte[] keyStreamBlock = new byte[MaskingIvSize];

        int offset = 0;
        while (offset < input.Length)
        {
            counter.CopyTo(counterBlock);
            encryptor.TransformBlock(counterBlock, 0, counterBlock.Length, keyStreamBlock, 0);
            keyStreamBlock.CopyTo(keyStream);

            int blockLength = Math.Min(MaskingIvSize, input.Length - offset);
            for (int i = 0; i < blockLength; i++)
            {
                output[offset + i] = (byte)(input[offset + i] ^ keyStream[i]);
            }

            IncrementCounter(counter);
            offset += blockLength;
        }

        return output;
    }

    private static void IncrementCounter(Span<byte> counter)
    {
        for (int i = counter.Length - 1; i >= 0; i--)
        {
            counter[i]++;
            if (counter[i] != 0)
            {
                return;
            }
        }
    }

    private static void DeriveKeys(
        PublicKey remotePublicKey,
        PrivateKey ephemeralPrivateKey,
        ReadOnlySpan<byte> initiatorNodeId,
        ReadOnlySpan<byte> recipientNodeId,
        byte[] challengeData,
        out byte[] initiatorKey,
        out byte[] recipientKey)
    {
        byte[] secret = SecP256k1Agreement.AgreeCompressed(remotePublicKey, ephemeralPrivateKey);
        DeriveKeys(secret, initiatorNodeId, recipientNodeId, challengeData, out initiatorKey, out recipientKey);
    }

    private void DeriveKeys(
        CompressedPublicKey ephemeralPublicKey,
        ReadOnlySpan<byte> initiatorNodeId,
        ReadOnlySpan<byte> recipientNodeId,
        byte[] challengeData,
        out byte[] initiatorKey,
        out byte[] recipientKey)
    {
        byte[] secret = SecP256k1Agreement.AgreeCompressed(ephemeralPublicKey, _privateKey);
        DeriveKeys(secret, initiatorNodeId, recipientNodeId, challengeData, out initiatorKey, out recipientKey);
    }

    private static void DeriveKeys(
        byte[] secret,
        ReadOnlySpan<byte> initiatorNodeId,
        ReadOnlySpan<byte> recipientNodeId,
        byte[] challengeData,
        out byte[] initiatorKey,
        out byte[] recipientKey)
    {
        byte[] prk = HMACSHA256.HashData(challengeData, secret);
        byte[] info = new byte[KeyAgreementInfoPrefix.Length + NodeIdSize + NodeIdSize];
        KeyAgreementInfoPrefix.CopyTo(info, 0);
        initiatorNodeId.CopyTo(info.AsSpan(KeyAgreementInfoPrefix.Length));
        recipientNodeId.CopyTo(info.AsSpan(KeyAgreementInfoPrefix.Length + NodeIdSize));

        byte[] keyData = HkdfExpand(prk, info, AesKeySize * 2);
        initiatorKey = keyData[..AesKeySize];
        recipientKey = keyData[AesKeySize..];
    }

    internal static (byte[] InitiatorKey, byte[] RecipientKey) DeriveKeysForTest(
        byte[] secret,
        byte[] initiatorNodeId,
        byte[] recipientNodeId,
        byte[] challengeData)
    {
        DeriveKeys(secret, initiatorNodeId, recipientNodeId, challengeData, out byte[] initiatorKey, out byte[] recipientKey);
        return (initiatorKey, recipientKey);
    }

    private static byte[] HkdfExpand(byte[] prk, byte[] info, int length)
    {
        byte[] result = new byte[length];
        byte[] previous = [];
        int offset = 0;
        byte counter = 1;
        using HMACSHA256 hmac = new(prk);
        while (offset < length)
        {
            byte[] input = new byte[previous.Length + info.Length + 1];
            previous.CopyTo(input, 0);
            info.CopyTo(input.AsSpan(previous.Length));
            input[^1] = counter++;
            previous = hmac.ComputeHash(input);
            int copyLength = Math.Min(previous.Length, length - offset);
            previous.AsSpan(0, copyLength).CopyTo(result.AsSpan(offset));
            offset += copyLength;
        }

        return result;
    }

    private byte[] SignIdNonce(byte[] challengeData, byte[] ephemeralPublicKey, byte[] recipientNodeId)
    {
        byte[] signingHash = CalculateIdSignatureHash(challengeData, ephemeralPublicKey, recipientNodeId);
        Signature signature = _ecdsa.Sign(_privateKey, new ValueHash256(signingHash));
        return signature.Bytes.ToArray();
    }

    private bool VerifyIdSignature(CompressedPublicKey signer, byte[] signatureBytes, byte[] challengeData, byte[] ephemeralPublicKey, byte[] recipientNodeId)
    {
        byte[] signingHash = CalculateIdSignatureHash(challengeData, ephemeralPublicKey, recipientNodeId);
        for (int recoveryId = 0; recoveryId <= 1; recoveryId++)
        {
            Signature signature = new(signatureBytes, recoveryId);
            CompressedPublicKey? recovered = _ecdsa.RecoverCompressedPublicKey(signature, new ValueHash256(signingHash));
            if (signer.Equals(recovered))
            {
                return true;
            }
        }

        return false;
    }

    internal static byte[] CalculateIdSignatureHashForTest(byte[] challengeData, byte[] ephemeralPublicKey, byte[] recipientNodeId)
        => CalculateIdSignatureHash(challengeData, ephemeralPublicKey, recipientNodeId);

    private static byte[] CalculateIdSignatureHash(byte[] challengeData, byte[] ephemeralPublicKey, byte[] recipientNodeId)
    {
        byte[] signingInput = new byte[IdentityProofText.Length + challengeData.Length + ephemeralPublicKey.Length + recipientNodeId.Length];
        IdentityProofText.CopyTo(signingInput, 0);
        challengeData.CopyTo(signingInput.AsSpan(IdentityProofText.Length));
        ephemeralPublicKey.CopyTo(signingInput.AsSpan(IdentityProofText.Length + challengeData.Length));
        recipientNodeId.CopyTo(signingInput.AsSpan(IdentityProofText.Length + challengeData.Length + ephemeralPublicKey.Length));
        return SHA256.HashData(signingInput);
    }

    private static bool TryReadHandshakeAuthData(
        byte[] authData,
        out byte[] sourceNodeId,
        out byte[] idSignature,
        out CompressedPublicKey ephemeralPublicKey,
        out byte[] record)
    {
        sourceNodeId = [];
        idSignature = [];
        ephemeralPublicKey = null!;
        record = [];

        if (authData.Length < HandshakeAuthDataHeadSize)
        {
            return false;
        }

        sourceNodeId = authData.AsSpan(0, NodeIdSize).ToArray();
        int signatureSize = authData[NodeIdSize];
        int ephemeralKeySize = authData[NodeIdSize + 1];
        if (signatureSize != IdSignatureSize || ephemeralKeySize != EphemeralPublicKeySize)
        {
            return false;
        }

        int signatureOffset = HandshakeAuthDataHeadSize;
        int ephemeralKeyOffset = signatureOffset + signatureSize;
        int recordOffset = ephemeralKeyOffset + ephemeralKeySize;
        if (authData.Length < recordOffset)
        {
            return false;
        }

        idSignature = authData.AsSpan(signatureOffset, signatureSize).ToArray();
        ephemeralPublicKey = new CompressedPublicKey(authData.AsSpan(ephemeralKeyOffset, ephemeralKeySize));
        record = authData.AsSpan(recordOffset).ToArray();
        return true;
    }
}
