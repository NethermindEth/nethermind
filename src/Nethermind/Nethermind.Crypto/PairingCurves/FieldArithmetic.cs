// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Numerics;
using Nethermind.Int256;
namespace Nethermind.Crypto.PairingCurves;

public class FieldArithmetic<T> where T : IBaseField
{
    public static Fq<T> Fq(BigInteger x, T baseField)
    {
        return new(x, baseField);
    }

    public static Fq2<T> Fq2(Fq<T> a, Fq<T> b)
    {
        return new(a, b, a.BaseField);
    }

    public static Fq2<T> Fq2(Fq<T> b)
    {
        return Fq2<T>.FromFq(b);
    }

    public static Fq2<T> Fq2(BigInteger b, T baseField)
    {
        return Fq2<T>.FromFq(Fq(b, baseField));
    }

    public static Fq6<T> Fq6(Fq2<T> a, Fq2<T> b, Fq2<T> c)
    {
        return new(a, b, c, a.BaseField);
    }

    public static Fq6<T> Fq6(BigInteger b, T baseField)
    {
        return Fq6<T>.FromFq(Fq(b, baseField));
    }

    public static Fq6<T> Fq6(Fq<T> b)
    {
        return Fq6<T>.FromFq(b);
    }

    public static Fq12<T> Fq12(Fq6<T> a, Fq6<T> b)
    {
        return new(a, b, a.BaseField);
    }

    public static Fq12<T> Fq12(BigInteger b, T baseField)
    {
        return Fq12<T>.FromFq(Fq(b, baseField));
    }

    public static Fq12<T> Fq12(Fq<T> b)
    {
        return Fq12<T>.FromFq(b);
    }


    public static (Fq2<T>, Fq2<T>)? G2Multiply(UInt256 s, (Fq2<T>, Fq2<T>)? p, ICurveParams<T> curve)
    {
        T f = curve.GetBaseField();
        if (!curve.G2IsOnCurve(p))
        {
            return null;
        }

        (Fq2<T>, Fq2<T>)? a = p;
        (Fq2<T>, Fq2<T>)? b = null;

        for (; s > 0; s >>= 1)
        {
            if ((s & 1) == 1)
            {
                b = G2Add(a, b, f);
            }
            a = G2Add(a, a, f);
        }

        return b;
    }

    public static (Fq2<T>, Fq2<T>)? G2Add((Fq2<T>, Fq2<T>)? p, (Fq2<T>, Fq2<T>)? q, T f)
    {
        if (!p.HasValue)
        {
            return q;
        }

        if (!q.HasValue)
        {
            return p;
        }

        Fq2<T> x1 = p.Value.Item1;
        Fq2<T> y1 = p.Value.Item2;
        Fq2<T> x2 = q.Value.Item1;
        Fq2<T> y2 = q.Value.Item2;

        if (p == q)
        {
            if (y1 == Fq2(0, f))
            {
                return null;
            }

            Fq2<T> lambda = Fq2(3, f) * (x1 ^ Fq(2, f)) / (Fq2(2, f) * y1);
            Fq2<T> x = (lambda ^ Fq(2, f)) - (Fq2(2, f) * x2);
            Fq2<T> y = (lambda * (x1 - x)) - y1;

            return (x, y);
        }
        else if (x1 != x2)
        {
            Fq2<T> lambda = (y2 - y1) / (x2 - x1);
            Fq2<T> x = (lambda ^ Fq(2, f)) - x1 - x2;
            Fq2<T> y = (lambda * (x1 - x)) - y1;

            return (x, y);
        }
        else
        {
            return null;
        }
    }

    public static Fq12<T>? Pairing((Fq<T>, Fq<T>) p, (Fq2<T>, Fq2<T>) q, ICurveParams<T> curve)
    {
        T f = curve.GetBaseField();
        if (curve.G1IsOnCurve(p) && curve.G2IsOnCurve(q))
        {
            return Miller(p, q, curve) ^ Fq(((f.GetOrder() ^ 12) - 1) / curve.GetSubgroupOrder(), f);
        }
        return null;
    }

    private static Fq12<T> Miller((Fq<T>, Fq<T>) p, (Fq2<T>, Fq2<T>) q, ICurveParams<T> curve)
    {
        T f = curve.GetBaseField();

        Fq12<T> acc = Fq12(Fq(1, f));
        (Fq2<T>, Fq2<T>)? r = q;

        foreach (bool b in ToBitListBigEndian(curve.GetX()))
        {
            acc = acc * acc * DoubleEval(r!.Value, p, f);
            r = G2Add(r, r, f);

            if (b)
            {
                acc *= AddEval(r!.Value, q, p, f);
                r = G2Add(r, q, f);
            }
        }

        return acc;
    }

    private static IEnumerable<bool> ToBitListBigEndian(BigInteger x)
    {
        LinkedList<bool> bits = [];
        for (; x > 0; x >>= 1)
        {
            bits.AddFirst((x & 1) == 1);
        }
        return bits;
    }

    private static Fq12<T> DoubleEval((Fq2<T>, Fq2<T>) r, (Fq<T>, Fq<T>) p, T f)
    {
        (Fq12<T>, Fq12<T>) wideR = Untwist(r, f);
        Fq12<T> slope = Fq12(3, f) * wideR.Item1 * wideR.Item1 / (Fq12(2, f) * wideR.Item2);
        Fq12<T> v = wideR.Item2 - slope * wideR.Item1;
        return Fq12(p.Item2) - (Fq12(p.Item1) * slope) - v;
    }

    private static Fq12<T> AddEval((Fq2<T>, Fq2<T>) r, (Fq2<T>, Fq2<T>) q, (Fq<T>, Fq<T>) p, T f)
    {
        (Fq12<T>, Fq12<T>) wideR = Untwist(r, f);
        (Fq12<T>, Fq12<T>) wideQ = Untwist(q, f);
        if ((wideR.Item1 == wideQ.Item1) && (wideR.Item2 == -wideQ.Item2))
        {
            return Fq12(p.Item1) - wideR.Item1;
        }
        else
        {
            Fq12<T> slope = (wideQ.Item2 - wideR.Item2) / (wideQ.Item1 - wideR.Item1);
            Fq12<T> v = ((wideQ.Item2 * wideR.Item1) - (wideR.Item2 * wideQ.Item1)) / (wideR.Item1 - wideQ.Item1);
            return Fq12(p.Item2) - (Fq12(p.Item1) * slope) - v;
        }
    }

    private static (Fq12<T>, Fq12<T>) Untwist((Fq2<T>, Fq2<T>) p, T f)
    {
        Fq6<T> root = Fq6(Fq2(0, f), Fq2(1, f), Fq2(0, f));
        Fq12<T> wideX = Fq12(Fq6(0, f), Fq6(Fq2(0, f), Fq2(0, f), p.Item1)) / Fq12(Fq6(0, f), root);
        Fq12<T> wideY = Fq12(Fq6(0, f), Fq6(Fq2(0, f), Fq2(0, f), p.Item2)) / Fq12(root, Fq6(0, f));
        return (wideX, wideY);
    }
}
