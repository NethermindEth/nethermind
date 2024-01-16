// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;
using Nethermind.Crypto.PairingCurves;

namespace Nethermind.Crypto;

public class Bls
{
    internal static readonly byte[] Cryptosuite = Encoding.ASCII.GetBytes("BLS_SIG_BLS12381G1_XMD:SHA-256_SSWU_RO_NUL_");
    internal static int InputLength = 64;

    public static Signature Sign(PrivateKey privateKey, ReadOnlySpan<byte> message)
    {
        return ToSignature(privateKey.Bytes.Reverse().ToArray() * HashToCurve(message, Cryptosuite));
    }

    public static bool Verify(PublicKey publicKey, Signature signature, ReadOnlySpan<byte> message)
    {
        return BlsCurve.PairingsEqual(FromSignature(signature), BlsCurve.G2.Generator, HashToCurve(message, Cryptosuite), FromPublicKey(publicKey));
    }

    public static PublicKey GetPublicKey(PrivateKey privateKey)
    {
        return ToPublicKey(privateKey.Bytes.Reverse().ToArray() * BlsCurve.G2.Generator);
    }

    public struct PrivateKey
    {
        public byte[] Bytes = new byte[32];

        public PrivateKey()
        {
        }
    }

    public struct PublicKey
    {
        public byte[] Bytes = new byte[96];

        public PublicKey()
        {
        }
    }

    public struct Signature
    {
        public byte[] Bytes = new byte[48];

        public Signature()
        {
        }
    }
    internal static BlsCurve.G1 FromSignature(Signature signature)
    {
        BlsCurve.G1 P = BlsCurve.G1.Zero;
        Span<byte> X = stackalloc byte[48];
        bool sign = DecompressPoint(signature.Bytes, X);
        return BlsCurve.G1.FromX(X, sign);
    }

    internal static Signature ToSignature(BlsCurve.G1 p)
    {
        Signature s = new();
        BlsCurve.G1 twistedPoint = BlsCurve.G1.FromX(p.X, true);
        bool sign = Enumerable.SequenceEqual(p.Y, twistedPoint.Y);
        CompressPoint(p.X, sign, s.Bytes);
        return s;
    }

    internal static BlsCurve.G2 FromPublicKey(PublicKey pk)
    {
        Span<byte> X0 = stackalloc byte[48];
        Span<byte> X1 = stackalloc byte[48];

        BlsCurve.G2 P = BlsCurve.G2.Zero;
        bool sign = DecompressPoint(pk.Bytes.AsSpan()[..48], X1);
        pk.Bytes.AsSpan()[48..].CopyTo(X0);

        return BlsCurve.G2.FromX(X0, X1, sign);
    }

    internal static PublicKey ToPublicKey(BlsCurve.G2 p)
    {
        PublicKey pk = new();
        bool sign = BlsCurve.Fp(p.Y.Item1) < BlsCurve.Fp(p.Y.Item2);
        CompressPoint(p.X.Item2, sign, pk.Bytes.AsSpan()[..48]);
        p.X.Item1.CopyTo(pk.Bytes.AsSpan()[48..]);
        return pk;
    }

    // parameters:
    // https://datatracker.ietf.org/doc/html/rfc9380#section-8.8.1
    // implementation:
    // https://datatracker.ietf.org/doc/html/rfc9380#section-3
    internal static BlsCurve.G1 HashToCurve(ReadOnlySpan<byte> msg, ReadOnlySpan<byte> dst)
    {
        List<byte[]> u = HashToField(msg, 2, dst);
        // n.b. MapToCurve implements clear_cofactor internally
        var q0 = BlsCurve.G1.MapToCurve(u[0]);
        var q1 = BlsCurve.G1.MapToCurve(u[1]);
        return q0 + q1;
    }

    // https://datatracker.ietf.org/doc/html/rfc9380#section-5.2
    internal static List<byte[]> HashToField(ReadOnlySpan<byte> msg, int count, ReadOnlySpan<byte> dst)
    {
        List<byte[]> u = [];

        int lenInBytes = count * InputLength;
        Span<byte> uniformBytes = stackalloc byte[lenInBytes];
        ExpandMessageXmd(msg, dst, lenInBytes, uniformBytes);
        for (int i = 0; i < count; i++)
        {
            int elmOffset = InputLength * i;
            ReadOnlySpan<byte> tv = uniformBytes[elmOffset..(elmOffset + InputLength)];
            BigInteger e = new BigInteger(tv, true, true) % BlsCurve.BaseFieldOrder;
            u.Add(e.ToBigEndianByteArray(48));
        }

        return u;
    }

    // https://datatracker.ietf.org/doc/html/rfc9380#section-5.3.1
    internal static void ExpandMessageXmd(ReadOnlySpan<byte> msg, ReadOnlySpan<byte> dst, int lenInBytes, Span<byte> res)
    {
        int ell = (lenInBytes / 32) + (lenInBytes % 32 == 0 ? 0 : 1);
        if (ell > 255)
        {
            throw new Exception();
        }

        // DST_prime = DST || I2OSP(len(DST), 1)
        Span<byte> dstPrime = stackalloc byte[dst.Length + 1];
        dst.CopyTo(dstPrime);
        dstPrime[dst.Length] = (byte)dst.Length;

        // msg_prime = Z_pad || msg || I2OSP(len_in_bytes, 2) || I2OSP(0, 1) || DST_prime
        Span<byte> msgPrime = stackalloc byte[64 + msg.Length + 2 + 1 + dstPrime.Length];
        msg.CopyTo(msgPrime[64..]);
        BinaryPrimitives.WriteUInt16BigEndian(msgPrime[(64 + msg.Length)..], (ushort)lenInBytes);
        dstPrime.CopyTo(msgPrime[(67 + msg.Length)..]);

        Span<byte> hashInput = stackalloc byte[32 + 1 + dstPrime.Length];

        Span<byte> b0 = SHA256.HashData(msgPrime);

        b0.CopyTo(hashInput);
        dstPrime.CopyTo(hashInput[33..]);

        for (byte i = 1; i <= ell; i++)
        {
            hashInput[32] = i;
            Span<byte> b = SHA256.HashData(hashInput);
            b.CopyTo(res[((i - 1) * 32)..]);

            // set first 32 bytes of next hash input
            b.Xor(b0);
            b.CopyTo(hashInput);
        }
    }

    internal static void CompressPoint(ReadOnlySpan<byte> p, bool sign, Span<byte> res)
    {
        p.CopyTo(res);

        if (sign)
        {
            res[0] |= 0x20;
        }

        // indicates that byte is compressed
        res[0] |= 0x80;
    }

    internal static bool DecompressPoint(ReadOnlySpan<byte> p, Span<byte> res)
    {
        bool sign = (p[0] & 0x20) == 0x20;
        p.CopyTo(res);
        res[0] &= 0x1F; // mask out top 3 bits
        return sign;
    }
}
