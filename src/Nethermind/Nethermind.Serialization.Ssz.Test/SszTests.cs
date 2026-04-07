// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.IO;
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

        [Test]
        public void Can_roundtrip_uint128_asymmetric()
        {
            UInt128 value = new UInt128(0x0000000000000001, 0x0000000000000002);
            Span<byte> output = stackalloc byte[16];
            Ssz.Encode(output, value);
            Assert.That(output.ToHexString(), Is.EqualTo("02000000000000000100000000000000"));
            Ssz.Decode((ReadOnlySpan<byte>)output, out UInt128 decoded);
            Assert.That(decoded, Is.EqualTo(value));
        }

        [TestCase(true, "0x01")]
        [TestCase(false, "0x00")]
        public void Can_serialize_bool(bool value, string expectedValue)
        {
            byte output = Ssz.Encode(value);
            Assert.That(new[] { output }, Is.EqualTo(Bytes.FromHexString(expectedValue)));
        }

        [Test]
        public void DecodeBitvector_rejects_wrong_byte_length()
        {
            // Bitvector[5] needs ceil(5/8) = 1 byte, not 2
            byte[] twoBytes = [0x1F, 0x00];
            Assert.Throws<InvalidDataException>(() => Ssz.DecodeBitvector(twoBytes, 5));
        }

        [Test]
        public void DecodeBitvector_rejects_set_high_bits()
        {
            // Bitvector[5]: only bits 0-4 valid. 0xFF has bits 5-7 set.
            byte[] data = [0xFF];
            Assert.Throws<InvalidDataException>(() => Ssz.DecodeBitvector(data, 5));
        }

        [Test]
        public void DecodeBitvector_accepts_valid_input()
        {
            // Bitvector[5]: bits 0-4 set = 0x1F, high bits clear
            byte[] data = [0x1F];
            BitArray result = Ssz.DecodeBitvector(data, 5);
            Assert.That(result.Length, Is.EqualTo(5));
            for (int i = 0; i < 5; i++)
                Assert.That(result[i], Is.True);
        }

        [Test]
        public void DecodeBitvector_accepts_byte_aligned_length()
        {
            // Bitvector[8]: all bits set = 0xFF, no unused high bits
            byte[] data = [0xFF];
            BitArray result = Ssz.DecodeBitvector(data, 8);
            Assert.That(result.Length, Is.EqualTo(8));
            for (int i = 0; i < 8; i++)
                Assert.That(result[i], Is.True);
        }

        [Test]
        public void DecodeBitlist_accepts_valid_input()
        {
            // Bitlist with 3 data bits [true, false, true] + sentinel
            // Encoded: bits 0-2 = data, bit 3 = sentinel
            // so we have 0x0D = 0000_1101
            byte[] data = [0x0D];
            BitArray result = Ssz.DecodeBitlist(data);
            Assert.That(result.Length, Is.EqualTo(3));
            Assert.That(result[0], Is.True);
            Assert.That(result[1], Is.False);
            Assert.That(result[2], Is.True);
        }

        [Test]
        public void DecodeBitlist_rejects_empty_input()
        {
            // missing sentinel
            Assert.Throws<InvalidDataException>(() => Ssz.DecodeBitlist(ReadOnlySpan<byte>.Empty));
        }

        [Test]
        public void DecodeBitlist_rejects_zero_last_byte()
        {
            // Last byte must contain the sentinel 1-bit; 0x00 has none
            byte[] data = [0x00];
            Assert.Throws<InvalidDataException>(() => Ssz.DecodeBitlist(data));
        }
    }
}
