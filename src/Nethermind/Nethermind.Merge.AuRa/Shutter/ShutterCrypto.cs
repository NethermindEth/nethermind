// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.IdentityModel.Tokens;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Crypto;
using Nethermind.Serialization.Ssz;
using Nethermind.Merkleization;

[assembly: InternalsVisibleTo("Nethermind.Merge.AuRa.Test")]

namespace Nethermind.Merge.AuRa.Shutter;

using G1 = Bls.P1;
using G2 = Bls.P2;
using GT = Bls.PT;

internal class ShutterCrypto
{
    public static readonly byte CryptoVersion = 0x2;
    internal static readonly UInt256 BlsSubgroupOrder = new((byte[])[0x73, 0xed, 0xa7, 0x53, 0x29, 0x9d, 0x7d, 0x48, 0x33, 0x39, 0xd8, 0x08, 0x09, 0xa1, 0xd8, 0x05, 0x53, 0xbd, 0xa4, 0x02, 0xff, 0xfe, 0x5b, 0xfe, 0xff, 0xff, 0xff, 0xff, 0x00, 0x00, 0x00, 0x01], true);

    public struct EncryptedMessage
    {
        public byte VersionId;
        public G2 c1;
        public Bytes32 c2;
        public IEnumerable<Bytes32> c3;
    }

    public class ShutterCryptoException(string message) : Exception(message)
    {
    }

    public static G1 ComputeIdentity(Bytes32 identityPrefix, Address sender)
    {
        Span<byte> preimage = stackalloc byte[52];
        identityPrefix.Unwrap().CopyTo(preimage);
        sender.Bytes.CopyTo(preimage[32..]);

        return ComputeIdentity(preimage);
    }

    public static byte[] Decrypt(EncryptedMessage encryptedMessage, G1 key)
    {
        Bytes32 sigma = RecoverSigma(encryptedMessage, key);
        IEnumerable<Bytes32> keys = ComputeBlockKeys(sigma, encryptedMessage.c3.Count());
        IEnumerable<Bytes32> decryptedBlocks = Enumerable.Zip(keys, encryptedMessage.c3, XorBlocks);
        byte[] msg = UnpadAndJoin(decryptedBlocks);

        UInt256 r;
        ComputeR(sigma, msg, out r);

        G2 expectedC1 = ComputeC1(r);
        if (!expectedC1.is_equal(encryptedMessage.c1))
        {
            throw new ShutterCryptoException("Could not decrypt message.");
        }

        return msg;
    }

    public static EncryptedMessage DecodeEncryptedMessage(ReadOnlySpan<byte> bytes)
    {
        if (bytes[0] != CryptoVersion)
        {
            throw new ShutterCryptoException("Encrypted message had wrong Shutter crypto version.");
        }

        // todo: change once shutter swaps to blst
        // ReadOnlySpan<byte> c3Bytes = bytes[(1 + 96 + 32)..];
        // List<Bytes32> c3 = [];
        // for (int i = 0; i < c3Bytes.Length / 32; i++)
        // {
        //     c3.Add(new(c3Bytes[(i * 32)..((i + 1) * 32)]));
        // }

        // return new()
        // {
        //     VersionId = bytes[0],
        //     c1 = new G2(bytes[1..(1 + 96)].ToArray()),
        //     c2 = new Bytes32(bytes[(1 + 96)..(1 + 96 + 32)]),
        //     c3 = c3
        // };

        ReadOnlySpan<byte> c3Bytes = bytes[(1 + 192 + 32)..];
        List<Bytes32> c3 = [];
        for (int i = 0; i < c3Bytes.Length / 32; i++)
        {
            c3.Add(new(c3Bytes[(i * 32)..((i + 1) * 32)]));
        }

        G2 c1;

        try
        {
            c1 = new G2(bytes[1..(1 + 192)].ToArray());
        }
        catch (ApplicationException e)
        {
            throw new ShutterCryptoException($"Encrypted Shutter message had invalid c1: {e}");
        }

        return new()
        {
            VersionId = bytes[0],
            c1 = c1,
            c2 = new Bytes32(bytes[(1 + 192)..(1 + 192 + 32)]),
            c3 = c3
        };
    }

    public static Bytes32 RecoverSigma(EncryptedMessage encryptedMessage, G1 decryptionKey)
    {
        // todo: change this once shutter swaps to blst
        // GT p = new(decryptionKey, encryptedMessage.c1);
        // Bytes32 key = Hash2(p);
        Bytes32 key = new Bytes32();
        Bytes32 sigma = XorBlocks(encryptedMessage.c2, key);
        return sigma;
    }

