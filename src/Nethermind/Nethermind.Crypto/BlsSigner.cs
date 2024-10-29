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
    // buf must be of size G2.Sz
    public static Signature Sign(Span<long> buf, Bls.SecretKey sk, ReadOnlySpan<byte> message)
    {
        if (buf.Length != G2.Sz)
        {
            throw new Bls.BlsException(Bls.ERROR.WRONGSIZE);
        }

        G2 p = new(buf);
        Signature s = new(p);
        s.Sign(sk, message);
        return s;
    }

    public static Signature Sign(Bls.SecretKey sk, ReadOnlySpan<byte> message)
        => Sign(new long[G2.Sz], sk, message);

    public static bool Verify(G1Affine publicKey, Signature signature, ReadOnlySpan<byte> message)
    {
        int len = 2 * GT.Sz;
        using ArrayPoolList<long> buf = new(len, len);

        GT p1 = new(buf.AsSpan()[..GT.Sz]);
        p1.MillerLoop(signature.Point, G1Affine.Generator(stackalloc long[G1Affine.Sz]));

        G2 m = new(stackalloc long[G2.Sz]);
        m.HashTo(message, Cryptosuite);
        GT p2 = new(buf.AsSpan()[GT.Sz..]);
        p2.MillerLoop(m.ToAffine(), publicKey);

        return GT.FinalVerify(p1, p2);
    }

    [SkipLocalsInit]
    public static bool Verify(G1Affine publicKey, ReadOnlySpan<byte> sigBytes, ReadOnlySpan<byte> message)
    {
        G2 p = new(stackalloc long[G2.Sz]);
        Signature s = new(p);

        //TryDecode
        //cache Sz size
        try
        {
            p.Decode(sigBytes);
        }
        catch (Bls.BlsException)
        {
            // invalid signature
            return false;
        }

        return Verify(publicKey, s, message);
    }

    public static bool VerifyAggregate(AggregatedPublicKey aggregatedPublicKey, Signature signature, ReadOnlySpan<byte> message)
        => Verify(aggregatedPublicKey.PublicKey, signature, message);

    public readonly ref struct Signature
    {
        public readonly ReadOnlySpan<byte> Bytes { get => _point.Compress(); }
        public readonly G2Affine Point { get => _point.ToAffine(); }
        private readonly G2 _point;

        public Signature()
        {
            _point = new();
        }

        public Signature(G2 point)
        {
            _point = point;
        }

        public void Decode(ReadOnlySpan<byte> bytes)
            => _point.Decode(bytes);

        public void Sign(Bls.SecretKey sk, ReadOnlySpan<byte> message)
        {
            _point.HashTo(message, Cryptosuite);
            _point.SignWith(sk);
        }

        public void Aggregate(Signature s)
            => _point.Aggregate(s.Point);
    }

    public readonly ref struct AggregatedPublicKey
    {
        public G1Affine PublicKey { get => _point.ToAffine(); }
        private readonly G1 _point;

        public AggregatedPublicKey()
        {
            _point = new();
        }

        public AggregatedPublicKey(G1 point)
        {
            point.Zero();
            _point = point;
        }

        public void Aggregate(G1Affine publicKey)
            => _point.Aggregate(publicKey);

        public void Aggregate(ReadOnlySpan<byte> publicKeyBytes)
        {
            if (publicKeyBytes.Length != PkCompressedSz)
            {
                throw new Bls.BlsException(Bls.ERROR.WRONGSIZE);
            }

            G1Affine pk = new(stackalloc long[G1Affine.Sz]);
            pk.Decode(publicKeyBytes);
            Aggregate(pk);
        }
    }
}
