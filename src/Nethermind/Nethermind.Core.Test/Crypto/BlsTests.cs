// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Crypto.PairingCurves;
using NUnit.Framework;

namespace Nethermind.Core.Test.Crypto;

using G1 = BlsCurve.G1;
using G2 = BlsCurve.G2;
using GT = BlsCurve.GT;

[TestFixture]
public class BlsTests
{
    private Bls.PrivateKey sk;

    [SetUp]
    public void Setup()
    {
        sk.Bytes = [0x2c, 0xd4, 0xba, 0x40, 0x6b, 0x52, 0x24, 0x59, 0xd5, 0x7a, 0x0b, 0xed, 0x51, 0xa3, 0x97, 0x43, 0x5c, 0x0b, 0xb1, 0x1d, 0xd5, 0xf3, 0xca, 0x11, 0x52, 0xb3, 0x69, 0x4b, 0xb9, 0x1d, 0x7c, 0x22];
    }

    [Test]
    public void Calculate_signature()
    {
        byte[] expected = [0x8e, 0x02, 0xb7, 0x95, 0x01, 0x98, 0xd3, 0x35, 0xc7, 0xb3, 0x52, 0xd1, 0x88, 0x80, 0xe2, 0xf6, 0xb4, 0xe7, 0xf6, 0x78, 0x02, 0x98, 0x87, 0x2b, 0x67, 0x84, 0x0d, 0xb1, 0xfa, 0xa0, 0x69, 0xf9, 0xa8, 0xbe, 0x48, 0x80, 0x0c, 0xe2, 0xee, 0x55, 0x65, 0xa8, 0x11, 0xd8, 0x23, 0x0d, 0x3f, 0x05];
        byte[] message = [0x50, 0x32, 0xec, 0x38, 0xbb, 0xc5, 0xda, 0x98, 0xee, 0x0c, 0x6f, 0x56, 0x8b, 0x87, 0x2a, 0x65, 0xa0, 0x8a, 0xbf, 0x25, 0x1d, 0xeb, 0x21, 0xbb, 0x4b, 0x56, 0xe5, 0xd8, 0x82, 0x1e, 0x68, 0xaa];
        Bls.Signature s = Bls.Sign(sk, message);
        s.Bytes.Should().Equal(expected);
    }

    [Test]
    public void Verify_signature()
    {
        byte[] message = [0x3e, 0x00, 0xef, 0x2f, 0x89, 0x5f, 0x40, 0xd6, 0x7f, 0x5b, 0xb8, 0xe8, 0x1f, 0x09, 0xa5, 0xa1, 0x2c, 0x84, 0x0e, 0xc3, 0xce, 0x9a, 0x7f, 0x3b, 0x18, 0x1b, 0xe1, 0x88, 0xef, 0x71, 0x1a, 0x1e];
        Bls.Signature s = Bls.Sign(sk, message);
        Assert.That(Bls.Verify(Bls.GetPublicKey(sk), s, message));
    }

    [Test]
    public void Rejects_bad_signature()
    {
        byte[] message = [0x3e, 0x00, 0xef, 0x2f, 0x89, 0x5f, 0x40, 0xd6, 0x7f, 0x5b, 0xb8, 0xe8, 0x1f, 0x09, 0xa5, 0xa1, 0x2c, 0x84, 0x0e, 0xc3, 0xce, 0x9a, 0x7f, 0x3b, 0x18, 0x1b, 0xe1, 0x88, 0xef, 0x71, 0x1a, 0x1e];
        Bls.Signature s = Bls.Sign(sk, message);
        s.Bytes[34] += 1;
        Assert.That(!Bls.Verify(Bls.GetPublicKey(sk), s, message));
    }

