// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Crypto;
using Nethermind.Merkleization;
using System.Text;
using Nethermind.Core.Collections;
using System.Runtime.CompilerServices;

namespace Nethermind.Shutter;

using G1 = Bls.P1;
using G1Affine = Bls.P1Affine;
using G2 = Bls.P2;
using G2Affine = Bls.P2Affine;
using GT = Bls.PT;

public static class ShutterCrypto
{
    private static readonly byte[] DST = Encoding.UTF8.GetBytes("SHUTTER_V01_BLS12381G1_XMD:SHA-256_SSWU_RO_");
    private const byte CryptoVersion = 0x3;
    private static readonly UInt256 BlsSubgroupOrder = new((byte[])[0x73, 0xed, 0xa7, 0x53, 0x29, 0x9d, 0x7d, 0x48, 0x33, 0x39, 0xd8, 0x08, 0x09, 0xa1, 0xd8, 0x05, 0x53, 0xbd, 0xa4, 0x02, 0xff, 0xfe, 0x5b, 0xfe, 0xff, 0xff, 0xff, 0xff, 0x00, 0x00, 0x00, 0x01], true);

    public readonly ref struct EncryptedMessage
    {
        public byte VersionId { get; init; }
        public G2 C1 { get; init; }
        public ReadOnlySpan<byte> C2 { get; init; }
        public ReadOnlySpan<byte> C3 { get; init; }
    }

    public class ShutterCryptoException(string message, Exception? innerException = null) : Exception(message, innerException);

    [SkipLocalsInit]
    public static void ComputeIdentity(G1 p, scoped ReadOnlySpan<byte> identityPrefix, in Address sender)
    {
        Span<byte> preimage = stackalloc byte[52];
        identityPrefix.CopyTo(preimage);
        sender.Bytes.CopyTo(preimage[32..]);
        ComputeIdentity(p, preimage);
    }

    public static void Decrypt(ref Span<byte> res, EncryptedMessage encryptedMessage, scoped G1 key)
    {
        RecoverSigma(out Span<byte> sigma, encryptedMessage, key.ToAffine());
        ComputeBlockKeys(res, sigma, encryptedMessage.C3.Length / 32);

        res.Xor(encryptedMessage.C3);

        UnpadAndJoin(ref res);

        ComputeR(sigma, res, out UInt256 r);

        G2 expectedC1 = ComputeC1(r);
        if (!expectedC1.IsEqual(encryptedMessage.C1))
        {
            throw new ShutterCryptoException("Could not decrypt message.");
        }
    }

    public static int GetDecryptedDataLength(EncryptedMessage encryptedMessage)
        => encryptedMessage.C3.Length;

    public static EncryptedMessage DecodeEncryptedMessage(ReadOnlySpan<byte> bytes)
    {
        if (bytes[0] != CryptoVersion)
        {
            throw new ShutterCryptoException($"Encrypted message had wrong Shutter crypto version. Expected version {CryptoVersion}, found {bytes[0]}.");
        }

        ReadOnlySpan<byte> c3 = bytes[(1 + 96 + 32)..];

        if (c3.Length % 32 != 0)
        {
            throw new ShutterCryptoException("Encrypted Shutter message had invalid c3");
        }

        try
        {
            G2 c1 = new(bytes[1..(1 + 96)]);
            return new()
            {
                VersionId = bytes[0],
                C1 = c1,
                C2 = bytes[(1 + 96)..(1 + 96 + 32)],
                C3 = c3
            };
        }
        catch (Bls.BlsException e)
        {
            throw new ShutterCryptoException("Encrypted Shutter message had invalid c1", e);
        }
    }

    public static void RecoverSigma(out Span<byte> sigma, EncryptedMessage encryptedMessage, G1Affine decryptionKey)
    {
        using ArrayPoolListRef<long> buf = new(GT.Sz, GT.Sz);
        GT p = new(buf.AsSpan());
        p.MillerLoop(encryptedMessage.C1.ToAffine(), decryptionKey);
        sigma = Hash2(p); // key
        sigma.Xor(encryptedMessage.C2);
    }

    [SkipLocalsInit]
    public static void ComputeBlockKeys(Span<byte> blockKeys, ReadOnlySpan<byte> sigma, int n)
    {
        if (blockKeys.Length != 32 * n)
        {
            throw new ArgumentException("Block keys buffer was the wrong size.");
        }

        Span<byte> preimageBuf = stackalloc byte[36];
        Span<byte> suffix = stackalloc byte[4];

        for (int i = 0; i < n; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(suffix, (uint)i);
            int suffixLength = 4;
            for (int j = 0; j < 3; j++)
            {
                if (suffix[j] != 0)
                {
                    break;
                }
                suffixLength--;
            }
            Span<byte> preimage = preimageBuf[..(32 + suffixLength)];
            sigma.CopyTo(preimage);
            suffix[(4 - suffixLength)..4].CopyTo(preimage[32..]);

            Hash4(preimage).Bytes.CopyTo(blockKeys[(i * 32)..]);
        }
    }

