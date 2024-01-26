// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.IdentityModel.Tokens;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto.PairingCurves;
using Nethermind.Int256;

[assembly: InternalsVisibleTo("Nethermind.Merge.AuRa.Test")]

namespace Nethermind.Merge.AuRa.Shutter;

using G1 = BlsCurve.G1;
using G2 = BlsCurve.G2;
using GT = BlsCurve.GT;

internal class ShutterCrypto
{
    public struct EncryptedMessage
    {
        public G2 c1;
        public Bytes32 c2;
        public IEnumerable<Bytes32> c3;
    }

    public static G1 ComputeIdentity(Bytes32 identityPrefix, Address sender)
    {
        Span<byte> identity = stackalloc byte[52];
        identityPrefix.Unwrap().CopyTo(identity);
        sender.Bytes.CopyTo(identity[32..]);
        // todo: reverse?
        return Keccak.Compute(identity).Bytes * G1.Generator;
    }

    public static byte[] Decrypt(EncryptedMessage encryptedMessage, G1 key)
    {
        Bytes32 sigma = RecoverSigma(encryptedMessage, key);
        IEnumerable<Bytes32> keys = ComputeBlockKeys(sigma, encryptedMessage.c3.Count());
        IEnumerable<Bytes32> decryptedBlocks = Enumerable.Zip(keys, encryptedMessage.c3, XorBlocks);
        byte[] msg = UnpadAndJoin(decryptedBlocks);

        UInt256 r = ComputeR(sigma, msg);
        G2 expectedC1 = ComputeC1(r);
        if (expectedC1 != encryptedMessage.c1)
        {
            throw new Exception("Could not decrypt message.");
        }

        return msg;
    }

    public static Bytes32 RecoverSigma(EncryptedMessage encryptedMessage, G1 decryptionKey)
    {
        GT p = BlsCurve.Pairing(decryptionKey, encryptedMessage.c1);
        Bytes32 key = HashGTToBlock(p);
        Bytes32 sigma = XorBlocks(encryptedMessage.c2, key);
        return sigma;
    }

    public static UInt256 ComputeR(Bytes32 sigma, byte[] msg)
    {
        return HashBlocksToInt([sigma, HashBytesToBlock(msg)]);
    }

    public static G2 ComputeC1(UInt256 r)
    {
        return G2.FromScalar(r);
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
            return HashBytesToBlock(preimage);
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
            res[(blocks.Count() * 32) + i] = lastBlock.Unwrap()[i];
        }

        return res;
    }

    public static Bytes32 HashBytesToBlock(ReadOnlySpan<byte> bytes)
    {
        return new(Keccak.Compute(bytes).Bytes);
    }

    public static UInt256 HashBlocksToInt(IEnumerable<Bytes32> blocks)
    {
        Span<byte> combinedBlocks = stackalloc byte[blocks.Count() * 32];

        for (int i = 0; i < blocks.Count(); i++)
        {
            blocks.ElementAt(i).Unwrap().CopyTo(combinedBlocks[(32 * i)..]);
        }

        Span<byte> hash = Keccak.Compute(combinedBlocks).Bytes;
        BigInteger v = new BigInteger(hash, true, true) % BlsCurve.SubgroupOrder;

        return new(v.ToBigEndianByteArray(32));
    }

    public static Bytes32 HashGTToBlock(GT p)
    {
        return HashBytesToBlock(EncodeGT(p));
    }

    public static byte[] EncodeGT(GT p)
    {
        return [];
    }
}