    [Test]
    public void Public_key_from_private_key()
    {
        byte[] expected = [0xb4, 0x95, 0x3c, 0x4b, 0xa1, 0x0c, 0x4d, 0x41, 0x96, 0xf9, 0x01, 0x69, 0xe7, 0x6f, 0xaf, 0x15, 0x4c, 0x26, 0x0e, 0xd7, 0x3f, 0xc7, 0x7b, 0xb6, 0x5d, 0xc3, 0xbe, 0x31, 0xe0, 0xce, 0xc6, 0x14, 0xa7, 0x28, 0x7c, 0xda, 0x94, 0x19, 0x53, 0x43, 0x67, 0x6c, 0x2c, 0x57, 0x49, 0x4f, 0x0e, 0x65, 0x15, 0x27, 0xe6, 0x50, 0x4c, 0x98, 0x40, 0x8e, 0x59, 0x9a, 0x4e, 0xb9, 0x6f, 0x7c, 0x5a, 0x8c, 0xfb, 0x85, 0xd2, 0xfd, 0xc7, 0x72, 0xf2, 0x85, 0x04, 0x58, 0x00, 0x84, 0xef, 0x55, 0x9b, 0x9b, 0x62, 0x3b, 0xc8, 0x4c, 0xe3, 0x05, 0x62, 0xed, 0x32, 0x0f, 0x6b, 0x7f, 0x65, 0x24, 0x5a, 0xd4];
        Assert.That(Bls.GetPublicKey(sk).Bytes, Is.EqualTo(expected));
    }

    [Test]
    public void Can_expand_msg()
    {
        // Test vector from
        // https://datatracker.ietf.org/doc/html/rfc9380#appendix-J.9.1
        byte[] expected = [0xef, 0x90, 0x4a, 0x29, 0xbf, 0xfc, 0x4c, 0xf9, 0xee, 0x82, 0x83, 0x24, 0x51, 0xc9, 0x46, 0xac, 0x3c, 0x8f, 0x80, 0x58, 0xae, 0x97, 0xd8, 0xd6, 0x29, 0x83, 0x1a, 0x74, 0xc6, 0x57, 0x2b, 0xd9, 0xeb, 0xd0, 0xdf, 0x63, 0x5c, 0xd1, 0xf2, 0x08, 0xe2, 0x03, 0x8e, 0x76, 0x0c, 0x49, 0x94, 0x98, 0x4c, 0xe7, 0x3f, 0x0d, 0x55, 0xea, 0x9f, 0x22, 0xaf, 0x83, 0xba, 0x47, 0x34, 0x56, 0x9d, 0x4b, 0xc9, 0x5e, 0x18, 0x35, 0x0f, 0x74, 0x0c, 0x07, 0xee, 0xf6, 0x53, 0xcb, 0xb9, 0xf8, 0x79, 0x10, 0xd8, 0x33, 0x75, 0x18, 0x25, 0xf0, 0xeb, 0xef, 0xa1, 0xab, 0xe5, 0x42, 0x0b, 0xb5, 0x2b, 0xe1, 0x4c, 0xf4, 0x89, 0xb3, 0x7f, 0xe1, 0xa7, 0x2f, 0x7d, 0xe2, 0xd1, 0x0b, 0xe4, 0x53, 0xb2, 0xc9, 0xd9, 0xeb, 0x20, 0xc7, 0xe3, 0xf6, 0xed, 0xc5, 0xa6, 0x06, 0x29, 0x17, 0x8d, 0x94, 0x78, 0xdf];
        byte[] msg = ASCIIEncoding.ASCII.GetBytes("abcdef0123456789");
        byte[] dst = ASCIIEncoding.ASCII.GetBytes("QUUX-V01-CS02-with-expander-SHA256-128");
        byte[] res = new byte[128];
        Bls.ExpandMessageXmd(msg, dst, 0x80, res);
        Assert.That(res, Is.EqualTo(expected));
    }

