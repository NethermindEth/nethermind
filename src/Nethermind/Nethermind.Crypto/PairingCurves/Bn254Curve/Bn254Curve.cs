// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;

namespace Nethermind.Crypto.PairingCurves;

public partial class Bn254Curve
{
    public static readonly BigInteger BaseFieldOrder = new([0x30, 0x64, 0x4E, 0x72, 0xE1, 0x31, 0xA0, 0x29, 0xB8, 0x50, 0x45, 0xB6, 0x81, 0x81, 0x58, 0x5D, 0x97, 0x81, 0x6A, 0x91, 0x68, 0x71, 0xCA, 0x8D, 0x3C, 0x20, 0x8C, 0x16, 0xD8, 0x7C, 0xFD, 0x47], true, true);
    public static readonly BigInteger SubgroupOrder = new([0x30, 0x64, 0x4E, 0x72, 0xE1, 0x31, 0xA0, 0x29, 0xB8, 0x50, 0x45, 0xB6, 0x81, 0x81, 0x58, 0x5D, 0x28, 0x33, 0xE8, 0x48, 0x79, 0xB9, 0x70, 0x91, 0x43, 0xE1, 0xF5, 0x93, 0xF0, 0x00, 0x00, 0x01], true, true);
    public static readonly BigInteger X = -new BigInteger(0xd201000000010000);

    public static bool Pairing(G1 g1, G2 g2)
    {
        Span<byte> encoded = stackalloc byte[192];
        Span<byte> output = stackalloc byte[32];
        g1.Encode(encoded[..64]);
        g2.Encode(encoded[64..]);
        Pairings.Bn254Pairing(encoded, output);
        return output[31] == 1;
    }

    public static bool Pairing2(G1 a1, G2 a2, G1 b1, G2 b2)
    {
        Span<byte> encoded = stackalloc byte[192 * 2];
        Span<byte> output = stackalloc byte[32];
        a1.Encode(encoded[..64]);
        a2.Encode(encoded[64..192]);
        b1.Encode(encoded[192..256]);
        b2.Encode(encoded[256..]);
        Pairings.Bn254Pairing(encoded, output);
        return output[31] == 1;
    }

    public static bool PairingsEqual(G1 a1, G2 a2, G1 b1, G2 b2)
    {
        return Pairing2(-a1, a2, b1, b2);
    }

    public class BaseField : IBaseField
    {
        public static readonly BaseField Instance = new();
        private BaseField() { }

        public BigInteger GetOrder()
        {
            return BaseFieldOrder;
        }

        public int GetSize()
        {
            return 32;
        }
    }

    public class Params : ICurveParams<BaseField>
    {
        public static readonly Params Instance = new();
        private Params() { }

        public (Fq<BaseField>, Fq<BaseField>) G1FromX(Fq<BaseField> x, bool sign)
        {
            (Fq<BaseField>, Fq<BaseField>) res = Fq<BaseField>.Sqrt((x ^ Fq(3)) + Fq(3));
            return (x, sign ? res.Item1 : res.Item2);
        }

        public bool G1IsOnCurve((Fq<BaseField>, Fq<BaseField>)? p)
        {
            if (p is null)
            {
                return false;
            }
            else
            {
                return (p.Value.Item1 ^ Fq(3)) + Fq(3) == p.Value.Item2 * p.Value.Item2;
            }
        }

        public (Fq2<BaseField>, Fq2<BaseField>) G2FromX(Fq2<BaseField> x, bool sign)
        {
            return (x, Fq2<BaseField>.Sqrt((x ^ Fq(3)) + (Fq2(3) / Fq2(1, 9)), sign));
        }

        public bool G2IsOnCurve((Fq2<BaseField>, Fq2<BaseField>)? p)
        {
            if (p is null)
            {
                return false;
            }
            else
            {
                return (p.Value.Item1 ^ Fq(3)) + (Fq2(3) / Fq2(1, 9)) == p.Value.Item2 * p.Value.Item2;
            }
        }

        public BaseField GetBaseField()
        {
            return BaseField.Instance;
        }

        public BigInteger GetSubgroupOrder()
        {
            return SubgroupOrder;
        }

        public BigInteger GetX()
        {
            return X;
        }
    }

}
