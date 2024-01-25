// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using Nethermind.Core.Extensions;

namespace Nethermind.Crypto.PairingCurves;

public class Fq<T> where T : IBaseField
{
    public readonly BigInteger Value;
    public readonly T BaseField;

    public Fq(ReadOnlySpan<byte> bytes, T baseField)
    {
        if (bytes.Length != baseField.GetSize())
        {
            throw new Exception("Field point must have " + baseField.GetSize() + " bytes");
        }
        Value = Normalise(new BigInteger(bytes, true, true), baseField.GetOrder());
        BaseField = baseField;
    }

    public Fq(BigInteger value, T baseField)
    {
        Value = Normalise(value, baseField.GetOrder());
        BaseField = baseField;
    }

    private static BigInteger Normalise(BigInteger x, BigInteger baseFieldOrder)
    {
        BigInteger unnormalised = x % baseFieldOrder;
        // return (unnormalised.Sign == 1) ? unnormalised : (baseFieldOrder + unnormalised);
        return unnormalised >= 0 ? unnormalised : (baseFieldOrder + unnormalised);
    }

    public byte[] ToBytes()
    {
        return Value.ToBigEndianByteArray(BaseField.GetSize());
    }

    public static (Fq<T>, Fq<T>) Sqrt(Fq<T> x)
    {
        Fq<T> res = x ^ (new Fq<T>(new BigInteger(1), x.BaseField) / new Fq<T>(new BigInteger(4), x.BaseField));
        return (res, -res);
    }

    public static Fq<T> operator -(Fq<T> x)
    {
        return new(x.BaseField.GetOrder() - x.Value, x.BaseField);
    }

    public static Fq<T> operator ^(Fq<T> x, Fq<T> exp)
    {
        return new(BigInteger.ModPow(x.Value, exp.Value, x.BaseField.GetOrder()), x.BaseField);
    }

    public static Fq<T> operator +(Fq<T> x, Fq<T> y)
    {
        return new(x.Value + y.Value, x.BaseField);
    }

    public static bool operator <(Fq<T> x, Fq<T> y)
    {
        return x.Value < y.Value;
    }

    public static bool operator >(Fq<T> x, Fq<T> y)
    {
        return x.Value > y.Value;
    }
    public override bool Equals(object obj) => Equals(obj as Fq<T>);

    public bool Equals(Fq<T> p)
    {
        return Value == p.Value;
    }
    public override int GetHashCode() => Value.GetHashCode();

    public static bool operator ==(Fq<T> x, Fq<T> y)
    {
        return x.Value == y.Value;
    }

    public static bool operator !=(Fq<T> x, Fq<T> y) => !x.Equals(y);

    private static BigInteger gcdExtended(BigInteger a, BigInteger b, ref BigInteger x, ref BigInteger y)
    {
        if (a == 0)
        {
            x = 0;
            y = 1;
            return b;
        }

        BigInteger x1 = 1, y1 = 1;
        BigInteger gcd = gcdExtended(b % a, a, ref x1, ref y1);

        x = y1 - (b / a) * x1;
        y = x1;

        return gcd;
    }

    public static Fq<T> MulNonRes(Fq<T> c)
    {
        return c;
    }

    public static Fq<T> Inv(Fq<T> c)
    {
        BigInteger x = 1;
        BigInteger y = 1;
        gcdExtended(c.Value, c.BaseField.GetOrder(), ref x, ref y);
        return new(x, c.BaseField);
    }

    public static Fq<T> operator *(Fq<T> x, Fq<T> y)
    {
        return new(x.Value * y.Value, x.BaseField);
    }

    public static Fq<T> operator -(Fq<T> x, Fq<T> y)
    {
        return x + (-y);
    }

    public static Fq<T> operator /(Fq<T> x, Fq<T> y)
    {
        return x * Inv(y);
    }
}
