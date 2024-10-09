// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text;
using Nethermind.Core.Collections;

namespace Nethermind.Crypto;

using G1 = Bls.P1;
using G1Affine = Bls.P1Affine;
using G2 = Bls.P2;
using G2Affine = Bls.P2Affine;
using GT = Bls.PT;

public class BlsSigner
{
    private static readonly byte[] Cryptosuite = Encoding.UTF8.GetBytes("BLS_SIG_BLS12381G2_XMD:SHA-256_SSWU_RO_POP_");
    private const int InputLength = 64;

    public static Signature Sign(Bls.SecretKey sk, ReadOnlySpan<byte> message)
    {
        G2 p = new(stackalloc long[G2.Sz]);
        p.HashTo(message, Cryptosuite);
        p.SignWith(sk);
        return new(p.Compress());
    }

    public static void GetPublicKey(G1 res, Bls.SecretKey sk)
        => res.FromSk(sk);

    public static G1 GetPublicKey(Bls.SecretKey sk)
    {
        G1 p = new();
        GetPublicKey(p, sk);
        return p;
    }

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
