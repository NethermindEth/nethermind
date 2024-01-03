// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using NUnit.Framework;

namespace Nethermind.Core.Test.Crypto
{
    [TestFixture]
    public class BlsTests
    {
        [Test]
        public void Calculate_signature()
        {
            byte[] expected = [0x80,0x8c,0xce,0xc5,0x43,0x5a,0x63,0xae,0x01,0xe1,0x0d,0x81,0xbe,0x27,0x07,0xab,0x55,0xcd,0x0d,0xfc,0x23,0x5d,0xfd,0xf9,0xf7,0x0a,0xd3,0x27,0x99,0xe4,0x25,0x10,0xd6,0x7c,0x9f,0x61,0xd9,0x8a,0x65,0x78,0xa9,0x6a,0x76,0xcf,0x6f,0x4c,0x10,0x5d,0x09,0x26,0x2e,0xc1,0xd8,0x6b,0x06,0x51,0x53,0x60,0xb2,0x90,0xe7,0xd5,0x2d,0x34,0x7e,0x48,0x43,0x8d,0xe2,0xea,0x22,0x33,0xf3,0xc7,0x2a,0x0c,0x22,0x21,0xed,0x2d,0xa5,0xe1,0x15,0x36,0x7b,0xca,0x7a,0x27,0x12,0x16,0x50,0x32,0x34,0x0e,0x0b,0x29];
            PrivateKey sk = new("0x7b0b2bab671fabdd9308d85c3d41954cd80bbe6fafc9a66fe1e3adfbdcd10b6f");
            Hash256 message = new("0x3e00ef2f895f40d67f5bb8e81f09a5a12c840ec3ce9a7f3b181be188ef711a1e");
            Bls.Signature s = Bls.Sign(sk, message);
            s.Bytes.Should().Equal(expected);
        }

        [Test]
        public void Verify_signature()
        {
            Hash256 message = new("0x3e00ef2f895f40d67f5bb8e81f09a5a12c840ec3ce9a7f3b181be188ef711a1e");
            Bls.Signature s = Bls.Sign(TestItem.PrivateKeyA, message);
            Assert.That(Bls.Verify(Bls.GetPublicKey(TestItem.PrivateKeyA), s, message));
        }

        [Test]
        public void Rejects_bad_signature()
        {
            Hash256 message = new("0x3e00ef2f895f40d67f5bb8e81f09a5a12c840ec3ce9a7f3b181be188ef711a1e");
            Bls.Signature s = Bls.Sign(TestItem.PrivateKeyA, message);
            s.Bytes[34] += 1;
            Assert.That(!Bls.Verify(Bls.GetPublicKey(TestItem.PrivateKeyA), s, message));
        }

        [Test]
        public void G1_additive_commutativity()
        {
            var p = Bls.G1.FromScalar(232323232u);
            var q = Bls.G1.FromScalar(9999999999u);
            Bls.G1 res = p.Add(q);
            Bls.G1 expected = q.Add(p);
            res.X.Should().Equal(expected.X);
            res.Y.Should().Equal(expected.Y);
        }

        [Test]
        public void G2_additive_commutativity()
        {
            var p = Bls.G2.FromScalar(232323232u);
            var q = Bls.G2.FromScalar(9999999999u);
            Bls.G2 res = p.Add(q);
            Bls.G2 expected = q.Add(p);
            res.X.Item1.Should().Equal(expected.X.Item1);
            res.X.Item2.Should().Equal(expected.X.Item2);
            res.Y.Item1.Should().Equal(expected.Y.Item1);
            res.Y.Item2.Should().Equal(expected.Y.Item2);
        }

        [Test]
        public void G1_additive_negation()
        {
            var p = Bls.G1.FromScalar(55555555u);
            Bls.G1 res = p.Add(p.Negate());
            res.X.Should().Equal(Bls.G1.Zero.X);
            res.Y.Should().Equal(Bls.G1.Zero.Y);
        }

        [Test]
        public void G2_additive_negation()
        {
            var p = Bls.G2.FromScalar(55555555u);
            Bls.G2 res = p.Add(p.Negate());
            res.X.Item1.Should().Equal(Bls.G2.Zero.X.Item1);
            res.X.Item2.Should().Equal(Bls.G2.Zero.X.Item2);
            res.Y.Item1.Should().Equal(Bls.G2.Zero.Y.Item1);
            res.Y.Item2.Should().Equal(Bls.G2.Zero.Y.Item2);
        }

        [Test]
        public void G1_multiply_by_scalar_zero()
        {
            Span<byte> s = stackalloc byte[32];
            Bls.G1 p = Bls.G1.Generator.Multiply(s);
            p.X.Should().Equal(Bls.G1.Zero.X);
            p.Y.Should().Equal(Bls.G1.Zero.Y);
        }

        [Test]
        public void G2_multiply_by_scalar_zero()
        {
            Span<byte> s = stackalloc byte[32];
            Bls.G2 p = Bls.G2.Generator.Multiply(s);
            p.X.Item1.Should().Equal(Bls.G2.Zero.X.Item1);
            p.X.Item2.Should().Equal(Bls.G2.Zero.X.Item2);
            p.Y.Item1.Should().Equal(Bls.G2.Zero.Y.Item1);
            p.Y.Item2.Should().Equal(Bls.G2.Zero.Y.Item2);
        }

