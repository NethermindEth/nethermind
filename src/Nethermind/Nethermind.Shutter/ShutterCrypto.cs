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
using Nethermind.Serialization.Ssz;
using Nethermind.Merkleization;
using System.Text;

namespace Nethermind.Shutter;

using G1 = Bls.P1;
using G2 = Bls.P2;
using GT = Bls.PT;

public static class ShutterCrypto
{
    private static readonly byte[] DST = Encoding.UTF8.GetBytes("SHUTTER_V01_BLS12381G1_XMD:SHA-256_SSWU_RO_");
    private static readonly byte CryptoVersion = 0x3;
    private static readonly UInt256 BlsSubgroupOrder = new((byte[])[0x73, 0xed, 0xa7, 0x53, 0x29, 0x9d, 0x7d, 0x48, 0x33, 0x39, 0xd8, 0x08, 0x09, 0xa1, 0xd8, 0x05, 0x53, 0xbd, 0xa4, 0x02, 0xff, 0xfe, 0x5b, 0xfe, 0xff, 0xff, 0xff, 0xff, 0x00, 0x00, 0x00, 0x01], true);

    public readonly ref struct EncryptedMessage
    {
        public readonly byte VersionId { get; init; }
        public readonly G2 C1 { get; init; }
        public readonly ReadOnlySpan<byte> C2 { get; init; }
        public readonly ReadOnlySpan<byte> C3 { get; init; }
    }

    public class ShutterCryptoException(string message, Exception? innerException = null) : Exception(message, innerException);

    public static G1 ComputeIdentity(Bytes32 identityPrefix, Address sender)
    {
        Span<byte> preimage = new byte[52];
        identityPrefix.Unwrap().CopyTo(preimage);
        sender.Bytes.CopyTo(preimage[32..]);
        return ComputeIdentity(preimage);
    }

    public static void Decrypt(ref Span<byte> res, EncryptedMessage encryptedMessage, G1 key)
    {
        RecoverSigma(out Span<byte> sigma, encryptedMessage, key);
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
        catch (Bls.Exception e)
        {
            throw new ShutterCryptoException("Encrypted Shutter message had invalid c1", e);
        }
    }

    public static void RecoverSigma(out Span<byte> res, EncryptedMessage encryptedMessage, G1 decryptionKey)
    {
        GT p = new(decryptionKey, encryptedMessage.C1);
        res = Hash2(p); // key
        res.Xor(encryptedMessage.C2);
    }

