// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Autofac.Features.AttributeFilters;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Discv5.Messages;
using Nethermind.Network.Enr;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Discv5.Packets;

public sealed class PacketCodec(
    [KeyFilter(IProtectedPrivateKey.NodeKey)] IProtectedPrivateKey nodeKey,
    INodeRecordProvider nodeRecordProvider,
    ICryptoRandom cryptoRandom,
    IEcdsa ecdsa) : IDisposable
{
    public const int NonceSize = 12;

    internal const int MaxPacketSize = 1280;

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
    private const int MaxStackPacketBufferSize = 512;
    private const int MaxRetainedDecodeMaskingAes = 32;

    private static ReadOnlySpan<byte> ProtocolId => "discv5"u8;
    private static ReadOnlySpan<byte> KeyAgreementInfoPrefix => "discovery v5 key agreement"u8;
    private static ReadOnlySpan<byte> IdentityProofText => "discovery v5 identity proof"u8;

    private readonly PrivateKey _privateKey = nodeKey.Unprotect();
    private readonly PublicKey _publicKey = nodeKey.PublicKey;
    private readonly ValueHash256 _localNodeId = nodeKey.PublicKey.Hash.ValueHash256;
    private readonly INodeRecordProvider _nodeRecordProvider = nodeRecordProvider;
    private readonly ICryptoRandom _cryptoRandom = cryptoRandom;
    private readonly IEcdsa _ecdsa = ecdsa;
    private readonly ObjectPool<Aes> _decodeMaskingAesPool = CreateDecodeMaskingAesPool(nodeKey.PublicKey.Hash.Bytes[..AesKeySize]);

    public void Dispose()
    {
        (_decodeMaskingAesPool as IDisposable)?.Dispose();
        _privateKey.Dispose();
    }

    internal byte[] EncodeOrdinary(PublicKey destination, ReadOnlySpan<byte> encryptionKey, Discv5Message message, ReadOnlySpan<byte> nonce)
        => EncodePacket(destination.Hash.Bytes, PacketFlag.Ordinary, nonce, _localNodeId.Bytes, encryptionKey, message);

    [SkipLocalsInit]
    internal byte[] EncodeWhoAreYou(ReadOnlySpan<byte> destinationNodeId, ReadOnlySpan<byte> requestNonce, ulong enrSequence, out Challenge challenge)
    {
        byte[] idNonce = _cryptoRandom.GenerateRandomBytes(IdNonceSize);
        Span<byte> authData = stackalloc byte[WhoAreYouAuthDataSize];
        idNonce.CopyTo(authData);
        BinaryPrimitives.WriteUInt64BigEndian(authData[IdNonceSize..], enrSequence);

        byte[] packet = EncodePacket(destinationNodeId, PacketFlag.WhoAreYou, requestNonce, authData, default, null, out byte[] challengeData);
        challenge = new Challenge(requestNonce.ToArray(), idNonce, enrSequence, challengeData);
        return packet;
    }

    [SkipLocalsInit]
    internal byte[] EncodeHandshake(PublicKey destination, Challenge challenge, Discv5Message message, out Session session)
    {
        using PrivateKey ephemeralKey = new PrivateKeyGenerator(_cryptoRandom).Generate();
        DeriveKeys(
            destination,
            ephemeralKey,
            _localNodeId.Bytes,
            destination.Hash.Bytes,
            challenge.ChallengeData,
            out byte[] initiatorKey,
            out byte[] recipientKey);

        byte[] ephemeralPublicKey = ephemeralKey.CompressedPublicKey.Bytes;
        byte[] record = challenge.EnrSequence < _nodeRecordProvider.Current.EnrSequence
            ? _nodeRecordProvider.Current.ToRlpBytes()
            : [];

        int authDataLength = HandshakeAuthDataHeadSize + IdSignatureSize + EphemeralPublicKeySize + record.Length;
        byte[]? rentedAuthData = null;
        Span<byte> authData = authDataLength <= MaxStackPacketBufferSize
            ? stackalloc byte[authDataLength]
            : (rentedAuthData = SafeArrayPool<byte>.Shared.Rent(authDataLength)).AsSpan(0, authDataLength);

        try
        {
            _localNodeId.Bytes.CopyTo(authData);
            authData[NodeIdSize] = IdSignatureSize;
            authData[NodeIdSize + 1] = EphemeralPublicKeySize;
            SignIdNonce(challenge.ChallengeData, ephemeralPublicKey, destination.Hash.Bytes, authData.Slice(HandshakeAuthDataHeadSize, IdSignatureSize));
            ephemeralPublicKey.CopyTo(authData[(HandshakeAuthDataHeadSize + IdSignatureSize)..]);
            record.CopyTo(authData[(HandshakeAuthDataHeadSize + IdSignatureSize + EphemeralPublicKeySize)..]);

            session = new Session(destination, recipientKey, initiatorKey);
            Span<byte> nonce = stackalloc byte[NonceSize];
            _cryptoRandom.GenerateRandomBytes(nonce);
            return EncodePacket(destination.Hash.Bytes, PacketFlag.Handshake, nonce, authData, initiatorKey, message);
        }
        finally
        {
            if (rentedAuthData is not null)
            {
                SafeArrayPool<byte>.Shared.Return(rentedAuthData);
            }
        }
    }

    internal bool TryDecode(byte[] packet, out Packet decoded)
        => TryDecode(packet.AsMemory(), out decoded);

    internal bool TryDecode(ReadOnlyMemory<byte> packet, out Packet decoded)
    {
        Aes localNodeMaskingAes = _decodeMaskingAesPool.Get();
        try
        {
            return TryDecode(packet, localNodeMaskingAes, out decoded);
        }
        finally
        {
            _decodeMaskingAesPool.Return(localNodeMaskingAes);
        }
    }

    internal static bool TryDecode(byte[] packet, ReadOnlySpan<byte> localNodeId, out Packet decoded)
        => TryDecode(packet.AsMemory(), localNodeId, out decoded);

    internal static bool TryDecode(ReadOnlyMemory<byte> packetMemory, ReadOnlySpan<byte> localNodeId, out Packet decoded)
    {
        using Aes localNodeMaskingAes = CreateMaskingAes(localNodeId[..AesKeySize]);
        return TryDecode(packetMemory, localNodeMaskingAes, out decoded);
    }

    [SkipLocalsInit]
    private static bool TryDecode(ReadOnlyMemory<byte> packetMemory, Aes localNodeMaskingAes, out Packet decoded)
    {
        decoded = default;
        ReadOnlySpan<byte> packet = packetMemory.Span;
        if (packet.Length is < MaskingIvSize + StaticHeaderSize or > MaxPacketSize)
        {
            return false;
        }

        ReadOnlySpan<byte> maskingIv = packet[..MaskingIvSize];
        Span<byte> staticHeader = stackalloc byte[StaticHeaderSize];
        AesCtrTransform(localNodeMaskingAes, maskingIv, packet.Slice(MaskingIvSize, StaticHeaderSize), staticHeader);
        ReadOnlySpan<byte> protocolId = ProtocolId;
        if (!staticHeader[..protocolId.Length].SequenceEqual(protocolId))
        {
            return false;
        }

        int authDataSize = BinaryPrimitives.ReadUInt16BigEndian(staticHeader[(StaticHeaderSize - sizeof(ushort))..]);
        int headerSize = StaticHeaderSize + authDataSize;
        if (packet.Length < MaskingIvSize + headerSize)
        {
            return false;
        }

        int messageAdLength = MaskingIvSize + headerSize;
        byte[] messageAdBuffer = SafeArrayPool<byte>.Shared.Rent(messageAdLength);
        Span<byte> messageAd = messageAdBuffer.AsSpan(0, messageAdLength);
        maskingIv.CopyTo(messageAd);
        Span<byte> header = messageAd[MaskingIvSize..];

        try
        {
            AesCtrTransform(localNodeMaskingAes, maskingIv, packet.Slice(MaskingIvSize, headerSize), header);
            if (!header[..protocolId.Length].SequenceEqual(protocolId))
            {
                SafeArrayPool<byte>.Shared.Return(messageAdBuffer);
                return false;
            }

            int version = BinaryPrimitives.ReadUInt16BigEndian(header[protocolId.Length..]);
            if (version != Version)
            {
                SafeArrayPool<byte>.Shared.Return(messageAdBuffer);
                return false;
            }

            int nonceOffset = MaskingIvSize + protocolId.Length + sizeof(ushort) + sizeof(byte);
            int authDataOffset = MaskingIvSize + StaticHeaderSize;
            PacketFlag flag = (PacketFlag)header[protocolId.Length + sizeof(ushort)];
            decoded = new Packet(
                flag,
                messageAdBuffer.AsMemory(nonceOffset, NonceSize),
                messageAdBuffer.AsMemory(authDataOffset, authDataSize),
                packetMemory.Slice(MaskingIvSize + headerSize),
                messageAdBuffer,
                messageAdLength);
            return true;
        }
        catch
        {
            SafeArrayPool<byte>.Shared.Return(messageAdBuffer);
            throw;
        }
    }

    internal bool TryDecryptMessage(scoped in Packet packet, ReadOnlySpan<byte> encryptionKey, out Discv5Message message)
        => TryDecryptMessageForTest(in packet, encryptionKey, out message);

    internal static bool TryDecryptMessageForTest(scoped in Packet packet, ReadOnlySpan<byte> encryptionKey, out Discv5Message message)
    {
        message = null!;
        ReadOnlySpan<byte> encryptedMessage = packet.Message.Span;
        if (packet.Message.Length < AesGcmTagSize)
        {
            return false;
        }

        ArrayPoolSpan<byte> plaintext = new(packet.Message.Length - AesGcmTagSize);
        bool ownerTransferred = false;
        try
        {
            using AesGcm aesGcm = new(encryptionKey, AesGcmTagSize);
            aesGcm.Decrypt(
                packet.Nonce.Span,
                encryptedMessage[..plaintext.Length],
                encryptedMessage.Slice(plaintext.Length, AesGcmTagSize),
                plaintext,
                packet.MessageAd.Span);

            ownerTransferred = true;
            message = MessageCodec.DecodeOwned(plaintext.AsReadOnlyMemory(), plaintext);
            return true;
        }
        catch (CryptographicException)
        {
            if (!ownerTransferred)
            {
                plaintext.Dispose();
            }

            return false;
        }
        catch
        {
            if (!ownerTransferred)
            {
                plaintext.Dispose();
            }

            throw;
        }
    }

    internal Challenge DecodeWhoAreYou(scoped in Packet packet)
    {
        if (packet.AuthData.Length != WhoAreYouAuthDataSize)
        {
            throw new RlpException("Invalid WHOAREYOU authdata length.");
        }

        byte[] idNonce = packet.AuthData.Span[..IdNonceSize].ToArray();
        ulong enrSequence = BinaryPrimitives.ReadUInt64BigEndian(packet.AuthData.Span[IdNonceSize..]);
        return new Challenge(packet.Nonce.ToArray(), idNonce, enrSequence, packet.ChallengeData.ToArray());
    }

    internal bool TryDecryptHandshake(
        scoped in Packet packet,
        Challenge challenge,
        NodeRecord? knownRecord,
        out Session session,
        out Discv5Message message,
        out NodeRecord? nodeRecord)
    {
        session = null!;
        message = null!;
        nodeRecord = null;

        if (!TryReadHandshakeAuthData(packet.AuthData, out ValueHash256 sourceNodeId, out ReadOnlyMemory<byte> idSignature, out CompressedPublicKey? ephemeralPublicKey, out ReadOnlyMemory<byte> recordBytes))
        {
            return false;
        }

        if (recordBytes.Length > 0)
        {
            try
            {
                nodeRecord = NodeRecord.FromBytes(recordBytes.Span, _ecdsa);
            }
            catch (Exception)
            {
                return false;
            }
        }

        NodeRecord? record = nodeRecord ?? knownRecord;
        CompressedPublicKey? remoteCompressedPublicKey = record?.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1);
        if (remoteCompressedPublicKey is null)
        {
            return false;
        }

        PublicKey remotePublicKey = remoteCompressedPublicKey.Decompress();
        if (remotePublicKey.Hash != sourceNodeId)
        {
            return false;
        }

        if (!VerifyIdSignature(remoteCompressedPublicKey, idSignature.Span, challenge.ChallengeData, ephemeralPublicKey.Bytes, _localNodeId.Bytes))
        {
            return false;
        }

        DeriveKeys(ephemeralPublicKey, sourceNodeId.Bytes, _localNodeId.Bytes, challenge.ChallengeData, out byte[] initiatorKey, out byte[] recipientKey);

        if (!TryDecryptMessage(in packet, initiatorKey, out message))
        {
            return false;
        }

        session = new Session(remotePublicKey, initiatorKey, recipientKey);
        return true;
    }

    internal static bool TryGetSourceNodeId(scoped in Packet packet, out ValueHash256 sourceNodeId)
    {
        sourceNodeId = default;
        switch (packet.Flag)
        {
            case PacketFlag.Ordinary when packet.AuthData.Length == NodeIdSize:
                sourceNodeId = new ValueHash256(packet.AuthData.Span);
                return true;
            case PacketFlag.Handshake when packet.AuthData.Length >= HandshakeAuthDataHeadSize:
                sourceNodeId = new ValueHash256(packet.AuthData.Span[..NodeIdSize]);
                return true;
            default:
                return false;
        }
    }

    private byte[] EncodePacket(
        ReadOnlySpan<byte> destinationNodeId,
        PacketFlag flag,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> authData,
        ReadOnlySpan<byte> encryptionKey,
        Discv5Message? message)
        => EncodePacket(destinationNodeId, flag, nonce, authData, encryptionKey, message, out _);

    private byte[] EncodePacket(
        ReadOnlySpan<byte> destinationNodeId,
        PacketFlag flag,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> authData,
        ReadOnlySpan<byte> encryptionKey,
        Discv5Message? message,
        out byte[] messageAd)
    {
        if (message is null)
        {
            return EncodePacketCore(destinationNodeId, flag, nonce, authData, default, default, out messageAd);
        }

        using NettyRlpStream encodedMessage = MessageCodec.Encode(message);
        return EncodePacketCore(destinationNodeId, flag, nonce, authData, encryptionKey, encodedMessage.AsSpan(), out messageAd);
    }

    [SkipLocalsInit]
    private byte[] EncodePacketCore(
        ReadOnlySpan<byte> destinationNodeId,
        PacketFlag flag,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> authData,
        ReadOnlySpan<byte> encryptionKey,
        ReadOnlySpan<byte> plaintext,
        out byte[] messageAd)
    {
        int headerLength = StaticHeaderSize + authData.Length;
        int messageAdLength = MaskingIvSize + headerLength;
        int encryptedMessageLength = plaintext.IsEmpty ? 0 : plaintext.Length + AesGcmTagSize;

        byte[] packet = new byte[MaskingIvSize + headerLength + encryptedMessageLength];
        Span<byte> maskingIv = packet.AsSpan(0, MaskingIvSize);
        _cryptoRandom.GenerateRandomBytes(maskingIv);

        byte[]? rentedMessageAd = null;
        Span<byte> messageAdBuffer = messageAdLength <= MaxStackPacketBufferSize
            ? stackalloc byte[messageAdLength]
            : (rentedMessageAd = SafeArrayPool<byte>.Shared.Rent(messageAdLength)).AsSpan(0, messageAdLength);

        try
        {
            maskingIv.CopyTo(messageAdBuffer);
            WriteHeader(messageAdBuffer[MaskingIvSize..], flag, nonce, authData);

            if (!plaintext.IsEmpty)
            {
                EncryptMessage(
                    encryptionKey,
                    nonce,
                    plaintext,
                    messageAdBuffer,
                    packet.AsSpan(MaskingIvSize + headerLength, encryptedMessageLength));
            }

            AesCtrTransform(destinationNodeId[..AesKeySize], maskingIv, messageAdBuffer.Slice(MaskingIvSize, headerLength), packet.AsSpan(MaskingIvSize, headerLength));
            messageAd = plaintext.IsEmpty
                ? messageAdBuffer.ToArray()
                : [];
            return packet;
        }
        finally
        {
            if (rentedMessageAd is not null)
            {
                SafeArrayPool<byte>.Shared.Return(rentedMessageAd);
            }
        }
    }

    private static void WriteHeader(Span<byte> header, PacketFlag flag, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> authData)
    {
        if (nonce.Length != NonceSize)
        {
            throw new ArgumentException($"Nonce must be {NonceSize} bytes.", nameof(nonce));
        }

        ReadOnlySpan<byte> protocolId = ProtocolId;
        protocolId.CopyTo(header);
        BinaryPrimitives.WriteUInt16BigEndian(header[protocolId.Length..], Version);
        header[protocolId.Length + sizeof(ushort)] = (byte)flag;
        nonce.CopyTo(header.Slice(protocolId.Length + sizeof(ushort) + sizeof(byte), NonceSize));
        BinaryPrimitives.WriteUInt16BigEndian(header[(StaticHeaderSize - sizeof(ushort))..], checked((ushort)authData.Length));
        authData.CopyTo(header[StaticHeaderSize..]);
    }

    private static void EncryptMessage(
        ReadOnlySpan<byte> encryptionKey,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> messageAd,
        Span<byte> encrypted)
    {
        using AesGcm aesGcm = new(encryptionKey, AesGcmTagSize);
        aesGcm.Encrypt(
            nonce,
            plaintext,
            encrypted[..plaintext.Length],
            encrypted.Slice(plaintext.Length, AesGcmTagSize),
            messageAd);
    }

    private static void AesCtrTransform(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> input, Span<byte> output)
    {
        using Aes aes = CreateMaskingAes(key);
        AesCtrTransform(aes, iv, input, output);
    }

    [SkipLocalsInit]
    private static void AesCtrTransform(Aes aes, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (output.Length < input.Length)
        {
            throw new ArgumentException("Output span must be at least as long as input.", nameof(output));
        }

        Span<byte> counter = stackalloc byte[MaskingIvSize];
        iv.CopyTo(counter);
        Span<byte> keyStream = stackalloc byte[MaskingIvSize];

        int offset = 0;
        while (offset < input.Length)
        {
            aes.EncryptEcb(counter, keyStream, PaddingMode.None);

            int blockLength = Math.Min(MaskingIvSize, input.Length - offset);
            for (int i = 0; i < blockLength; i++)
            {
                output[offset + i] = (byte)(input[offset + i] ^ keyStream[i]);
            }

            IncrementCounter(counter);
            offset += blockLength;
        }
    }

    private static Aes CreateMaskingAes(ReadOnlySpan<byte> key)
    {
        Aes aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.SetKey(key);
        return aes;
    }

    private static ObjectPool<Aes> CreateDecodeMaskingAesPool(ReadOnlySpan<byte> key)
    {
        DefaultObjectPoolProvider provider = new()
        {
            MaximumRetained = Math.Min(Environment.ProcessorCount, MaxRetainedDecodeMaskingAes)
        };

        return provider.Create(new DecodeMaskingAesPolicy(key));
    }

    private sealed class DecodeMaskingAesPolicy : IPooledObjectPolicy<Aes>
    {
        private readonly byte[] _key;

        public DecodeMaskingAesPolicy(ReadOnlySpan<byte> key) => _key = key.ToArray();

        public Aes Create() => CreateMaskingAes(_key);

        public bool Return(Aes obj) => true;
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
        ReadOnlySpan<byte> challengeData,
        out byte[] initiatorKey,
        out byte[] recipientKey)
    {
        byte[] sharedPoint = ephemeralPrivateKey.GetCompressedSharedPoint(remotePublicKey);
        DeriveKeys(sharedPoint, initiatorNodeId, recipientNodeId, challengeData, out initiatorKey, out recipientKey);
    }

    private void DeriveKeys(
        CompressedPublicKey ephemeralPublicKey,
        ReadOnlySpan<byte> initiatorNodeId,
        ReadOnlySpan<byte> recipientNodeId,
        ReadOnlySpan<byte> challengeData,
        out byte[] initiatorKey,
        out byte[] recipientKey)
    {
        byte[] sharedPoint = _privateKey.GetCompressedSharedPoint(ephemeralPublicKey);
        DeriveKeys(sharedPoint, initiatorNodeId, recipientNodeId, challengeData, out initiatorKey, out recipientKey);
    }

    [SkipLocalsInit]
    private static void DeriveKeys(
        byte[] secret,
        ReadOnlySpan<byte> initiatorNodeId,
        ReadOnlySpan<byte> recipientNodeId,
        ReadOnlySpan<byte> challengeData,
        out byte[] initiatorKey,
        out byte[] recipientKey)
    {
        Span<byte> prk = stackalloc byte[SHA256.HashSizeInBytes];
        HMACSHA256.HashData(challengeData, secret, prk);

        ReadOnlySpan<byte> infoPrefix = KeyAgreementInfoPrefix;
        Span<byte> info = stackalloc byte[infoPrefix.Length + NodeIdSize + NodeIdSize];
        infoPrefix.CopyTo(info);
        initiatorNodeId.CopyTo(info[infoPrefix.Length..]);
        recipientNodeId.CopyTo(info[(infoPrefix.Length + NodeIdSize)..]);

        Span<byte> hkdfInput = stackalloc byte[info.Length + 1];
        info.CopyTo(hkdfInput);
        hkdfInput[^1] = 1;

        Span<byte> keyData = stackalloc byte[AesKeySize * 2];
        HMACSHA256.HashData(prk, hkdfInput, keyData);
        initiatorKey = keyData[..AesKeySize].ToArray();
        recipientKey = keyData[AesKeySize..].ToArray();
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

    [SkipLocalsInit]
    private void SignIdNonce(
        ReadOnlySpan<byte> challengeData,
        ReadOnlySpan<byte> ephemeralPublicKey,
        ReadOnlySpan<byte> recipientNodeId,
        Span<byte> destination)
    {
        Span<byte> signingHash = stackalloc byte[SHA256.HashSizeInBytes];
        CalculateIdSignatureHash(challengeData, ephemeralPublicKey, recipientNodeId, signingHash);
        Signature signature = _ecdsa.Sign(_privateKey, new ValueHash256(signingHash));
        signature.Bytes[..IdSignatureSize].CopyTo(destination);
    }

    [SkipLocalsInit]
    private bool VerifyIdSignature(
        CompressedPublicKey signer,
        ReadOnlySpan<byte> signatureBytes,
        ReadOnlySpan<byte> challengeData,
        ReadOnlySpan<byte> ephemeralPublicKey,
        ReadOnlySpan<byte> recipientNodeId)
    {
        Span<byte> signingHash = stackalloc byte[SHA256.HashSizeInBytes];
        CalculateIdSignatureHash(challengeData, ephemeralPublicKey, recipientNodeId, signingHash);
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
    {
        byte[] signingHash = new byte[SHA256.HashSizeInBytes];
        CalculateIdSignatureHash(challengeData, ephemeralPublicKey, recipientNodeId, signingHash);
        return signingHash;
    }

    [SkipLocalsInit]
    private static void CalculateIdSignatureHash(
        ReadOnlySpan<byte> challengeData,
        ReadOnlySpan<byte> ephemeralPublicKey,
        ReadOnlySpan<byte> recipientNodeId,
        Span<byte> destination)
    {
        ReadOnlySpan<byte> identityProofText = IdentityProofText;
        int signingInputLength = identityProofText.Length + challengeData.Length + ephemeralPublicKey.Length + recipientNodeId.Length;
        byte[]? rentedSigningInput = null;
        Span<byte> signingInput = signingInputLength <= MaxStackPacketBufferSize
            ? stackalloc byte[signingInputLength]
            : (rentedSigningInput = SafeArrayPool<byte>.Shared.Rent(signingInputLength)).AsSpan(0, signingInputLength);

        try
        {
            identityProofText.CopyTo(signingInput);
            challengeData.CopyTo(signingInput[identityProofText.Length..]);
            ephemeralPublicKey.CopyTo(signingInput[(identityProofText.Length + challengeData.Length)..]);
            recipientNodeId.CopyTo(signingInput[(identityProofText.Length + challengeData.Length + ephemeralPublicKey.Length)..]);
            SHA256.HashData(signingInput, destination);
        }
        finally
        {
            if (rentedSigningInput is not null)
            {
                SafeArrayPool<byte>.Shared.Return(rentedSigningInput);
            }
        }
    }

    private static bool TryReadHandshakeAuthData(
        ReadOnlyMemory<byte> authDataMemory,
        out ValueHash256 sourceNodeId,
        out ReadOnlyMemory<byte> idSignature,
        [NotNullWhen(true)] out CompressedPublicKey? ephemeralPublicKey,
        out ReadOnlyMemory<byte> record)
    {
        sourceNodeId = default;
        idSignature = ReadOnlyMemory<byte>.Empty;
        ephemeralPublicKey = null;
        record = ReadOnlyMemory<byte>.Empty;

        ReadOnlySpan<byte> authData = authDataMemory.Span;
        if (authData.Length < HandshakeAuthDataHeadSize)
        {
            return false;
        }

        sourceNodeId = new ValueHash256(authData[..NodeIdSize]);
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

        idSignature = authDataMemory.Slice(signatureOffset, signatureSize);
        ephemeralPublicKey = new CompressedPublicKey(authData.Slice(ephemeralKeyOffset, ephemeralKeySize));
        record = authDataMemory[recordOffset..];
        return true;
    }
}
