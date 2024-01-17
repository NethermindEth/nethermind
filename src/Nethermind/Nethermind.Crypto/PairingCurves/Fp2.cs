// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Crypto.PairingCurves;

public class Fp2<T>(Fp<T> a, Fp<T> b, T baseField) where T : IBaseField
{
    // ai + b
    public Fp<T> a = a, b = b;
    private readonly T _baseField = baseField;

    public static (Fp<T>, Fp<T>) Len(Fp2<T> x)
    {
        Fp<T> two = new(2, x._baseField);
        return Fp<T>.Sqrt((x.a ^ two) + (x.b ^ two));
    }

    public static Fp2<T> operator -(Fp2<T> x)
    {
        return new(-x.a, -x.b, x._baseField);
    }

    public static Fp2<T> operator ^(Fp2<T> x, int exp)
    {
        Fp2<T> a = x;
        Fp2<T> b = new(new Fp<T>(0, x._baseField), new Fp<T>(1, x._baseField), x._baseField);

        for (int i = 0; i < 32; i++)
        {
            if ((exp & 1) == 1)
            {
                b *= a;
            }
            a *= a;
            exp >>= 1;
        }

        return b;
    }

    public static Fp2<T> operator +(Fp2<T> x, Fp2<T> y)
    {
        return new(x.a + y.a, x.b + y.b, x._baseField);
    }

    public override bool Equals(object obj) => Equals(obj as Fp2<T>);

    public bool Equals(Fp2<T> p)
    {
        return (a == p.a) && (b == p.b);
    }
    public override int GetHashCode() => (a, b).GetHashCode();

    public static bool operator ==(Fp2<T> x, Fp2<T> y)
    {
        return x.Equals(y);
    }

    public static bool operator !=(Fp2<T> x, Fp2<T> y) => !x.Equals(y);

    public static Fp2<T> Inv(Fp2<T> c)
    {
        Fp<T> two = new(2, c._baseField);
        Fp<T> lenSqr = (c.a ^ two) + (c.b ^ two);
        return new((-c.a) / lenSqr, c.b / lenSqr, c._baseField);
    }

    public static Fp2<T> operator *(Fp2<T> x, Fp2<T> y)
    {
        return new((x.a * y.b) + (x.b * y.a), (x.b * y.b) - (x.a * y.a), x._baseField);
    }

    public static Fp2<T> operator -(Fp2<T> x, Fp2<T> y)
    {
        return x + (-y);
    }

    public static Fp2<T> operator /(Fp2<T> x, Fp2<T> y)
    {
        return x * Inv(y);
    }

    public static Fp2<T> Sqrt(Fp2<T> x, bool sign)
    {
        (Fp<T>, Fp<T>) l = Len(x);
        // test all possible signs
        for (int i = 0; i < 2; i++)
        {
            Fp<T> lCurrent = i == 0 ? l.Item1 : l.Item2;
            for (int j = 0; j < 2; j++)
            {
                // sqrt(x) = ai + b
                Fp<T> two = new(2, x._baseField);
                (Fp<T>, Fp<T>) b = Fp<T>.Sqrt(((-x.b) + lCurrent) / two);
                (Fp<T>, Fp<T>) a = Fp<T>.Sqrt((x.b + lCurrent) / two);

                Fp<T> bCurrent = j == 0 ? b.Item1 : b.Item2;

                for (int k = 0; k < 2; k++)
                {
                    Fp<T> aCurrent = k == 0 ? a.Item1 : a.Item2;

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

                    return new Fp2<T>(aCurrent, bCurrent, x._baseField);
                }
            }
        }
        throw new Exception("Could not calculate sqrt of Fp2 point");
    }
}
