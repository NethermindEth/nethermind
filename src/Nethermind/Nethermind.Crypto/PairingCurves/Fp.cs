// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using Nethermind.Core.Extensions;

namespace Nethermind.Crypto.PairingCurves;

public class Fp<T> where T : IBaseField
{
    internal BigInteger _value;
    private readonly T _baseField;

    public Fp(ReadOnlySpan<byte> bytes, T baseField)
    {
        if (bytes.Length != baseField.GetSize())
        {
            throw new Exception("Field point must have " + baseField.GetSize() + " bytes");
        }
        _value = Normalise(new BigInteger(bytes, true, true), baseField.GetOrder());
        _baseField = baseField;
    }

    public Fp(BigInteger value, T baseField)
    {
        _value = Normalise(value, baseField.GetOrder());
        _baseField = baseField;
    }

    private static BigInteger Normalise(BigInteger x, BigInteger baseFieldOrder)
    {
        BigInteger unnormalised = x % baseFieldOrder;
        return (unnormalised.Sign == 1) ? unnormalised : (baseFieldOrder + unnormalised);
    }

    public byte[] ToBytes()
    {
        return _value.ToBigEndianByteArray(48);
    }

    public static (Fp<T>, Fp<T>) Sqrt(Fp<T> x)
    {
        Fp<T> res = x ^ (new Fp<T>(new BigInteger(1), x._baseField) / new Fp<T>(new BigInteger(4), x._baseField));
        return (res, -res);
    }

    public static Fp<T> operator -(Fp<T> x)
    {
        return new(x._baseField.GetOrder() - x._value, x._baseField);
    }

    public static Fp<T> operator ^(Fp<T> x, Fp<T> exp)
    {
        return new(BigInteger.ModPow(x._value, exp._value, x._baseField.GetOrder()), x._baseField);
    }

    public static Fp<T> operator +(Fp<T> x, Fp<T> y)
    {
        return new(x._value + y._value, x._baseField);
    }

    public static bool operator <(Fp<T> x, Fp<T> y)
    {
        return x._value < y._value;
    }

    public static bool operator >(Fp<T> x, Fp<T> y)
    {
        return x._value > y._value;
    }
    public override bool Equals(object obj) => Equals(obj as Fp<T>);

    public bool Equals(Fp<T> p)
    {
        return _value == p._value;
    }
    public override int GetHashCode() => _value.GetHashCode();

    public static bool operator ==(Fp<T> x, Fp<T> y)
    {
        return x._value == y._value;
    }

    public static bool operator !=(Fp<T> x, Fp<T> y) => !x.Equals(y);

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

    public static Fp<T> Inv(Fp<T> c)
    {
        BigInteger x = 1;
        BigInteger y = 1;
        gcdExtended(c._value, c._baseField.GetOrder(), ref x, ref y);
        return new(x, c._baseField);
    }

    public static Fp<T> operator *(Fp<T> x, Fp<T> y)
    {
        return new(x._value * y._value, x._baseField);
    }

    public static Fp<T> operator -(Fp<T> x, Fp<T> y)
    {
        return x + (-y);
    }

    public static Fp<T> operator /(Fp<T> x, Fp<T> y)
    {
        return x * Inv(y);
    }
}
