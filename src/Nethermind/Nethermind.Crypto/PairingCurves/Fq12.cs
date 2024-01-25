// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;

namespace Nethermind.Crypto.PairingCurves;
public class Fq12<T>(Fq6<T> a, Fq6<T> b, T baseField) where T : IBaseField
{
    public Fq6<T> a = a, b = b;
    public readonly T BaseField = baseField;

    public static Fq12<T> FromFq(Fq<T> x)
    {
        var zero = Fq6<T>.FromFq(new(0, x.BaseField));
        return new(zero, Fq6<T>.FromFq(x), x.BaseField);
    }

    public static Fq12<T> operator -(Fq12<T> x)
    {
        return new(-x.a, -x.b, x.BaseField);
    }

    public static Fq12<T> operator ^(Fq12<T> x, Fq<T> exp)
    {
        Fq12<T> a = x;
        Fq12<T> b = Fq12<T>.FromFq(new Fq<T>(1, x.BaseField));

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

    public static Fq12<T> operator +(Fq12<T> x, Fq12<T> y)
    {
        return new(x.a + y.a, x.b + y.b, x.BaseField);
    }

    public override bool Equals(object obj) => Equals(obj as Fq12<T>);

    public bool Equals(Fq12<T> p)
    {
        return (a == p.a) && (b == p.b);
    }
    public override int GetHashCode() => (a, b).GetHashCode();

    public static bool operator ==(Fq12<T> x, Fq12<T> y)
    {
        return x.Equals(y);
    }

    public static bool operator !=(Fq12<T> x, Fq12<T> y) => !x.Equals(y);

    public static Fq12<T> MulNonRes(Fq12<T> c)
    {
        // todo: work out actual
        return new(c.a + c.b, c.b - c.a, c.BaseField);
    }

    public static Fq12<T> Inv(Fq12<T> c)
    {
        Fq6<T> factor = Fq6<T>.Inv((c.b * c.b) - Fq6<T>.MulNonRes(c.a * c.a));
        return new(-c.a * factor, c.b * factor, c.BaseField);
    }

    public static Fq12<T> operator *(Fq12<T> x, Fq12<T> y)
    {
        return new((x.a * y.b) + (x.b * y.a), (x.b * y.b) + Fq6<T>.MulNonRes(x.a * y.a), x.BaseField);
    }

    public static Fq12<T> operator -(Fq12<T> x, Fq12<T> y)
    {
        return new(x.a - y.a, x.b - y.b, x.BaseField);
    }

    public static Fq12<T> operator /(Fq12<T> x, Fq12<T> y)
    {
        return x * Inv(y);
    }
}