    [Test]
    public void Can_hash_to_curve()
    {
        // Test vector from
        // https://datatracker.ietf.org/doc/html/rfc9380#appendix-K.1
        G1 expected = new(
            [0x11, 0xe0, 0xb0, 0x79, 0xde, 0xa2, 0x9a, 0x68, 0xf0, 0x38, 0x3e, 0xe9, 0x4f, 0xed, 0x1b, 0x94, 0x09, 0x95, 0x27, 0x24, 0x07, 0xe3, 0xbb, 0x91, 0x6b, 0xbf, 0x26, 0x8c, 0x26, 0x3d, 0xdd, 0x57, 0xa6, 0xa2, 0x72, 0x00, 0xa7, 0x84, 0xcb, 0xc2, 0x48, 0xe8, 0x4f, 0x35, 0x7c, 0xe8, 0x2d, 0x98],
            [0x03, 0xa8, 0x7a, 0xe2, 0xca, 0xf1, 0x4e, 0x8e, 0xe5, 0x2e, 0x51, 0xfa, 0x2e, 0xd8, 0xee, 0xfe, 0x80, 0xf0, 0x24, 0x57, 0x00, 0x4b, 0xa4, 0xd4, 0x86, 0xd6, 0xaa, 0x1f, 0x51, 0x7c, 0x08, 0x89, 0x50, 0x1d, 0xc7, 0x41, 0x37, 0x53, 0xf9, 0x59, 0x9b, 0x09, 0x9e, 0xbc, 0xbb, 0xd2, 0xd7, 0x09]
        );
        byte[] msg = ASCIIEncoding.ASCII.GetBytes("abcdef0123456789");
        byte[] dst = ASCIIEncoding.ASCII.GetBytes("QUUX-V01-CS02-with-BLS12381G1_XMD:SHA-256_SSWU_RO_");
        var res = Bls.HashToCurve(msg, dst);
        Assert.That(res, Is.EqualTo(expected));
    }

    [TestCase(232323232u)]
    [TestCase(11111111111u)]
    public void G1_from_signature(ulong s)
    {
        var p = G1.FromScalar(s);
        Assert.That(Bls.FromSignature(Bls.ToSignature(p)), Is.EqualTo(p));
    }

    [TestCase(232323232u)]
    [TestCase(11111111111u)]
    public void G2_from_public_key(ulong s)
    {
        var p = G2.FromScalar(s);
        Assert.That(Bls.FromPublicKey(Bls.ToPublicKey(p)), Is.EqualTo(p));
    }

    [TestCase(232323232u)]
    [TestCase(54935932u)]
    [TestCase(5738295764u)]
    [TestCase(1111u)]
    public void G2_from_x(ulong x)
    {
        var p = G2.FromScalar(x);
        bool sign = BlsCurve.Fq(p.Y.Item1) < BlsCurve.Fq(p.Y.Item2);
        var res = G2.FromX(p.X.Item1, p.X.Item2, sign);
        Assert.That(res, Is.EqualTo(p));
    }

    [Test]
    public void G1_additive_commutativity()
    {
        var p = G1.FromScalar(232323232);
        var q = G1.FromScalar(9999999999);
        Assert.That(p + q, Is.EqualTo(q + p));
    }

    [Test]
    public void G2_additive_commutativity()
    {
        var p = G2.FromScalar(232323232);
        var q = G2.FromScalar(9999999999);
        Assert.That(p + q, Is.EqualTo(q + p));
    }

    [Test]
    public void G1_additive_negation()
    {
        var p = G1.FromScalar(55555555);
        Assert.That(p + (-p), Is.EqualTo(G1.Zero));
    }

    [Test]
    public void G2_additive_negation()
    {
        var p = G2.FromScalar(55555555);
        Assert.That(p + (-p), Is.EqualTo(G2.Zero));
    }

    [Test]
    public void G1_multiply_by_scalar_zero()
    {
        var p = G1.FromScalar(666666666);
        Assert.That(0 * p, Is.EqualTo(G1.Zero));
    }

    [Test]
    public void G2_multiply_by_scalar_zero()
    {
        var p = G2.FromScalar(666666666);
        Assert.That(0 * p, Is.EqualTo(G2.Zero));
    }

    [Test]
    public void G1_multiply_by_scalar_one()
    {
        var p = G1.FromScalar(666666666);
        Assert.That(1 * p, Is.EqualTo(p));
    }

    [Test]
    public void G2_multiply_by_scalar_one()
    {
        var p = G2.FromScalar(666666666);
        Assert.That(1 * p, Is.EqualTo(p));
    }

