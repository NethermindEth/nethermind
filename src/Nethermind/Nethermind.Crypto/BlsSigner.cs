// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Crypto;

using G1 = Bls.P1;
using G2 = Bls.P2;
using GT = Bls.PT;

public class BlsSigner
{
    internal static readonly string Cryptosuite = "BLS_SIG_BLS12381G1_XMD:SHA-256_SSWU_RO_NUL_";
    internal static int InputLength = 64;

    public static Signature Sign(PrivateKey privateKey, ReadOnlySpan<byte> message)
    {
        G1 p = new();
        p.hash_to(message.ToArray(), Cryptosuite);
        p.sign_with(new Bls.SecretKey(privateKey.Bytes, Bls.ByteOrder.LittleEndian));
        Signature s = new()
        {
            Bytes = p.compress()
        };
        return s;
    }

    public static bool Verify(PublicKey publicKey, Signature signature, ReadOnlySpan<byte> message)
    {
        G1? sig;
        try
        {
            sig = new(signature.Bytes);
        }
        catch (Exception)
        {
            // point not on curve
            return false;
        }

        GT p1 = new(sig.Value, G2.generator());

        G1 m = new();
        m.hash_to(message.ToArray(), Cryptosuite);
        G2 pk = new(publicKey.Bytes);
        GT p2 = new(m, pk);

        return GT.finalverify(p1, p2);
    }

    public static PublicKey GetPublicKey(PrivateKey privateKey)
    {
        Bls.SecretKey sk = new(privateKey.Bytes, Bls.ByteOrder.LittleEndian);
        G2 p = new(sk);
        PublicKey pk = new()
        {
            Bytes = p.compress()
        };
        return pk;
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
}
