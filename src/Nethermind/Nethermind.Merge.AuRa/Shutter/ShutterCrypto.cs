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

[assembly: InternalsVisibleTo("Nethermind.Merge.AuRa.Test")]

namespace Nethermind.Merge.AuRa.Shutter;

using G1 = Bls.P1;
using G2 = Bls.P2;
using GT = Bls.PT;

internal class ShutterCrypto
{
    internal static readonly UInt256 BlsSubgroupOrder = new((byte[])[0x73, 0xed, 0xa7, 0x53, 0x29, 0x9d, 0x7d, 0x48, 0x33, 0x39, 0xd8, 0x08, 0x09, 0xa1, 0xd8, 0x05, 0x53, 0xbd, 0xa4, 0x02, 0xff, 0xfe, 0x5b, 0xfe, 0xff, 0xff, 0xff, 0xff, 0x00, 0x00, 0x00, 0x01], true);
    internal static readonly byte CryptoVersion = 0x2;

    public struct EncryptedMessage
    {
        public byte VersionId;
        public G2 c1;
        public Bytes32 c2;
        public IEnumerable<Bytes32> c3;
    }

    public static G1 ComputeIdentity(Bytes32 identityPrefix, Address sender)
    {
        Span<byte> identity = stackalloc byte[52];
        identityPrefix.Unwrap().CopyTo(identity);
        sender.Bytes.CopyTo(identity[32..]);

        // todo: check if need to reverse?
        Span<byte> hash = Keccak.Compute(identity).Bytes;
        hash.Reverse();
        return G1.generator().mult(hash.ToArray());
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
            throw new Exception("Could not decrypt message.");
        }

        return msg;
    }

    public static EncryptedMessage DecodeEncryptedMessage(ReadOnlySpan<byte> bytes)
    {
        if (bytes[0] != CryptoVersion)
        {
            throw new Exception("Encrypted message had wrong crypto id.");
        }

        ReadOnlySpan<byte> c3Bytes = bytes[(96 + 32)..];
        List<Bytes32> c3 = [];
        for (int i = 0; i < c3Bytes.Length / 32; i++)
        {
            c3.Add(new(c3Bytes[(i * 32)..((i + 1) * 32)]));
        }

        return new()
        {
            VersionId = bytes[0],
            c1 = new G2(bytes[..96].ToArray()),
            c2 = new Bytes32(bytes[96..(96 + 32)]),
            c3 = c3
        };
    }

    public static Bytes32 RecoverSigma(EncryptedMessage encryptedMessage, G1 decryptionKey)
    {
        GT p = new(decryptionKey, encryptedMessage.c1);
        Bytes32 key = Hash2(p);
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

    // helper functions
    public static IEnumerable<Bytes32> ComputeBlockKeys(Bytes32 sigma, int n)
    {
        // suffix_length = max((n.bit_length() + 7) // 8, 1)
        // suffixes = [n.to_bytes(suffix_length, "big")]
        // preimages = [sigma + suffix for suffix in suffixes]
        // keys = [hash_bytes_to_block(preimage) for preimage in preimages]
        // return keys
        // int bitLength = 0;
        // for (int x = n; x > 0; x >>= 1)
        // {
        //     bitLength++;
        // }

        // int suffixLength = int.Max((bitLength + 7) / 8, 1);

        // todo: is actual implementation correct?
        return Enumerable.Range(0, n).Select(suffix =>
        {
            Span<byte> preimage = stackalloc byte[36];
            sigma.Unwrap().CopyTo(preimage);
            BinaryPrimitives.WriteInt32BigEndian(preimage[32..], suffix);
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
            throw new Exception("Invalid padding length");
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

    public static G1 Hash1(ReadOnlySpan<byte> bytes)
    {
        byte[] preimage = new byte[bytes.Length + 1];
        preimage[0] = 0x1;
        bytes.CopyTo(preimage.AsSpan()[1..]);

        // todo: change once shutter updates
        // return new G1().hash_to(preimage);

        // todo: check if need to reverse?
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
        UInt256.Mod(new UInt256(hash), BlsSubgroupOrder, out res);
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
        // todo: check against version in spec
        return GT.finalverify(new(decryptionKey, G2.generator()), new(identity, eonPublicKey));
    }

    public static bool CheckSlotDecryptionIdentitiesSignature(ulong instanceId, ulong eon, ulong slot, IEnumerable<G1> identities, ReadOnlySpan<byte> signature, Address keyperAddress)
    {
        Hash256 h = GenerateHash(instanceId, eon, slot, identities);
        Ecdsa ecdsa = new();
        PublicKey? expectedPubkey;

        // todo: investigate bad recovery id 230
        Signature s = new Signature(signature);
        expectedPubkey = ecdsa.RecoverPublicKey(s, h);

        if (expectedPubkey is null)
        {
            return false;
        }

        return keyperAddress == expectedPubkey.Address;
    }

    // todo: should keyperIndex and txPointer be included?
    public static Hash256 GenerateHash(ulong instanceId, ulong eon, ulong slot, IEnumerable<G1> identities)
    {
        Span<byte> container = stackalloc byte[40 + identities.Count() * 64];

        Ssz.Encode(container[..8], instanceId);
        Ssz.Encode(container[8..16], eon);
        Ssz.Encode(container[16..24], slot);

        foreach ((G1 identity, int index) in identities.WithIndex())
        {
            int offset = 24 + index * 64;
            Ssz.Encode(container[offset..(offset + 64)], identity.compress());
        }

        return Keccak.Compute(container);
    }
}
