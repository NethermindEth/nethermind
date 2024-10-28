// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Text;
using Nethermind.Core.Collections;

namespace Nethermind.Crypto;

// https://www.ietf.org/archive/id/draft-irtf-cfrg-bls-signature-05.html

using G1 = Bls.P1;
using G1Affine = Bls.P1Affine;
using G2 = Bls.P2;
using G2Affine = Bls.P2Affine;
using GT = Bls.PT;

public static class BlsSigner
{
    public const int PkCompressedSz = 384 / 8;
    private static readonly byte[] Cryptosuite = Encoding.UTF8.GetBytes("BLS_SIG_BLS12381G2_XMD:SHA-256_SSWU_RO_POP_");
    private const int InputLength = 64;

    [SkipLocalsInit]
    public static Signature Sign(Bls.SecretKey sk, ReadOnlySpan<byte> message)
    {
        G2 p = new(stackalloc long[G2.Sz]);
        p.HashTo(message, Cryptosuite);
        p.SignWith(sk);
        return new(p.Compress());
    }

    [SkipLocalsInit]
    public static Signature SignAggregate(ReadOnlySpan<byte> skBytes, ReadOnlySpan<byte> message)
    {
        if (skBytes.Length % 32 != 0)
        {
            throw new Bls.BlsException(Bls.ERROR.WRONGSIZE);
        }

        G2 p = new(stackalloc long[G2.Sz]);
        G2 agg = new(stackalloc long[G2.Sz]);
        agg.Zero();

        for (int i = 0; i < skBytes.Length; i += 32)
        {
            Bls.SecretKey sk = new(skBytes.Slice(i, 32));
            p.HashTo(message, Cryptosuite);
            p.SignWith(sk);
            agg.Aggregate(p.ToAffine());
        }

        return new(agg.Compress());
    }

    [SkipLocalsInit]
    public static bool Verify(G1Affine publicKey, Signature signature, ReadOnlySpan<byte> message)
    {
        int len = 2 * GT.Sz;
        using ArrayPoolList<long> buf = new(len, len);
        try
        {
            G2Affine sig = new(stackalloc long[G2Affine.Sz]);
            sig.Decode(signature.Bytes);
            GT p1 = new(buf.AsSpan()[..GT.Sz]);
            p1.MillerLoop(sig, G1Affine.Generator(stackalloc long[G1Affine.Sz]));

            G2 m = new(stackalloc long[G2.Sz]);
            m.HashTo(message, Cryptosuite);
            GT p2 = new(buf.AsSpan()[GT.Sz..]);
            p2.MillerLoop(m.ToAffine(), publicKey);

            return GT.FinalVerify(p1, p2);
        }
        catch (Bls.BlsException)
        {
            // point not on curve
            return false;
        }
    }

    [SkipLocalsInit]
    public static bool VerifyAggregate(ReadOnlySpan<byte> publicKeyBytes, Signature signature, ReadOnlySpan<byte> message)
    {
        if (publicKeyBytes.Length % PkCompressedSz != 0)
        {
            throw new Bls.BlsException(Bls.ERROR.WRONGSIZE);
        }

        G1Affine pk = new(stackalloc long[G1Affine.Sz]);
        G1 agg = new(stackalloc long[G1.Sz]);
        agg.Zero();

        for (int i = 0; i < publicKeyBytes.Length; i += PkCompressedSz)
        {
            pk.Decode(publicKeyBytes.Slice(i, PkCompressedSz));
            agg.Aggregate(pk);
        }

        return Verify(agg.ToAffine(), signature, message);
    }

    // Compressed G2 point
    public readonly ref struct Signature()
    {
        public readonly ReadOnlySpan<byte> Bytes;

        public Signature(ReadOnlySpan<byte> s) : this()
        {
            if (s.Length != 96)
            {
                throw new Bls.BlsException(Bls.ERROR.BADENCODING);
            }
            Bytes = s;
        }
    }
}