    public static void ComputeR(Bytes32 sigma, ReadOnlySpan<byte> msg, out UInt256 res)
    {
        Span<byte> preimage = stackalloc byte[32 + msg.Length];
        sigma.Unwrap().CopyTo(preimage);
        msg.CopyTo(preimage[32..]);
        Hash3(preimage, out res);
    }

    public static G2 ComputeC1(UInt256 r)
    {
        return G2.generator().mult(r.ToLittleEndian());
    }

    public static IEnumerable<Bytes32> ComputeBlockKeys(Bytes32 sigma, int n)
    {
        return Enumerable.Range(0, n).Select(x =>
        {
            byte[] suffix = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(suffix, (uint)x);
            int suffixLength = 4;
            for (int i = 0; i < 3; i++)
            {
                if (suffix[i] != 0)
                {
                    break;
                }
                suffixLength--;
            }
            Span<byte> preimage = stackalloc byte[32 + suffixLength];
            sigma.Unwrap().CopyTo(preimage);
            suffix.AsSpan()[(4 - suffixLength)..4].CopyTo(preimage[32..]);
            return Hash4(preimage);
        });
    }

    public static Bytes32 XorBlocks(Bytes32 x, Bytes32 y)
    {
        return new(x.Unwrap().Xor(y.Unwrap()));
    }

    public static byte[] UnpadAndJoin(IEnumerable<Bytes32> blocks)
    {
        if (blocks.IsNullOrEmpty())
        {
            return [];
        }

        Bytes32 lastBlock = blocks.Last();
        byte n = lastBlock.Unwrap().Last();

        if (n == 0 || n > 32)
        {
            throw new ShutterCryptoException("Invalid padding length");
        }

        byte[] res = new byte[(blocks.Count() * 32) - n];

        for (int i = 0; i < blocks.Count() - 1; i++)
        {
            blocks.ElementAt(i).Unwrap().CopyTo(res.AsSpan()[(i * 32)..]);
        }

        for (int i = 0; i < (32 - n); i++)
        {
            res[((blocks.Count() - 1) * 32) + i] = lastBlock.Unwrap()[i];
        }

        return res;
    }

    public static Bytes32 HashBytesToBlock(ReadOnlySpan<byte> bytes)
    {
        return new(Keccak.Compute(bytes).Bytes);
    }

    // Hash1 in spec
    public static G1 ComputeIdentity(ReadOnlySpan<byte> bytes)
    {
        byte[] preimage = new byte[bytes.Length + 1];
        preimage[0] = 0x1;
        bytes.CopyTo(preimage.AsSpan()[1..]);

        // todo: change once shutter swaps to blst
        // return new G1().hash_to(preimage, "SHUTTER_V01_BLS12381G1_XMD:SHA-256_SSWU_RO_");

        Span<byte> hash = Keccak.Compute(preimage).Bytes;
        hash.Reverse();
        return G1.generator().mult(hash.ToArray());
    }

    public static Bytes32 Hash2(GT p)
    {
        Span<byte> preimage = stackalloc byte[577];
        preimage[0] = 0x2;
        p.final_exp().to_bendian().CopyTo(preimage[1..]);
        return HashBytesToBlock(preimage);
    }

    public static void Hash3(ReadOnlySpan<byte> bytes, out UInt256 res)
    {
        byte[] preimage = new byte[bytes.Length + 1];
        preimage[0] = 0x3;
        bytes.CopyTo(preimage.AsSpan()[1..]);
        Span<byte> hash = Keccak.Compute(preimage).Bytes;
        UInt256.Mod(new UInt256(hash, true), BlsSubgroupOrder, out res);
    }

    public static Bytes32 Hash4(ReadOnlySpan<byte> bytes)
    {
        byte[] preimage = new byte[bytes.Length + 1];
        preimage[0] = 0x4;
        bytes.CopyTo(preimage.AsSpan()[1..]);
        Span<byte> hash = Keccak.Compute(preimage).Bytes;
        return new(hash);
    }

    public static GT GTExp(GT x, UInt256 exp)
    {
        GT a = x;
        GT acc = GT.one();

        for (; exp > 0; exp >>= 1)
        {
            if ((exp & 1) == 1)
            {
                acc.mul(a);
            }
            a.sqr();
        }

        return acc;
    }

    public static bool CheckDecryptionKey(G1 decryptionKey, G2 eonPublicKey, G1 identity)
    {
        return GT.finalverify(new(decryptionKey, G2.generator()), new(identity, eonPublicKey));
    }