    // Hash1 in spec
    public static void ComputeIdentity(G1 p, scoped ReadOnlySpan<byte> bytes)
    {
        int len = bytes.Length + 1;
        using ArrayPoolListRef<byte> buf = new(len, len);
        Span<byte> preimage = buf.AsSpan();
        preimage[0] = 0x1;
        bytes.CopyTo(preimage[1..]);
        p.HashTo(preimage, DST);
    }

    public static Span<byte> Hash2(GT p)
    {
        using ArrayPoolListRef<byte> buf = new(577, 577);
        Span<byte> preimage = buf.AsSpan();
        preimage[0] = 0x2;
        p.FinalExp().ToBendian().CopyTo(preimage[1..]);
        return HashBytesToBlock(preimage);
    }

    public static void GTExp(ref GT x, UInt256 exp)
    {
        if (exp == 0)
        {
            return;
        }
        exp -= 1;

        using ArrayPoolListRef<long> buf = new(GT.Sz, GT.Sz);
        x.Fp12.CopyTo(buf.AsSpan());
        GT a = new(buf.AsSpan());
        for (; exp > 0; exp >>= 1)
        {
            if ((exp & 1) == 1)
            {
                x.Mul(a);
            }
            a.Sqr();
        }
    }

    [SkipLocalsInit]
    public static bool CheckDecryptionKey(G1Affine decryptionKey, G2Affine eonPublicKey, G1Affine identity)
    {
        int len = GT.Sz * 2;
        using ArrayPoolListRef<long> buf = new(len, len);
        GT p1 = new(buf.AsSpan()[..GT.Sz]);
        p1.MillerLoop(G2Affine.Generator(stackalloc long[G2Affine.Sz]), decryptionKey);
        GT p2 = new(buf.AsSpan()[GT.Sz..]);
        p2.MillerLoop(eonPublicKey, identity);
        return GT.FinalVerify(p1, p2);
    }

    public static bool CheckSlotDecryptionIdentitiesSignature(
        ulong instanceId,
        ulong eon,
        ulong slot,
        ulong txPointer,
        IEnumerable<ReadOnlyMemory<byte>> identityPreimages,
        ReadOnlySpan<byte> signatureBytes,
        Address keyperAddress)
    {
        Hash256 hash = GenerateHash(instanceId, eon, slot, txPointer, identityPreimages);

        // check recovery_id
        if (signatureBytes[64] > 3)
        {
            return false;
        }

        Signature signature = new(signatureBytes[..64], signatureBytes[64]);

        PublicKey? expectedPubkey = _ecdsa.RecoverPublicKey(signature, hash);

        return expectedPubkey is not null && keyperAddress == expectedPubkey.Address;
    }

    public static bool CheckValidatorRegistrySignatures(BlsSigner.AggregatedPublicKey pk, ReadOnlySpan<byte> sigBytes, ReadOnlySpan<byte> msgBytes)
        => BlsSigner.Verify(pk.PublicKey, sigBytes, ValueKeccak.Compute(msgBytes).Bytes);

    public static EncryptedMessage Encrypt(ReadOnlySpan<byte> msg, G1 identity, G2 eonKey, ReadOnlySpan<byte> sigma)
    {
        int len = PaddedLength(msg);
        using ArrayPoolListRef<byte> buf = new(len, len);
        ComputeR(sigma, msg, out UInt256 r);
        ReadOnlySpan<byte> msgBlocks = PadAndSplit(buf.AsSpan(), msg);
        Span<byte> c3 = new byte[msgBlocks.Length];
        ComputeC3(c3, msgBlocks, sigma);

        return new()
        {
            VersionId = CryptoVersion,
            C1 = ComputeC1(r),
            C2 = ComputeC2(sigma, r, identity, eonKey),
            C3 = c3
        };
    }

    public static Span<byte> EncodeEncryptedMessage(EncryptedMessage encryptedMessage)
    {
        Span<byte> encoded = new byte[1 + 96 + 32 + encryptedMessage.C3.Length];

        encoded[0] = encryptedMessage.VersionId;
        encryptedMessage.C1.Compress().CopyTo(encoded[1..]);
        encryptedMessage.C2.CopyTo(encoded[(1 + 96)..]);
        encryptedMessage.C3.CopyTo(encoded[(1 + 96 + 32)..]);

        return encoded;
    }