        [Test]
        public void G1_multiply_by_scalar_one()
        {
            Span<byte> s = stackalloc byte[32];
            s[31] = 1;
            Bls.G1 p = Bls.G1.Generator.Multiply(s);
            p.X.Should().Equal(Bls.G1.Generator.X);
            p.Y.Should().Equal(Bls.G1.Generator.Y);
        }

        [Test]
        public void G2_multiply_by_scalar_one()
        {
            Span<byte> s = stackalloc byte[32];
            s[31] = 1;
            Bls.G2 p = Bls.G2.Generator.Multiply(s);
            p.X.Item1.Should().Equal(Bls.G2.Generator.X.Item1);
            p.X.Item2.Should().Equal(Bls.G2.Generator.X.Item2);
            p.Y.Item1.Should().Equal(Bls.G2.Generator.Y.Item1);
            p.Y.Item2.Should().Equal(Bls.G2.Generator.Y.Item2);
        }

        [Test]
        public void G1_doubling()
        {
            Span<byte> s = stackalloc byte[32];
            s[31] = 2;

            var p = Bls.G1.FromScalar(20572853u);
            Bls.G1 doubled = p.Multiply(s);
            Bls.G1 expected = p.Add(p);

            doubled.X.Should().Equal(expected.X);
            doubled.Y.Should().Equal(expected.Y);
        }

        [Test]
        public void G2_doubling()
        {
            Span<byte> s = stackalloc byte[32];
            s[31] = 2;

            var p = Bls.G2.FromScalar(60074914u);
            Bls.G2 doubled = p.Multiply(s);
            Bls.G2 expected = p.Add(p);

            doubled.X.Item1.Should().Equal(expected.X.Item1);
            doubled.X.Item2.Should().Equal(expected.X.Item2);
            doubled.Y.Item1.Should().Equal(expected.Y.Item1);
            doubled.Y.Item2.Should().Equal(expected.Y.Item2);
        }

        [Test]
        public void G1_subgroup_check()
        {
            var p = Bls.G1.FromScalar(10403746324u);
            Bls.G1 res = p.Multiply(Bls.SubgroupOrder);

            res.X.Should().Equal(Bls.G1.Zero.X);
            res.Y.Should().Equal(Bls.G1.Zero.Y);
        }

        [Test]
        public void G2_subgroup_check()
        {
            var p = Bls.G2.FromScalar(92461756u);
            Bls.G2 res = p.Multiply(Bls.SubgroupOrder);

            res.X.Item1.Should().Equal(Bls.G2.Zero.X.Item1);
            res.X.Item2.Should().Equal(Bls.G2.Zero.X.Item2);
            res.Y.Item1.Should().Equal(Bls.G2.Zero.Y.Item1);
            res.Y.Item2.Should().Equal(Bls.G2.Zero.Y.Item2);
        }

        [Test]
        public void G1_multiplication_by_unnormalised_scalar()
        {
            Span<byte> s1 = stackalloc byte[32];
            Span<byte> s2 = stackalloc byte[32];
            s1[30] = 0xDA;
            s1[31] = 0xAC;
            Bls.SubgroupOrder.CopyTo(s2);
            s2[30] += 0xDA;
            s2[31] += 0xAC;

            var p = Bls.G1.FromScalar(43333333u);
            Bls.G1 res = p.Multiply(s2);
            Bls.G1 expected = p.Multiply(s1);

            res.X.Should().Equal(expected.X);
            res.Y.Should().Equal(expected.Y);
        }

        [Test]
        public void G2_multiplication_by_unnormalised_scalar()
        {
            Span<byte> s1 = stackalloc byte[32];
            Span<byte> s2 = stackalloc byte[32];
            s1[30] = 0xDA;
            s1[31] = 0xAC;
            Bls.SubgroupOrder.CopyTo(s2);
            s2[30] += 0xDA;
            s2[31] += 0xAC;

            var p = Bls.G2.FromScalar(43577532u);
            Bls.G2 res = p.Multiply(s2);
            Bls.G2 expected = p.Multiply(s1);

            res.X.Item1.Should().Equal(expected.X.Item1);
            res.X.Item2.Should().Equal(expected.X.Item2);
            res.Y.Item1.Should().Equal(expected.Y.Item1);
            res.Y.Item2.Should().Equal(expected.Y.Item2);
        }

        [Test]
        public void Pairing_degeneracy()
        {
            var p = Bls.G1.FromScalar(6758363496u);
            var q = Bls.G2.FromScalar(14863974504635u);
            Assert.That(Bls.Pairing(p, Bls.G2.Zero));
            Assert.That(Bls.Pairing(Bls.G1.Zero, q));
            Assert.That(Bls.Pairing2(p, Bls.G2.Zero, Bls.G1.Zero, q));
            Assert.That(Bls.PairingsEqual(p, Bls.G2.Zero, Bls.G1.Zero, q));
        }

        [Test]
        public void Pairing_bilinearity()
        {
            Span<byte> s1 = stackalloc byte[32];
            Span<byte> s2 = stackalloc byte[32];
            BinaryPrimitives.WriteUInt128BigEndian(s1[16..], 35789430543857u);
            BinaryPrimitives.WriteUInt128BigEndian(s2[16..], 60857913825u);

            var p = Bls.G1.FromScalar(5452347823u);
            var q = Bls.G2.FromScalar(984534538u);

            Assert.That(Bls.PairingsEqual(p.Multiply(s1), q.Multiply(s2), p.Multiply(s1).Multiply(s2), q));
            Assert.That(Bls.PairingsEqual(p.Multiply(s1), q.Multiply(s2), p, q.Multiply(s1).Multiply(s2)));
        }
    }
}
