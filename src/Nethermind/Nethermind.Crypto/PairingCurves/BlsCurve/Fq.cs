// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;

namespace Nethermind.Crypto.PairingCurves;

public partial class BlsCurve
{
    public static Fq<BaseField> Fq(ReadOnlySpan<byte> bytes)
    {
        return new(bytes, BaseField.Instance);
    }

    public static Fq<BaseField> Fq(BigInteger x)
    {
        return new(x, BaseField.Instance);
    }

    public static Fq2<BaseField> Fq2(ReadOnlySpan<byte> X0, ReadOnlySpan<byte> X1)
    {
        return new(Fq(X0), Fq(X1), BaseField.Instance);
    }

    public static Fq2<BaseField> Fq2(Fq<BaseField> a, Fq<BaseField> b)
    {
        return new(a, b, BaseField.Instance);
    }

    public static Fq2<BaseField> Fq2(Fq<BaseField> b)
    {
        return Fq2<BaseField>.FromFq(b);
    }

    public static Fq2<BaseField> Fq2(BigInteger b)
    {
        return Fq2<BaseField>.FromFq(Fq(b));
    }

    public static Fq6<BaseField> Fq6(Fq2<BaseField> a, Fq2<BaseField> b, Fq2<BaseField> c)
    {
        return new(a, b, c, BaseField.Instance);
    }

    public static Fq6<BaseField> Fq6(BigInteger b)
    {
        return Fq6<BaseField>.FromFq(Fq(b));
    }

    public static Fq6<BaseField> Fq6(Fq<BaseField> b)
    {
        return Fq6<BaseField>.FromFq(b);
    }

    public static Fq12<BaseField> Fq12(Fq6<BaseField> a, Fq6<BaseField> b)
    {
        return new(a, b, BaseField.Instance);
    }

    public static Fq12<BaseField> Fq12(BigInteger b)
    {
        return Fq12<BaseField>.FromFq(Fq(b));
    }

    public static Fq12<BaseField> Fq12(Fq<BaseField> b)
    {
        return Fq12<BaseField>.FromFq(b);
    }

}