    private static readonly Ecdsa _ecdsa = new();

    private static G2 ComputeC1(UInt256 r)
        => G2.Generator().Mult(r.ToLittleEndian());

    private static Span<byte> HashBytesToBlock(scoped ReadOnlySpan<byte> bytes)
        => Keccak.Compute(bytes).Bytes;

    private static void UnpadAndJoin(ref Span<byte> blocks)
    {
        if (blocks.Length == 0)
        {
            return;
        }

        byte n = blocks[^1];

        if (n is 0 or > 32)
        {
            throw new ShutterCryptoException("Invalid padding length");
        }

        blocks = blocks[..(blocks.Length - n)];
    }

    private static Span<byte> ComputeC2(scoped ReadOnlySpan<byte> sigma, UInt256 r, G1 identity, G2 eonKey)
    {
        using ArrayPoolListRef<long> buf = new(GT.Sz, GT.Sz);
        GT p = new(buf.AsSpan());
        p.MillerLoop(eonKey, identity);
        GTExp(ref p, r);
        Span<byte> c2 = Hash2(p); //key
        c2.Xor(sigma);
        return c2;
    }

    private static void ComputeC3(Span<byte> c3, scoped ReadOnlySpan<byte> msgBlocks, ReadOnlySpan<byte> sigma)
    {
        // c3 = keys ^ msgs
        ComputeBlockKeys(c3, sigma, msgBlocks.Length / 32);
        c3.Xor(msgBlocks);
    }

    private static int PaddedLength(ReadOnlySpan<byte> bytes)
        => bytes.Length + (32 - (bytes.Length % 32));

    private static Span<byte> PadAndSplit(Span<byte> paddedBytes, scoped ReadOnlySpan<byte> bytes)
    {
        int n = 32 - (bytes.Length % 32);
        bytes.CopyTo(paddedBytes);
        paddedBytes[bytes.Length..].Fill((byte)n);
        return paddedBytes;
    }

    private static void ComputeR(scoped ReadOnlySpan<byte> sigma, scoped ReadOnlySpan<byte> msg, out UInt256 r)
    {
        int len = 32 + msg.Length;
        using ArrayPoolListRef<byte> buf = new(len, len);
        Span<byte> preimage = buf.AsSpan();
        sigma.CopyTo(preimage);
        msg.CopyTo(preimage[32..]);
        Hash3(preimage, out r);
    }

    internal static Hash256 GenerateHash(ulong instanceId, ulong eon, ulong slot, ulong txPointer, IEnumerable<ReadOnlyMemory<byte>> identityPreimages)
    {
        SlotDecryptionIdentites container = new()
        {
            InstanceID = instanceId,
            Eon = eon,
            Slot = slot,
            TxPointer = txPointer,
            IdentityPreimages = identityPreimages
        };

        Merkleizer merkleizer = new(Merkle.NextPowerOfTwoExponent(5));
        merkleizer.Feed(container.InstanceID);
        merkleizer.Feed(container.Eon);
        merkleizer.Feed(container.Slot);
        merkleizer.Feed(container.TxPointer);
        merkleizer.Feed(container.IdentityPreimages, 1024);
        merkleizer.CalculateRoot(out UInt256 root);

        return new(root.ToLittleEndian());
    }

    private static void Hash3(ReadOnlySpan<byte> bytes, out UInt256 res)
    {
        int len = bytes.Length + 1;
        using ArrayPoolListRef<byte> buf = new(len, len);
        Span<byte> preimage = buf.AsSpan();
        preimage[0] = 0x3;
        bytes.CopyTo(preimage[1..]);
        ReadOnlySpan<byte> hash = ValueKeccak.Compute(preimage).Bytes;
        UInt256.Mod(new UInt256(hash, true), BlsSubgroupOrder, out res);
    }

    private static ValueHash256 Hash4(ReadOnlySpan<byte> bytes)
    {
        int len = bytes.Length + 1;
        using ArrayPoolListRef<byte> buf = new(len, len);
        Span<byte> preimage = buf.AsSpan();
        preimage[0] = 0x4;
        bytes.CopyTo(preimage[1..]);
        return ValueKeccak.Compute(preimage);
    }

    private readonly struct SlotDecryptionIdentites
    {
        public ulong InstanceID { get; init; }
        public ulong Eon { get; init; }
        public ulong Slot { get; init; }
        public ulong TxPointer { get; init; }
        public IEnumerable<ReadOnlyMemory<byte>> IdentityPreimages { get; init; }
    }

}
