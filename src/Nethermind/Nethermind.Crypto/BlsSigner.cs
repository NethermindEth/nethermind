// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text;

namespace Nethermind.Crypto;

using G1 = Bls.P1;
using G2 = Bls.P2;
using GT = Bls.PT;

public class BlsSigner
{
    internal static readonly byte[] Cryptosuite = Encoding.UTF8.GetBytes("BLS_SIG_BLS12381G2_XMD:SHA-256_SSWU_RO_POP_");
    internal static int InputLength = 64;

    public static Signature Sign(Bls.SecretKey sk, ReadOnlySpan<byte> message)
    {
        G2 p = new();
        p.HashTo(message, Cryptosuite);
        p.SignWith(sk);
        return new(new byte[96]);
    }

    public static bool Verify(G1 publicKey, Signature signature, ReadOnlySpan<byte> message)
    {
        try
        {
            G2 sig = new(signature.Bytes);
            GT p1 = new(sig, G1.Generator());

            G2 m = new();
            m.HashTo(message, Cryptosuite);
            GT p2 = new(m, publicKey);

            return GT.FinalVerify(p1, p2);
        }
        catch (Bls.Exception)
        {
            // point not on curve
            return false;
        }
    }

    public static G1 GetPublicKey(Bls.SecretKey sk)
        => new(sk);

    // Compressed G2 point
    public readonly ref struct Signature()
    {
        public readonly ReadOnlySpan<byte> Bytes;

        public Signature(ReadOnlySpan<byte> s) : this()
        {
            if (s.Length != 96)
            {
                throw new Bls.Exception(Bls.ERROR.BADENCODING);
            }
            Bytes = s;
        }
    }
}
