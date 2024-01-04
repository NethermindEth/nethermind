// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using NUnit.Framework;

namespace Nethermind.Core.Test.Crypto
{
    [TestFixture]
    public class BlsTests
    {
        // test vectors not currently passsing
        [Test]
        public void Calculate_signature()
        {
            // sig format G1.X|| G1.Y is wrong should be G.X but first 48 bytes should match
            byte[] expected = [0xa9,0xd4,0xde,0x7b,0x0b,0x28,0x05,0xfe,0x52,0xbc,0xcb,0x86,0x41,0x5e,0xf7,0xb8,0xff,0xec,0xb3,0x13,0xc3,0xc2,0x54,0x04,0x4d,0xfc,0x1b,0xdc,0x53,0x1d,0x3e,0xae,0x99,0x9d,0x87,0x71,0x78,0x22,0xa0,0x52,0x69,0x21,0x40,0x77,0x4b,0xd7,0x24,0x5c];
            PrivateKey sk = new("0x2cd4ba406b522459d57a0bed51a397435c0bb11dd5f3ca1152b3694bb91d7c22");
            byte[] message = [0x50,0x32,0xec,0x38,0xbb,0xc5,0xda,0x98,0xee,0x0c,0x6f,0x56,0x8b,0x87,0x2a,0x65,0xa0,0x8a,0xbf,0x25,0x1d,0xeb,0x21,0xbb,0x4b,0x56,0xe5,0xd8,0x82,0x1e,0x68,0xaa];
            Bls.Signature s = Bls.Sign(sk, message);
            s.Bytes.Should().Equal(expected);
        }

        [Test]
        public void Calculate_signature_2()
        {
            byte[] expected = [0x90,0x9a,0xe4,0xdf,0xed,0xb4,0x02,0xa0,0x5f,0x2f,0x3e,0xf6,0x9a,0x71,0x5f,0x74,0x25,0x3e,0xbe,0x29,0xa9,0x58,0x54,0x03,0x46,0x1d,0xc4,0x36,0xa3,0xad,0x6a,0x18,0x6f,0x7a,0x74,0x2c,0x21,0x16,0xcb,0x25,0x1b,0x48,0x27,0x19,0x8c,0x5f,0xb3,0xdb,0x11,0x33,0x75,0xaf,0xc2,0x51,0x9a,0x1c,0xfe,0x8a,0x3b,0x34,0xb4,0xbd,0x41,0xc0,0x3d,0x22,0xfe,0xfc,0xc8,0x98,0xd5,0x03,0xce,0x1f,0x62,0xbd,0x90,0x8c,0x55,0x4b,0x80,0xe5,0x7c,0x55,0x5b,0xf0,0x24,0xd5,0xd2,0xf3,0x87,0x2d,0xf4,0x71,0x49,0x43];
            PrivateKey sk = new("0x33d63cb7844cbfad698d2ebcfe32fb2ddfe2513f74a8269e8180711100cfa08b");
            byte[] message = ASCIIEncoding.ASCII.GetBytes("hello");
            Bls.Signature s = Bls.Sign(sk, message);
            s.Bytes.Should().Equal(expected);
        }

        [Test]
        public void Verify_signature()
        {
            byte[] message = [0x3e,0x00,0xef,0x2f,0x89,0x5f,0x40,0xd6,0x7f,0x5b,0xb8,0xe8,0x1f,0x09,0xa5,0xa1,0x2c,0x84,0x0e,0xc3,0xce,0x9a,0x7f,0x3b,0x18,0x1b,0xe1,0x88,0xef,0x71,0x1a,0x1e];
            Bls.Signature s = Bls.Sign(TestItem.PrivateKeyA, message);
            Assert.That(Bls.Verify(Bls.GetPublicKey(TestItem.PrivateKeyA), s, message));
        }

        [Test]
        public void Rejects_bad_signature()
        {
            byte[] message = [0x3e,0x00,0xef,0x2f,0x89,0x5f,0x40,0xd6,0x7f,0x5b,0xb8,0xe8,0x1f,0x09,0xa5,0xa1,0x2c,0x84,0x0e,0xc3,0xce,0x9a,0x7f,0x3b,0x18,0x1b,0xe1,0x88,0xef,0x71,0x1a,0x1e];
            Bls.Signature s = Bls.Sign(TestItem.PrivateKeyA, message);
            s.Bytes[34] += 1;
            Assert.That(!Bls.Verify(Bls.GetPublicKey(TestItem.PrivateKeyA), s, message));
        }

