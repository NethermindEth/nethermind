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
        [TestCase(0, "")]
        [TestCase(1, "")]
        [TestCase(byte.MaxValue, "")]
        public void Can_serialize_uin8(byte uint8, string expectedOutput)
        {
            Span<byte> output = stackalloc byte[1];
            Ssz.EncodeInt8(output, uint8);
            Assert.AreEqual(Bytes.FromHexString(expectedOutput), output.ToArray());
        }
        
        [TestCase(0, "")]
        [TestCase(1, "")]
        [TestCase(ushort.MaxValue, "")]
        public void Can_serialize_uin16(ushort uint16, string expectedOutput)
        {
            Span<byte> output = stackalloc byte[2];
            Ssz.EncodeInt16(output, uint16);
            Assert.AreEqual(Bytes.FromHexString(expectedOutput), output.ToArray());
        }
        
        [TestCase(0, "")]
        [TestCase(1, "")]
        [TestCase(uint.MaxValue, "")]
        public void Can_serialize_uin32(uint uint32, string expectedOutput)
        {
            Span<byte> output = stackalloc byte[4];
            Ssz.EncodeInt32(output, uint32);
            Assert.AreEqual(Bytes.FromHexString(expectedOutput), output.ToArray());
        }
        
        [TestCase(0, "")]
        [TestCase(1, "")]
        [TestCase(ulong.MaxValue, "")]
        public void Can_serialize_uin64(ulong uint64, string expectedOutput)
        {
            Span<byte> output = stackalloc byte[8];
            Ssz.EncodeInt64(output, uint64);
            Assert.AreEqual(Bytes.FromHexString(expectedOutput), output.ToArray());
        }
        
        [Test]
        public void Can_serialize_uin128_0()
        {
            Span<byte> output = stackalloc byte[16];
            Ssz.EncodeInt128(output, UInt128.Zero);
            Assert.AreEqual("", output.ToArray());
        }
        
        [Test]
        public void Can_serialize_uin128_1()
        {
            Span<byte> output = stackalloc byte[16];
            Ssz.EncodeInt128(output, UInt128.One);
            Assert.AreEqual("", output.ToArray());
        }
        
        [Test]
        public void Can_serialize_uin128_max()
        {
            Span<byte> output = stackalloc byte[16];
            Ssz.EncodeInt128(output, UInt128.MaxValue);
            Assert.AreEqual("", output.ToArray());
        }
        
        [Test]
        public void Can_serialize_uin256_0()
        {
            Span<byte> output = stackalloc byte[32];
            Ssz.EncodeInt256(output, UInt256.Zero);
            Assert.AreEqual("", output.ToArray());
        }
        
        [Test]
        public void Can_serialize_uin256_1()
        {
            Span<byte> output = stackalloc byte[32];
            Ssz.EncodeInt256(output, UInt256.One);
            Assert.AreEqual("", output.ToArray());
        }
        
        [Test]
        public void Can_serialize_uin256_max()
        {
            Span<byte> output = stackalloc byte[32];
            Ssz.EncodeInt256(output, UInt256.MaxValue);
            Assert.AreEqual("", output.ToArray());
        }
        
        [TestCase(true, "0x01")]
        [TestCase(false, "0x00")]
        public void Can_serialize_bool(bool value, string expectedValue)
        {
            Span<byte> output = stackalloc byte[32];
            Ssz.EncodeBool(output, value);
            Assert.AreEqual(Bytes.FromHexString(expectedValue), output.ToArray());
        }
    }
}