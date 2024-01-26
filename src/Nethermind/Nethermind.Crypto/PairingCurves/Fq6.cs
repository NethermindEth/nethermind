// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Crypto.PairingCurves;

public class Fq6<T>(Fq2<T> a, Fq2<T> b, Fq2<T> c, T baseField) where T : IBaseField
{
    public Fq2<T> a = a, b = b, c = c;
    public readonly T BaseField = baseField;

    public static Fq6<T> FromFq(Fq<T> x)
    {
        var zero = Fq2<T>.FromFq(new(0, x.BaseField));
        return new(zero, zero, Fq2<T>.FromFq(x), x.BaseField);
    }

    public byte[] ToBytes()
    {
        int s = BaseField.GetSize();
        byte[] res = new byte[s * 6];
        a.ToBytes().CopyTo(res.AsSpan()[..(s * 2)]);
        b.ToBytes().CopyTo(res.AsSpan()[(s * 2)..(s * 4)]);
        c.ToBytes().CopyTo(res.AsSpan()[(s * 4)..]);
        return res;
    }

    public static Fq6<T> operator -(Fq6<T> x)
    {
        return new(-x.a, -x.b, -x.c, x.BaseField);
    }

    public static Fq6<T> operator +(Fq6<T> x, Fq6<T> y)
    {
        return new(x.a + y.a, x.b + y.b, x.c + y.c, x.BaseField);
    }

    public override bool Equals(object obj) => Equals(obj as Fq6<T>);

    public bool Equals(Fq6<T> p)
    {
        return (a == p.a) && (b == p.b) && (c == p.c);
    }
    public override int GetHashCode() => (a, b).GetHashCode();

    public static bool operator ==(Fq6<T> x, Fq6<T> y)
    {
        return x.Equals(y);
    }

    public static bool operator !=(Fq6<T> x, Fq6<T> y) => !x.Equals(y);

    public static Fq6<T> MulNonRes(Fq6<T> c)
    {
        return new(c.b, c.c, Fq2<T>.MulNonRes(c.a), c.BaseField);
    }
    public static Fq6<T> Inv(Fq6<T> c)
    {
        Fq2<T> t0 = (c.c * c.c) - Fq2<T>.MulNonRes(c.b * c.a);
        Fq2<T> t1 = Fq2<T>.MulNonRes(c.a * c.a) - (c.b * c.c);
        Fq2<T> t2 = (c.b * c.b) - (c.a * c.c);
        Fq2<T> factor = Fq2<T>.Inv((c.c * t0) + Fq2<T>.MulNonRes(c.a * t1) + Fq2<T>.MulNonRes(c.b * t2));
        return new(t2 * factor, t1 * factor, t0 * factor, c.BaseField);
    }

    public static Fq6<T> operator *(Fq6<T> x, Fq6<T> y)
    {
        Fq2<T> t0 = x.c * y.c;
        Fq2<T> t1 = (x.c * y.b) + (x.b * y.c);
        Fq2<T> t2 = (x.c * y.a) + (x.b * y.b) + (x.a * y.c);
        Fq2<T> t3 = Fq2<T>.MulNonRes((x.b * y.a) + (x.a * y.b));
        Fq2<T> t4 = Fq2<T>.MulNonRes(x.a * y.a);
        return new(t2, t1 + t4, t0 + t3, x.BaseField);
    }

    public static Fq6<T> operator -(Fq6<T> x, Fq6<T> y)
    {
        return new(x.a - y.a, x.b - y.b, x.c - y.c, x.BaseField);
    }

    public static Fq6<T> operator /(Fq6<T> x, Fq6<T> y)
    {
        return x * Inv(y);
    }
}