    [Test]
    public void G1_doubling()
    {
        var p = G1.FromScalar(20572853);
        Assert.That(2 * p, Is.EqualTo(p + p));
    }

    [Test]
    public void G2_doubling()
    {
        var p = G2.FromScalar(60074914);
        Assert.That(2 * p, Is.EqualTo(p + p));
    }

    [Test]
    public void G1_subgroup_check()
    {
        var p = G1.FromScalar(10403746324);
        Assert.That(p.IsInSubgroup());
    }

    [Test]
    public void G2_subgroup_check()
    {
        var p = G2.FromScalar(92461756);
        Assert.That(p.IsInSubgroup());
    }

    [Test]
    public void G1_multiplication_by_unnormalised_scalar()
    {
        Span<byte> s = stackalloc byte[32];
        Span<byte> unnormalised = stackalloc byte[32];
        s[30] = 0xDA;
        s[31] = 0xAC;
        BlsCurve.SubgroupOrder.ToBigEndianByteArray(32).CopyTo(unnormalised);
        unnormalised[30] += 0xDA;
        unnormalised[31] += 0xAC;

        var p = G1.FromScalar(43333333);
        Assert.That(unnormalised * p, Is.EqualTo(s * p));
    }

    [Test]
    public void G2_multiplication_by_unnormalised_scalar()
    {
        Span<byte> s1 = stackalloc byte[32];
        Span<byte> s2 = stackalloc byte[32];
        s1[30] = 0xDA;
        s1[31] = 0xAC;
        BlsCurve.SubgroupOrder.ToBigEndianByteArray(32).CopyTo(s2);
        s2[30] += 0xDA;
        s2[31] += 0xAC;

        var p = G2.FromScalar(43577532);
        G2 res = s2 * p;
        G2 expected = s1 * p;

        Assert.That(res, Is.EqualTo(expected));
    }

    [Test]
    public void Pairing_degeneracy()
    {
        var p = G1.FromScalar(6758363496);
        var q = G2.FromScalar(14863974504635);
        Assert.That(BlsCurve.PairingVerify(p, G2.Zero));
        Assert.That(BlsCurve.PairingVerify(G1.Zero, q));
        Assert.That(BlsCurve.PairingVerify2(p, G2.Zero, G1.Zero, q));
        Assert.That(BlsCurve.PairingsEqual(p, G2.Zero, G1.Zero, q));
    }

    [Test]
    public void Pairing_bilinearity()
    {
        Span<byte> s1 = stackalloc byte[32];
        Span<byte> s2 = stackalloc byte[32];
        BinaryPrimitives.WriteUInt128BigEndian(s1[16..], 35789430543857);
        BinaryPrimitives.WriteUInt128BigEndian(s2[16..], 60857913825);

        var p = G1.FromScalar(5452347823);
        var q = G2.FromScalar(984534538);

        Assert.That(BlsCurve.PairingsEqual(s1 * p, s2 * q, s2 * (s1 * p), q));
        Assert.That(BlsCurve.PairingsEqual(s1 * p, s2 * q, p, s2 * (s1 * q)));

        GT r1 = BlsCurve.Pairing(s1 * p, s2 * q);
        GT r2 = BlsCurve.Pairing(s2 * (s1 * p), q);
        Assert.That(r1.X! * r2.X!, Is.EqualTo(BlsCurve.Fq12(1)));
    }

    [Test]
    public void PairingGT()
    {
        //@pairing((12+34)*56*g1, 78*g2) == pairing(78*g1, 12*56*g2) * pairing(78*g1, 34*56*g2)@
        GT r1 = BlsCurve.Pairing(G1.FromScalar((12 + 34) * 56), G2.FromScalar(78));
        GT r2 = BlsCurve.Pairing(G1.FromScalar(78), G2.FromScalar(12 * 56));
        GT r3 = BlsCurve.Pairing(G1.FromScalar(78), G2.FromScalar(34 * 56));
        Assert.That(r2.X! * r3.X!, Is.EqualTo(r1.X!));
    }
}
