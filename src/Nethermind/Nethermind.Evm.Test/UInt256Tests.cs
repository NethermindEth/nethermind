//  Copyright (c) 2021 Demerzel Solutions Limited
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
using System.Numerics;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class UInt256Tests
    {
        [Test]
        public void IsOne()
        {
            Assert.True(UInt256.One.IsOne, "1");
            Assert.False(UInt256.Zero.IsOne, "0");
            Assert.False(((UInt256)BigInteger.Pow(2, 64)).IsOne, "2^64");
            Assert.False(((UInt256)BigInteger.Pow(2, 128)).IsOne, "2^128");
            Assert.False(((UInt256)BigInteger.Pow(2, 196)).IsOne, "2^196");
        }
        
        [Test]
        public void To_big_endian_can_store_in_address()
        {
            Span<byte> target = stackalloc byte[20];
            UInt256 a = new(Bytes.FromHexString("0xA0A1A2A3A4A5A6A7B0B1B2B3B4B5B6B7C0C1C2C3C4C5C6C7D0D1D2D3D4D5D6D7").AsSpan(), true);
            a.ToBigEndian(target);
            Assert.AreEqual("b4b5b6b7c0c1c2c3c4c5c6c7d0d1d2d3d4d5d6d7".ToUpperInvariant(), target.ToHexString().ToUpperInvariant());
        }
        
        [Test]
        public void To_big_endian_can_store_on_stack()
        {
            Span<byte> target = stackalloc byte[32];
            UInt256 a = new(Bytes.FromHexString("0xA0A1A2A3A4A5A6A7B0B1B2B3B4B5B6B7C0C1C2C3C4C5C6C7D0D1D2D3D4D5D6D7").AsSpan(), true);
            a.ToBigEndian(target);
            Assert.AreEqual("A0A1A2A3A4A5A6A7B0B1B2B3B4B5B6B7C0C1C2C3C4C5C6C7D0D1D2D3D4D5D6D7".ToUpperInvariant(), target.ToHexString().ToUpperInvariant());
        }
    }
}