        [Test]
        public void G1_additive_commutativity()
        {
            var p = Bls.G1.FromScalar(232323232);
            var q = Bls.G1.FromScalar(9999999999);
            Bls.G1 res = p + q;
            Bls.G1 expected = q + p;
            res.X.Should().Equal(expected.X);
            res.Y.Should().Equal(expected.Y);
        }

        [Test]
        public void G2_additive_commutativity()
        {
            var p = Bls.G2.FromScalar(232323232);
            var q = Bls.G2.FromScalar(9999999999);
            Assert.That(p + q, Is.EqualTo(q + p));
        }

        [Test]
        public void G1_additive_negation()
        {
            var p = Bls.G1.FromScalar(55555555);
            Assert.That(p + (-p), Is.EqualTo(Bls.G1.Zero));
        }

        [Test]
        public void G2_additive_negation()
        {
            var p = Bls.G2.FromScalar(55555555);
            Assert.That(p + (-p), Is.EqualTo(Bls.G2.Zero));
        }

        [Test]
        public void G1_multiply_by_scalar_zero()
        {
            var p = Bls.G1.FromScalar(666666666);
            Assert.That(0 * p, Is.EqualTo(p));
        }

        [Test]
        public void G2_multiply_by_scalar_zero()
        {
            var p = Bls.G2.FromScalar(666666666);
            Assert.That(0 * p, Is.EqualTo(p));
        }

        [Test]
        public void G1_multiply_by_scalar_one()
        {
            var p = Bls.G1.FromScalar(666666666);
            Assert.That(1 * p, Is.EqualTo(p));
        }

        [Test]
        public void G2_multiply_by_scalar_one()
        {
            var p = Bls.G2.FromScalar(666666666);
            Assert.That(1 * p, Is.EqualTo(p));
        }

        [Test]
        public void G1_doubling()
        {
            var p = Bls.G1.FromScalar(20572853);
            Assert.That(2 * p, Is.EqualTo(p + p));
        }

        [Test]
        public void G2_doubling()
        {
            var p = Bls.G2.FromScalar(60074914);
            Assert.That(2 * p, Is.EqualTo(p + p));
        }

        [Test]
        public void G1_subgroup_check()
        {
            var p = Bls.G1.FromScalar(10403746324);
            Assert.That(Bls.SubgroupOrder * p, Is.EqualTo(Bls.G1.Zero));
        }

        [Test]
        public void G2_subgroup_check()
        {
            var p = Bls.G2.FromScalar(92461756);
            Assert.That(Bls.SubgroupOrder * p, Is.EqualTo(Bls.G2.Zero));
        }

        [Test]
        public void G1_multiplication_by_unnormalised_scalar()
        {
            Span<byte> s = stackalloc byte[32];
            Span<byte> unnormalised = stackalloc byte[32];
            s[30] = 0xDA;
            s[31] = 0xAC;
            Bls.SubgroupOrder.CopyTo(unnormalised);
            unnormalised[30] += 0xDA;
            unnormalised[31] += 0xAC;

            var p = Bls.G1.FromScalar(43333333);
            Assert.That(unnormalised * p, Is.EqualTo(s * p));
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

            var p = Bls.G2.FromScalar(43577532);
            Bls.G2 res = s2 * p;
            Bls.G2 expected = s1 * p;

            res.X.Item1.Should().Equal(expected.X.Item1);
            res.X.Item2.Should().Equal(expected.X.Item2);
            res.Y.Item1.Should().Equal(expected.Y.Item1);
            res.Y.Item2.Should().Equal(expected.Y.Item2);
        }

        [Test]
        public void Pairing_degeneracy()
        {
            var p = Bls.G1.FromScalar(6758363496);
            var q = Bls.G2.FromScalar(14863974504635);
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
            BinaryPrimitives.WriteUInt128BigEndian(s1[16..], 35789430543857);
            BinaryPrimitives.WriteUInt128BigEndian(s2[16..], 60857913825);

            var p = Bls.G1.FromScalar(5452347823);
            var q = Bls.G2.FromScalar(984534538);

            Assert.That(Bls.PairingsEqual(s1 * p, s2 * q, s2 * (s1 * p), q));
            Assert.That(Bls.PairingsEqual(s1 * p, s2 * q, p, s2 * (s1 * q)));
        }
    }
}
