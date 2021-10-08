//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using NUnit.Framework;

namespace Nethermind.Ssz.Test
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
            Assert.AreEqual(Bytes.FromHexString(expectedOutput), output.ToArray());
        }

        [TestCase(ushort.MinValue, "0x0000")]
        [TestCase((ushort)1, "0x0100")]
        [TestCase(ushort.MaxValue, "0xffff")]
        public void Can_serialize_uin16(ushort uint16, string expectedOutput)
        {
            Span<byte> output = stackalloc byte[2];
            Ssz.Encode(output, uint16);
            Assert.AreEqual(Bytes.FromHexString(expectedOutput), output.ToArray());
        }

        [TestCase(0U, "0x00000000")]
        [TestCase(1U, "0x01000000")]
        [TestCase(uint.MaxValue, "0xffffffff")]
        public void Can_serialize_uin32(uint uint32, string expectedOutput)
        {
            Span<byte> output = stackalloc byte[4];
            Ssz.Encode(output, uint32);
            Assert.AreEqual(Bytes.FromHexString(expectedOutput), output.ToArray());
        }

        [TestCase(0UL, "0x0000000000000000")]
        [TestCase(1UL, "0x0100000000000000")]
        [TestCase(ulong.MaxValue, "0xffffffffffffffff")]
        public void Can_serialize_uin64(ulong uint64, string expectedOutput)
        {
            Span<byte> output = stackalloc byte[8];
            Ssz.Encode(output, uint64);
            Assert.AreEqual(Bytes.FromHexString(expectedOutput), output.ToArray());
        }

        [Test]
        public void Can_serialize_uin128_0()
        {
            Span<byte> output = stackalloc byte[16];
            Ssz.Encode(output, UInt128.Zero);
            Assert.AreEqual("00000000000000000000000000000000", output.ToHexString());
        }

        [Test]
        public void Can_serialize_uin128_1()
        {
            Span<byte> output = stackalloc byte[16];
            Ssz.Encode(output, UInt128.One);
            Assert.AreEqual("01000000000000000000000000000000", output.ToHexString());
        }

        [Test]
        public void Can_serialize_uin128_max()
        {
            Span<byte> output = stackalloc byte[16];
            Ssz.Encode(output, UInt128.MaxValue);
            Assert.AreEqual("ffffffffffffffffffffffffffffffff", output.ToHexString());
        }

        [Test]
        public void Can_serialize_uin256_0()
        {
            Span<byte> output = stackalloc byte[32];
            Ssz.Encode(output, UInt256.Zero);
            Assert.AreEqual("0000000000000000000000000000000000000000000000000000000000000000", output.ToHexString());
        }

        [Test]
        public void Can_serialize_uin256_1()
        {
            Span<byte> output = stackalloc byte[32];
            Ssz.Encode(output, UInt256.One);
            Assert.AreEqual("0100000000000000000000000000000000000000000000000000000000000000", output.ToHexString());
        }

        [Test]
        public void Can_serialize_uin256_max()
        {
            Span<byte> output = stackalloc byte[32];
            Ssz.Encode(output, UInt256.MaxValue);
            Assert.AreEqual("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", output.ToHexString());
        }

        [TestCase(true, "0x01")]
        [TestCase(false, "0x00")]
        public void Can_serialize_bool(bool value, string expectedValue)
        {
            byte output = Ssz.Encode(value);
            Assert.AreEqual(Bytes.FromHexString(expectedValue), new [] {output});
        }
    }
}