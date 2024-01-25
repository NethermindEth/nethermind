// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Crypto.PairingCurves;
using NUnit.Framework;

namespace Nethermind.Core.Test.Crypto;

using F = BlsCurve.BaseField;

[TestFixture]
public class FqTests
{
    private Fq2<F> a = BlsCurve.Fq2(BlsCurve.Fq(473842924384), BlsCurve.Fq(43234267524));
    private Fq2<F> b = BlsCurve.Fq2(BlsCurve.Fq(222), BlsCurve.Fq(64637));
    private Fq2<F> c = BlsCurve.Fq2(BlsCurve.Fq(43334335), BlsCurve.Fq(2543251436));
    private Fq2<F> d = BlsCurve.Fq2(BlsCurve.Fq(111111), BlsCurve.Fq(9865343));
    private Fq2<F> e = BlsCurve.Fq2(BlsCurve.Fq(7676456546), BlsCurve.Fq(11123233245));
    private Fq2<F> f = BlsCurve.Fq2(BlsCurve.Fq(433455), BlsCurve.Fq(222222));

    [Test]
    public void Fq_div()
    {
        Fq<F> x = BlsCurve.Fq(473842924384);
        Assert.That(x * Fq<F>.Inv(x), Is.EqualTo(BlsCurve.Fq(1)));
        Assert.That((x / BlsCurve.Fq(34372)) * BlsCurve.Fq(34372), Is.EqualTo(x));
    }

    [Test]
    public void Fq2_div()
    {
        Fq2<F> x = BlsCurve.Fq2(BlsCurve.Fq(473842924384), BlsCurve.Fq(43234267524));
        Fq2<F> q = BlsCurve.Fq2(BlsCurve.Fq(900001111), BlsCurve.Fq(2333232));
        Assert.That(x * Fq2<F>.Inv(x), Is.EqualTo(BlsCurve.Fq2(1)));
        Assert.That((x / q) * q, Is.EqualTo(x));
    }

    [Test]
    public void Fq6_div()
    {
        Fq6<F> x = BlsCurve.Fq6(a, b, c);
        Fq6<F> q = BlsCurve.Fq6(d, e, f);
        Assert.That(x * Fq6<F>.Inv(x), Is.EqualTo(BlsCurve.Fq6(1)));
        Assert.That((x / q) * q, Is.EqualTo(x));
    }

    [Test]
    public void Fq12_div()
    {
        Fq12<F> x = BlsCurve.Fq12(BlsCurve.Fq6(a, b, c), BlsCurve.Fq6(d, e, f));
        Fq12<F> q = BlsCurve.Fq12(BlsCurve.Fq6(d * c, a * e, b), BlsCurve.Fq6(f, d * f, c));
        Assert.That(x * Fq12<F>.Inv(x), Is.EqualTo(BlsCurve.Fq12(1)));
        Assert.That((x / q) * q, Is.EqualTo(x));
    }

    [Test]
    public void Fq12_mul()
    {
        Fq12<F> x = BlsCurve.Fq12(BlsCurve.Fq6(a, b, c), BlsCurve.Fq6(d, e, f));

        Fq12<F> acc = BlsCurve.Fq12(0);
        for (int i = 0; i < 1000; i++)
        {
            acc += x;
        }

        Assert.That(BlsCurve.Fq12(1000) * x, Is.EqualTo(acc));
    }

    [Test]
    public void Fq12_exp()
    {
        Fq12<F> x = BlsCurve.Fq12(BlsCurve.Fq6(a, b, c), BlsCurve.Fq6(d, e, f));

        Fq12<F> acc = BlsCurve.Fq12(1);
        for (int i = 0; i < 1000; i++)
        {
            acc *= x;
        }

        Assert.That(x ^ BlsCurve.Fq(1000), Is.EqualTo(acc));
    }
}