    public static void ComputeBlockKeys(Span<byte> res, ReadOnlySpan<byte> sigma, int n)
    {
        if (res.Length != 32 * n)
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
            Hash4(preimage).CopyTo(res[(i * 32)..]);
        }
    }

    // Hash1 in spec
    public static G1 ComputeIdentity(ReadOnlySpan<byte> bytes)
    {
        byte[] preimage = new byte[bytes.Length + 1];
        preimage[0] = 0x1;
        bytes.CopyTo(preimage.AsSpan()[1..]);

        return G1.Generator().HashTo(preimage, DST);
    }

    public static Span<byte> Hash2(GT p)
    {
        Span<byte> preimage = new byte[577];
        preimage[0] = 0x2;
        p.FinalExp().ToBendian().CopyTo(preimage[1..]);
        return HashBytesToBlock(preimage);
    }

    public static GT GTExp(GT x, UInt256 exp)
    {
        GT a = x;
        GT acc = GT.One();

        for (; exp > 0; exp >>= 1)
        {
            if ((exp & 1) == 1)
            {
                acc.Mul(a);
            }
            a.Sqr();
        }

        return acc;
    }

    public static bool CheckDecryptionKey(G1 decryptionKey, G2 eonPublicKey, G1 identity) =>
        GT.FinalVerify(new(decryptionKey, G2.Generator()), new(identity, eonPublicKey));

    public static bool CheckSlotDecryptionIdentitiesSignature(
        ulong instanceId,
        ulong eon,
        ulong slot,
        ulong txPointer,
        List<byte[]> identityPreimages,
        ReadOnlySpan<byte> signatureBytes,
        Address keyperAddress)
    {
        Hash256 hash = GenerateHash(instanceId, eon, slot, txPointer, identityPreimages);
        Ecdsa ecdsa = new();

        // check recovery_id
        if (signatureBytes[64] > 3)
        {
            return false;
        }

        Signature signature = new(signatureBytes[..64], signatureBytes[64]);

        PublicKey? expectedPubkey = ecdsa.RecoverPublicKey(signature, hash);

        return expectedPubkey is not null && keyperAddress == expectedPubkey.Address;
    }

    public static bool CheckValidatorRegistrySignature(ReadOnlySpan<byte> pkBytes, ReadOnlySpan<byte> sigBytes, ReadOnlySpan<byte> msgBytes)
    {
        BlsSigner.Signature sig = new(sigBytes);
        Hash256 h = Keccak.Compute(msgBytes);

        return BlsSigner.Verify(new(pkBytes), sig, h.BytesToArray());
    }

    public static EncryptedMessage Encrypt(ReadOnlySpan<byte> msg, G1 identity, G2 eonKey, ReadOnlySpan<byte> sigma)
    {
        ComputeR(sigma, msg, out UInt256 r);
        ReadOnlySpan<byte> msgBlocks = PadAndSplit(msg);
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

    private static G2 ComputeC1(UInt256 r)
        => G2.Generator().Mult(r.ToLittleEndian());

    private static Span<byte> HashBytesToBlock(ReadOnlySpan<byte> bytes)
        => Keccak.Compute(bytes).Bytes;

    private static void UnpadAndJoin(ref Span<byte> blocks)
    {
        if (blocks.Length == 0)
        {
            return;
        }

        byte n = blocks[^1];

        if (n == 0 || n > 32)
        {
            throw new ShutterCryptoException("Invalid padding length");
        }

        blocks = blocks[..(blocks.Length - n)];
    }

    private static Span<byte> ComputeC2(ReadOnlySpan<byte> sigma, UInt256 r, G1 identity, G2 eonKey)
    {
        GT p = new(identity, eonKey);
        GT preimage = GTExp(p, r);
        Span<byte> res = Hash2(preimage); //key
        res.Xor(sigma);
        return res;
    }

    private static void ComputeC3(Span<byte> res, ReadOnlySpan<byte> msgBlocks, ReadOnlySpan<byte> sigma)
    {
        // res = keys ^ msgs
        ComputeBlockKeys(res, sigma, msgBlocks.Length / 32);
        res.Xor(msgBlocks);
    }

    private static Span<byte> PadAndSplit(ReadOnlySpan<byte> bytes)
    {
        int n = 32 - (bytes.Length % 32);

        Span<byte> res = new byte[bytes.Length + n];
        res.Fill((byte)n);
        bytes.CopyTo(res);
        return res;
    }

    private static void ComputeR(ReadOnlySpan<byte> sigma, ReadOnlySpan<byte> msg, out UInt256 res)
    {
        Span<byte> preimage = stackalloc byte[32 + msg.Length];
        sigma.CopyTo(preimage);
        msg.CopyTo(preimage[32..]);
        Hash3(preimage, out res);
    }

    internal static Hash256 GenerateHash(ulong instanceId, ulong eon, ulong slot, ulong txPointer, List<byte[]> identityPreimages)
    {
        Ssz.SlotDecryptionIdentites container = new()
        {
            InstanceID = instanceId,
            Eon = eon,
            Slot = slot,
            TxPointer = txPointer,
            IdentityPreimages = identityPreimages
        };

        Merkle.Ize(out UInt256 root, container);
        return new(root.ToLittleEndian());
    }

    private static void Hash3(ReadOnlySpan<byte> bytes, out UInt256 res)
    {
        byte[] preimage = new byte[bytes.Length + 1];
        preimage[0] = 0x3;
        bytes.CopyTo(preimage.AsSpan()[1..]);
        Span<byte> hash = Keccak.Compute(preimage).Bytes;
        UInt256.Mod(new UInt256(hash, true), BlsSubgroupOrder, out res);
    }

    private static Span<byte> Hash4(ReadOnlySpan<byte> bytes)
    {
        byte[] preimage = new byte[bytes.Length + 1];
        preimage[0] = 0x4;
        bytes.CopyTo(preimage.AsSpan()[1..]);
        return Keccak.Compute(preimage).Bytes;
    }
}
