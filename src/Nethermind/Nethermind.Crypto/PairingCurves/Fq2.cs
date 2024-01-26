// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;

namespace Nethermind.Crypto.PairingCurves;

public class Fq2<T>(Fq<T> a, Fq<T> b, T baseField) where T : IBaseField
{
    // ai + b
    public Fq<T> a = a, b = b;
    public readonly T BaseField = baseField;

    public static Fq2<T> FromFq(Fq<T> x)
    {
        return new(new Fq<T>(0, x.BaseField), x, x.BaseField);
    }

    public byte[] ToBytes()
    {
        int s = BaseField.GetSize();
        byte[] res = new byte[s * 2];
        a.ToBytes().CopyTo(res.AsSpan()[..s]);
        b.ToBytes().CopyTo(res.AsSpan()[s..]);
        return res;
    }

    public static (Fq<T>, Fq<T>) Len(Fq2<T> x)
    {
        Fq<T> two = new(2, x.BaseField);
        return Fq<T>.Sqrt((x.a ^ two) + (x.b ^ two));
    }

    public static Fq2<T> operator -(Fq2<T> x)
    {
        return new(-x.a, -x.b, x.BaseField);
    }

    public static Fq2<T> operator ^(Fq2<T> x, Fq<T> exp)
    {
        Fq2<T> a = x;
        Fq2<T> b = Fq2<T>.FromFq(new Fq<T>(1, x.BaseField));

        for (BigInteger v = exp.Value; v != 0; v >>= 1)
        {
            if ((v & 1) == 1)
            {
                b *= a;
            }
            a *= a;
        }

        return b;
    }

    public static Fq2<T> operator +(Fq2<T> x, Fq2<T> y)
    {
        return new(x.a + y.a, x.b + y.b, x.BaseField);
    }

    public override bool Equals(object obj) => Equals(obj as Fq2<T>);

    public bool Equals(Fq2<T> p)
    {
        return (a == p.a) && (b == p.b);
    }
    public override int GetHashCode() => (a, b).GetHashCode();

    public static bool operator ==(Fq2<T> x, Fq2<T> y)
    {
        return x.Equals(y);
    }

    public static bool operator !=(Fq2<T> x, Fq2<T> y) => !x.Equals(y);

    public static Fq2<T> MulNonRes(Fq2<T> c)
    {
        return new(c.a + c.b, c.b - c.a, c.BaseField);
    }

    public static Fq2<T> Inv(Fq2<T> c)
    {
        Fq<T> factor = Fq<T>.Inv((c.a * c.a) + (c.b * c.b));
        return new(-c.a * factor, c.b * factor, c.BaseField);
    }

    public static Fq2<T> operator *(Fq2<T> x, Fq2<T> y)
    {
        return new((x.a * y.b) + (x.b * y.a), (x.b * y.b) - (x.a * y.a), x.BaseField);
    }


    public static Fq2<T> operator -(Fq2<T> x, Fq2<T> y)
    {
        return new(x.a - y.a, x.b - y.b, x.BaseField);
    }

    public static Fq2<T> operator /(Fq2<T> x, Fq2<T> y)
    {
        return x * Inv(y);
    }

    public static Fq2<T> Sqrt(Fq2<T> x, bool sign)
    {
        (Fq<T>, Fq<T>) l = Len(x);
        // test all possible signs
        for (int i = 0; i < 2; i++)
        {
            Fq<T> lCurrent = i == 0 ? l.Item1 : l.Item2;
            for (int j = 0; j < 2; j++)
            {
                // sqrt(x) = ai + b
                Fq<T> two = new(2, x.BaseField);
                (Fq<T>, Fq<T>) b = Fq<T>.Sqrt(((-x.b) + lCurrent) / two);
                (Fq<T>, Fq<T>) a = Fq<T>.Sqrt((x.b + lCurrent) / two);

                Fq<T> bCurrent = j == 0 ? b.Item1 : b.Item2;

                for (int k = 0; k < 2; k++)
                {
                    Fq<T> aCurrent = k == 0 ? a.Item1 : a.Item2;

                    if ((bCurrent < aCurrent) != sign)
                    {
                        continue;
                    }

                    if (two * aCurrent * bCurrent != x.a)
                    {
                        continue;
                    }

                    if ((bCurrent ^ two) - (aCurrent ^ two) != x.b)
                    {
                        continue;
                    }

                    return new Fq2<T>(aCurrent, bCurrent, x.BaseField);
                }
            }
        }
        throw new Exception("Could not calculate sqrt of Fq2 point");
    }
}
