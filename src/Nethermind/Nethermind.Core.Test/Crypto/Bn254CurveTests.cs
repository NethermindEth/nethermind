// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Crypto.PairingCurves;
using NUnit.Framework;

namespace Nethermind.Core.Test.Crypto;

[TestFixture]
public class Bn254CurveTests
{
    [Test]
    public void Fp_div()
    {
        var p = Bn254Curve.G1.FromScalar(232323232);
        Fp<Bn254Curve.BaseField> x = Bn254Curve.Fp(p.X);
        Assert.That(x * Fp<Bn254Curve.BaseField>.Inv(x), Is.EqualTo(Bn254Curve.Fp(1)));
        Assert.That((x / Bn254Curve.Fp(2)) * Bn254Curve.Fp(2), Is.EqualTo(x));
    }

    [Test]
    public void Fp2_div()
    {
        var p = Bn254Curve.G2.FromScalar(232323232);
        Fp2<Bn254Curve.BaseField> x = Bn254Curve.Fp2(p.X.Item2, p.X.Item1);
        Fp2<Bn254Curve.BaseField> q = Bn254Curve.Fp2(p.Y.Item2, p.Y.Item1);
        Assert.That(x * Fp2<Bn254Curve.BaseField>.Inv(x), Is.EqualTo(Bn254Curve.Fp2(1)));
        Assert.That((x / q) * q, Is.EqualTo(x));
    }

    [TestCase(232323232u)]
    [TestCase(54935932u)]
    [TestCase(5738295764u)]
    [TestCase(1111u)]
    public void G2_twist(ulong x)
    {
        var p = Bn254Curve.G2.FromScalar(x);
        bool sign = Bn254Curve.Fp(p.Y.Item2) < Bn254Curve.Fp(p.Y.Item1);
        var res = Bn254Curve.G2.FromX(p.X.Item1, p.X.Item2, sign);
        Assert.That(res, Is.EqualTo(p));
    }

    [Test]
    public void G1_additive_commutativity()
    {
        var p = Bn254Curve.G1.FromScalar(232323232);
        var q = Bn254Curve.G1.FromScalar(9999999999);
        Assert.That(p + q, Is.EqualTo(q + p));
    }

    [Test]
    public void G2_additive_commutativity()
    {
        var p = Bn254Curve.G2.FromScalar(232323232);
        var q = Bn254Curve.G2.FromScalar(9999999999);
        Assert.That(p + q, Is.EqualTo(q + p));
        Assert.That(p + q, Is.Not.EqualTo(Bn254Curve.G2.Zero));
    }

    [Test]
    public void G1_additive_negation()
    {
        var p = Bn254Curve.G1.FromScalar(55555555);
        Assert.That(p + (-p), Is.EqualTo(Bn254Curve.G1.Zero));
    }

    // [Test]
    // public void G2_additive_negation()
    // {
    //     var p = Bn254Curve.G2.FromScalar(55555555);
    //     Assert.That(p + (-p), Is.EqualTo(Bn254Curve.G2.Zero));
    // }

    [Test]
    public void G1_multiply_by_scalar_zero()
    {
        var p = Bn254Curve.G1.FromScalar(666666666);
        Assert.That(0 * p, Is.EqualTo(Bn254Curve.G1.Zero));
    }

    [Test]
    public void G2_multiply_by_scalar_zero()
    {
        var p = Bn254Curve.G2.FromScalar(666666666);
        Assert.That(0 * p, Is.EqualTo(Bn254Curve.G2.Zero));
    }

    [Test]
    public void G1_multiply_by_scalar_one()
    {
        var p = Bn254Curve.G1.FromScalar(666666666);
        Assert.That(1 * p, Is.EqualTo(p));
    }

    [Test]
    public void G2_multiply_by_scalar_one()
    {
        var p = Bn254Curve.G2.FromScalar(666666666);
        Assert.That(1 * p, Is.EqualTo(p));
    }

    [Test]
    public void G1_doubling()
    {
        var p = Bn254Curve.G1.FromScalar(20572853);
        Assert.That(2 * p, Is.EqualTo(p + p));
    }

    [Test]
    public void G2_doubling()
    {
        var p = Bn254Curve.G2.FromScalar(60074914);
        Assert.That(2 * p, Is.EqualTo(p + p));
    }

    [Test]
    public void G1_subgroup_check()
    {
        var p = Bn254Curve.G1.FromScalar(10403746324);
        Assert.That(Bn254Curve.SubgroupOrder * p, Is.EqualTo(Bn254Curve.G1.Zero));
    }

    // [Test]
    // public void G2_subgroup_check()
    // {
    //     var p = Bn254Curve.G2.FromScalar(92461756);
    //     Assert.That(Bn254Curve.SubgroupOrder * p, Is.EqualTo(Bn254Curve.G2.Zero));
    // }

    // [Test]
    // public void G1_multiplication_by_unnormalised_scalar()
    // {
    //     Span<byte> s = stackalloc byte[32];
    //     Span<byte> unnormalised = stackalloc byte[32];
    //     s[30] = 0xDA;
    //     s[31] = 0xAC;
    //     Bn254Curve.SubgroupOrder.CopyTo(unnormalised);
    //     unnormalised[30] += 0xDA;
    //     unnormalised[31] += 0xAC;

    //     var p = Bn254Curve.G1.FromScalar(43333333);
    //     Assert.That(unnormalised * p, Is.EqualTo(s * p));
    // }

    // [Test]
    // public void G2_multiplication_by_unnormalised_scalar()
    // {
    //     Span<byte> s1 = stackalloc byte[32];
    //     Span<byte> s2 = stackalloc byte[32];
    //     s1[30] = 0xDA;
    //     s1[31] = 0xAC;
    //     Bn254Curve.SubgroupOrder.CopyTo(s2);
    //     s2[30] += 0xDA;
    //     s2[31] += 0xAC;

    //     var p = Bn254Curve.G2.FromScalar(43577532);
    //     Bn254Curve.G2 res = s2 * p;
    //     Bn254Curve.G2 expected = s1 * p;

    //     Assert.That(res, Is.EqualTo(expected));
    // }

    // [Test]
    // public void Pairing_degeneracy()
    // {
    //     var p = Bn254Curve.G1.FromScalar(6758363496);
    //     var q = Bn254Curve.G2.FromScalar(14863974504635);
    //     Assert.That(Bn254Curve.Pairing(p, Bn254Curve.G2.Zero));
    //     Assert.That(Bn254Curve.Pairing(Bn254Curve.G1.Zero, q));
    //     Assert.That(Bn254Curve.Pairing2(p, Bn254Curve.G2.Zero, Bn254Curve.G1.Zero, q));
    //     Assert.That(Bn254Curve.PairingsEqual(p, Bn254Curve.G2.Zero, Bn254Curve.G1.Zero, q));
    // }

    // [Test]
    // public void Pairing_bilinearity()
    // {
    //     Span<byte> s1 = stackalloc byte[32];
    //     Span<byte> s2 = stackalloc byte[32];
    //     BinaryPrimitives.WriteUInt128BigEndian(s1[16..], 35789430543857);
    //     BinaryPrimitives.WriteUInt128BigEndian(s2[16..], 60857913825);

    //     var p = Bn254Curve.G1.FromScalar(5452347823);
    //     var q = Bn254Curve.G2.FromScalar(984534538);

    //     Assert.That(Bn254Curve.PairingsEqual(s1 * p, s2 * q, s2 * (s1 * p), q));
    //     Assert.That(Bn254Curve.PairingsEqual(s1 * p, s2 * q, p, s2 * (s1 * q)));
    // }
}