    public static bool CheckSlotDecryptionIdentitiesSignature(ulong instanceId, ulong eon, ulong slot, ulong txPointer, List<byte[]> identityPreimages, ReadOnlySpan<byte> signatureBytes, Address keyperAddress)
    {
        Hash256 hash = GenerateHash(instanceId, eon, slot, txPointer, identityPreimages);
        Ecdsa ecdsa = new();
        PublicKey? expectedPubkey;

        // check recovery_id
        if (signatureBytes[64] > 3)
        {
            return false;
        }
        Signature signature = new Signature(signatureBytes[..64], signatureBytes[64]);

        expectedPubkey = ecdsa.RecoverPublicKey(signature, hash);

        if (expectedPubkey is null)
        {
            return false;
        }

        return keyperAddress == expectedPubkey.Address;
    }

    public static bool CheckValidatorRegistrySignature(in byte[] pkBytes, in byte[] sigBytes, in byte[] msgBytes)
    {
        BlsSigner.PublicKey pk = new() { Bytes = pkBytes };
        BlsSigner.Signature sig = new() { Bytes = sigBytes };

        Hash256 h = Keccak.Compute(msgBytes);

        return BlsSigner.Verify(pk, sig, h.Bytes);
    }

    public static Hash256 GenerateHash(ulong instanceId, ulong eon, ulong slot, ulong txPointer, List<byte[]> identityPreimages)
    {
        var container = new Ssz.SlotDecryptionIdentites()
        {
            InstanceID = instanceId,
            Eon = eon,
            Slot = slot,
            TxPointer = txPointer,
            IdentityPreimages = identityPreimages
        };

        UInt256 root;
        Merkle.Ize(out root, container);
        return new(root.ToLittleEndian());
    }

    public static EncryptedMessage Encrypt(ReadOnlySpan<byte> msg, G1 identity, G2 eonKey, Bytes32 sigma)
    {
        UInt256 r;
        ComputeR(sigma, msg, out r);

        EncryptedMessage c = new()
        {
            VersionId = CryptoVersion,
            c1 = ComputeC1(r),
            c2 = ComputeC2(sigma, r, identity, eonKey),
            c3 = ComputeC3(PadAndSplit(msg), sigma)
        };
        return c;
    }

    internal static byte[] EncodeEncryptedMessage(EncryptedMessage encryptedMessage)
    {
        // todo: change once shutter swaps to blst
        // byte[] bytes = new byte[1 + 96 + 32 + (encryptedMessage.c3.Count() * 32)];

        // bytes[0] = encryptedMessage.VersionId;
        // encryptedMessage.c1.compress().CopyTo(bytes.AsSpan()[1..]);
        // encryptedMessage.c2.Unwrap().CopyTo(bytes.AsSpan()[(1 + 96)..]);

        // foreach ((Bytes32 block, int i) in encryptedMessage.c3.WithIndex())
        // {
        //     int offset = 1 + 96 + 32 + (32 * i);
        //     block.Unwrap().CopyTo(bytes.AsSpan()[offset..]);
        // }

        byte[] bytes = new byte[1 + 192 + 32 + (encryptedMessage.c3.Count() * 32)];

        bytes[0] = encryptedMessage.VersionId;
        encryptedMessage.c1.serialize().CopyTo(bytes.AsSpan()[1..]);
        encryptedMessage.c2.Unwrap().CopyTo(bytes.AsSpan()[(1 + 192)..]);

        foreach ((Bytes32 block, int i) in encryptedMessage.c3.WithIndex())
        {
            int offset = 1 + 192 + 32 + (32 * i);
            block.Unwrap().CopyTo(bytes.AsSpan()[offset..]);
        }

        return bytes;
    }

    private static Bytes32 ComputeC2(Bytes32 sigma, UInt256 r, G1 identity, G2 eonKey)
    {
        // todo: change once shutter swaps to blst
        // GT p = new(identity, eonKey);
        // GT preimage = ShutterCrypto.GTExp(p, r);
        // Bytes32 key = ShutterCrypto.Hash2(preimage);
        Bytes32 key = new();
        return XorBlocks(sigma, key);
    }

    private static IEnumerable<Bytes32> ComputeC3(IEnumerable<Bytes32> messageBlocks, Bytes32 sigma)
    {
        IEnumerable<Bytes32> keys = ComputeBlockKeys(sigma, messageBlocks.Count());
        return Enumerable.Zip(keys, messageBlocks, XorBlocks);
    }

    private static IEnumerable<Bytes32> PadAndSplit(ReadOnlySpan<byte> bytes)
    {
        List<Bytes32> res = [];
        int n = 32 - (bytes.Length % 32);
        Span<byte> padded = stackalloc byte[bytes.Length + n];
        padded.Fill((byte)n);
        bytes.CopyTo(padded);

        for (int i = 0; i < padded.Length / 32; i++)
        {
            int offset = i * 32;
            res.Add(new(padded[offset..(offset + 32)]));
        }
        return res;
    }
}
