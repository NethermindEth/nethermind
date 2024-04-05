// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Serialization.Ssz.Test
{
    [TestFixture]
    public class SszTests
    {
        [TestCase(0, "0x00")]
        [TestCase(1, "0x01")]
        [TestCase(byte.MaxValue, "0xff")]
        public void Can_serialize_uin8(byte uint8, string expectedOutput)
        {
            Span<byte> output = stackalloc byte[1];
            Ssz.Encode(output, uint8);
            Assert.That(output.ToArray(), Is.EqualTo(Bytes.FromHexString(expectedOutput)));
        }

        [TestCase(ushort.MinValue, "0x0000")]
        [TestCase((ushort)1, "0x0100")]
        [TestCase(ushort.MaxValue, "0xffff")]
        public void Can_serialize_uin16(ushort uint16, string expectedOutput)
        {
            Span<byte> output = stackalloc byte[2];
            Ssz.Encode(output, uint16);
            Assert.That(output.ToArray(), Is.EqualTo(Bytes.FromHexString(expectedOutput)));
        }

        [TestCase(0U, "0x00000000")]
        [TestCase(1U, "0x01000000")]
        [TestCase(uint.MaxValue, "0xffffffff")]
        public void Can_serialize_uin32(uint uint32, string expectedOutput)
        {
            Span<byte> output = stackalloc byte[4];
            Ssz.Encode(output, uint32);
            Assert.That(output.ToArray(), Is.EqualTo(Bytes.FromHexString(expectedOutput)));
        }

        [TestCase(0UL, "0x0000000000000000")]
        [TestCase(1UL, "0x0100000000000000")]
        [TestCase(ulong.MaxValue, "0xffffffffffffffff")]
        public void Can_serialize_uin64(ulong uint64, string expectedOutput)
        {
            Span<byte> output = stackalloc byte[8];
            Ssz.Encode(output, uint64);
            Assert.That(output.ToArray(), Is.EqualTo(Bytes.FromHexString(expectedOutput)));
        }

        [Test]
        public void Can_serialize_uin128_0()
        {
            Span<byte> output = stackalloc byte[16];
            Ssz.Encode(output, UInt128.Zero);
            Assert.That(output.ToHexString(), Is.EqualTo("00000000000000000000000000000000"));
        }

        [Test]
        public void Can_serialize_uin128_1()
        {
            Span<byte> output = stackalloc byte[16];
            Ssz.Encode(output, UInt128.One);
            Assert.That(output.ToHexString(), Is.EqualTo("01000000000000000000000000000000"));
        }

        [Test]
        public void Can_serialize_uin128_max()
        {
            Span<byte> output = stackalloc byte[16];
            Ssz.Encode(output, UInt128.MaxValue);
            Assert.That(output.ToHexString(), Is.EqualTo("ffffffffffffffffffffffffffffffff"));
        }

        [Test]
        public void Can_serialize_uin256_0()
        {
            Span<byte> output = stackalloc byte[32];
            Ssz.Encode(output, UInt256.Zero);
            Assert.That(output.ToHexString(), Is.EqualTo("0000000000000000000000000000000000000000000000000000000000000000"));
        }

        [Test]
        public void Can_serialize_uin256_1()
        {
            Span<byte> output = stackalloc byte[32];
            Ssz.Encode(output, UInt256.One);
            Assert.That(output.ToHexString(), Is.EqualTo("0100000000000000000000000000000000000000000000000000000000000000"));
        }

        [Test]
        public void Can_serialize_uin256_max()
        {
            Span<byte> output = stackalloc byte[32];
            Ssz.Encode(output, UInt256.MaxValue);
            Assert.That(output.ToHexString(), Is.EqualTo("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"));
        }

        [TestCase(true, "0x01")]
        [TestCase(false, "0x00")]
        public void Can_serialize_bool(bool value, string expectedValue)
        {
            byte output = Ssz.Encode(value);
            Assert.That(new[] { output }, Is.EqualTo(Bytes.FromHexString(expectedValue)));
        }
    }
}
