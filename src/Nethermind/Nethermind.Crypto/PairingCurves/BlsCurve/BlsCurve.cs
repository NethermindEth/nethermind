// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;

namespace Nethermind.Crypto.PairingCurves;

// BLS12-381 curve
public partial class BlsCurve
{
    public static readonly BigInteger BaseFieldOrder = new([0x1a, 0x01, 0x11, 0xea, 0x39, 0x7f, 0xe6, 0x9a, 0x4b, 0x1b, 0xa7, 0xb6, 0x43, 0x4b, 0xac, 0xd7, 0x64, 0x77, 0x4b, 0x84, 0xf3, 0x85, 0x12, 0xbf, 0x67, 0x30, 0xd2, 0xa0, 0xf6, 0xb0, 0xf6, 0x24, 0x1e, 0xab, 0xff, 0xfe, 0xb1, 0x53, 0xff, 0xff, 0xb9, 0xfe, 0xff, 0xff, 0xff, 0xff, 0xaa, 0xab], true, true);
    public static readonly BigInteger SubgroupOrder = new([0x73, 0xed, 0xa7, 0x53, 0x29, 0x9d, 0x7d, 0x48, 0x33, 0x39, 0xd8, 0x08, 0x09, 0xa1, 0xd8, 0x05, 0x53, 0xbd, 0xa4, 0x02, 0xff, 0xfe, 0x5b, 0xfe, 0xff, 0xff, 0xff, 0xff, 0x00, 0x00, 0x00, 0x01], true, true);
    public static readonly BigInteger X = -new BigInteger(0xd201000000010000);

    public static bool PairingVerify(G1 p, G2 q)
    {
        Span<byte> encoded = stackalloc byte[384];
        Span<byte> output = stackalloc byte[32];
        p.Encode(encoded[..128]);
        q.Encode(encoded[128..]);
        Pairings.BlsPairing(encoded, output);
        return output[31] == 1;
    }

    public static bool PairingVerify2(G1 a1, G2 a2, G1 b1, G2 b2)
    {
        Span<byte> encoded = stackalloc byte[384 * 2];
        Span<byte> output = stackalloc byte[32];
        a1.Encode(encoded[..128]);
        a2.Encode(encoded[128..384]);
        b1.Encode(encoded[384..512]);
        b2.Encode(encoded[512..]);
        Pairings.BlsPairing(encoded, output);
        return output[31] == 1;
    }

    public static bool PairingsEqual(G1 a1, G2 a2, G1 b1, G2 b2)
    {
        return PairingVerify2(-a1, a2, b1, b2);
    }

    public static GT Pairing(G1 p, G2 q)
    {
        if (p == G1.Zero || q == G2.Zero || !p.IsInSubgroup() || !q.IsInSubgroup())
        {
            return new(null);
        }

        Fq12<BaseField>? r = FieldArithmetic<BaseField>.Pairing(p.ToFq()!.Value, q.ToFq()!.Value, Params.Instance);
        return new(r);
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
            return 48;
        }
    }

    public class Params : ICurveParams<BaseField>
    {
        public static readonly Params Instance = new();
        private Params() { }

        public (Fq<BaseField>, Fq<BaseField>) G1FromX(Fq<BaseField> x, bool sign)
        {
            (Fq<BaseField>, Fq<BaseField>) res = Fq<BaseField>.Sqrt((x ^ Fq(3)) + Fq(4));
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
                return (p.Value.Item1 ^ Fq(3)) + Fq(4) == p.Value.Item2 * p.Value.Item2;
            }
        }

        public (Fq2<BaseField>, Fq2<BaseField>) G2FromX(Fq2<BaseField> x, bool sign)
        {
            return (x, Fq2<BaseField>.Sqrt((x ^ Fq(3)) + Fq2<BaseField>.MulNonRes(Fq2(4)), sign));
        }

        public bool G2IsOnCurve((Fq2<BaseField>, Fq2<BaseField>)? p)
        {
            if (p is null)
            {
                return false;
            }
            else
            {
                return (p.Value.Item1 ^ Fq(3)) + Fq2<BaseField>.MulNonRes(Fq2(4)) == p.Value.Item2 * p.Value.Item2;
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
