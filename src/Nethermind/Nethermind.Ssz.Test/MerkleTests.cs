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
    public class MerkleTests
    {
        [TestCase(uint.MinValue, 1U)]
        [TestCase(1U, 1U)]
        [TestCase(2U, 2U)]
        [TestCase(3U, 4U)]
        [TestCase(uint.MaxValue / 2, 2147483648U)]
        [TestCase(uint.MaxValue / 2 + 1, 2147483648U)]
        public void Can_get_the_next_power_of_two_32(uint value, uint expectedResult)
        {
            Assert.AreEqual(expectedResult, Merkle.NextPowerOfTwo(value));
        }

        [TestCase(ulong.MinValue, 1UL)]
        [TestCase(1UL, 1UL)]
        [TestCase(2UL, 2UL)]
        [TestCase(3UL, 4UL)]
        [TestCase(ulong.MaxValue / 2, 9223372036854775808UL)]
        [TestCase(ulong.MaxValue / 2 + 1, 9223372036854775808UL)]
        public void Can_get_the_next_power_of_two_64(ulong value, ulong expectedResult)
        {
            Assert.AreEqual(expectedResult, Merkle.NextPowerOfTwo(value));
        }
        
        [Test]
        public void Zero_hashes_0_is_correct()
        {
            Assert.AreEqual(UInt256.Zero, Merkle.ZeroHashes[0]);
        }
        
        [Test]
        public void Can_merkleize_bool()
        {
            Span<byte> root = stackalloc byte[32];
            Merkle.Ize(root, true);
            Assert.AreEqual("0x0100000000000000000000000000000000000000000000000000000000000000", root.ToHexString(true));
        }
        
        [Test]
        public void Can_merkleize_byte()
        {
            Span<byte> root = stackalloc byte[32];
            Merkle.Ize(root, (byte)34);
            Assert.AreEqual("0x2200000000000000000000000000000000000000000000000000000000000000", root.ToHexString(true));
        }
        
        [Test]
        public void Can_merkleize_ushort()
        {
            Span<byte> root = stackalloc byte[32];
            Merkle.Ize(root, (ushort)(34 + byte.MaxValue));
            Assert.AreEqual("0x2101000000000000000000000000000000000000000000000000000000000000", root.ToHexString(true));
        }
        
        [Test]
        public void Can_merkleize_uint()
        {
            Span<byte> root = stackalloc byte[32];
            Merkle.Ize(root, (uint)34 + byte.MaxValue + ushort.MaxValue);
            Assert.AreEqual("0x2001010000000000000000000000000000000000000000000000000000000000", root.ToHexString(true));
        }
        
        [Test]
        public void Can_merkleize_int()
        {
            Span<byte> root = stackalloc byte[32];
            Merkle.Ize(root, 34 + byte.MaxValue + ushort.MaxValue);
            Assert.AreEqual("0x2001010000000000000000000000000000000000000000000000000000000000", root.ToHexString(true));
        }
        
        [Test]
        public void Can_merkleize_ulong()
        {
            Span<byte> root = stackalloc byte[32];
            Merkle.Ize(root, (ulong)34 + byte.MaxValue + ushort.MaxValue + uint.MaxValue);
            Assert.AreEqual("0x1f01010001000000000000000000000000000000000000000000000000000000", root.ToHexString(true));
        }
        
        [Test]
        public void Can_merkleize_uint128()
        {
            UInt128 input = UInt128.Zero;
            input += 34;
            input += byte.MaxValue;
            input += ushort.MaxValue;
            input += uint.MaxValue;
            input += ulong.MaxValue;
            Span<byte> root = stackalloc byte[32];
            Merkle.Ize(root, input);
            Assert.AreEqual("0x1e01010001000000010000000000000000000000000000000000000000000000", root.ToHexString(true));
        }
        
        [Test]
        public void Can_merkleize_uint256()
        {
            UInt256 input = UInt256.Zero;
            input += 34;
            input += byte.MaxValue;
            input += ushort.MaxValue;
            input += uint.MaxValue;
            input += ulong.MaxValue;
            Span<byte> root = stackalloc byte[32];
            Merkle.Ize(root, input);
            Assert.AreEqual("0x1e01010001000000010000000000000000000000000000000000000000000000", root.ToHexString(true));
        }

        [Test]
        public void Can_merkleize_bool_vector()
        {
            Span<byte> root = stackalloc byte[32];
            Merkle.Ize(root, new []{true, false});
            Assert.AreEqual("0x0100000000000000000000000000000000000000000000000000000000000000", root.ToHexString(true));
        }
        
        [Test]
        public void Can_merkleize_ushort_vector()
        {
            Span<byte> root = stackalloc byte[32];
            Merkle.Ize(root, new []{(ushort)1, (ushort)3});
            Assert.AreEqual("0x0100030000000000000000000000000000000000000000000000000000000000", root.ToHexString(true));
        }
        
        [Test]
        public void Can_merkleize_uint_vector()
        {
            Span<byte> root = stackalloc byte[32];
            Merkle.Ize(root, new []{1U, 3U});
            Assert.AreEqual("0x0100000003000000000000000000000000000000000000000000000000000000", root.ToHexString(true));
        }
        
        [Test]
        public void Can_merkleize_ulong_vector()
        {
            Span<byte> root = stackalloc byte[32];
            Merkle.Ize(root, new []{1UL, 3UL});
            Assert.AreEqual("0x0100000000000000030000000000000000000000000000000000000000000000", root.ToHexString(true));
        }
        
        [Test]
        public void Can_merkleize_uint128_vector()
        {
            Span<byte> root = stackalloc byte[32];
            Merkle.Ize(root, new UInt128[]{1, 3, 5});
            Assert.AreEqual("0xf189891181de961f99a35c1aa21c0d909bf30bb8bebb760050f3d06dc56e488a", root.ToHexString(true));
        }
        
        [Test]
        public void Can_merkleize_uint256_vector()
        {
            Span<byte> root = stackalloc byte[32];
            Merkle.Ize(root, new UInt256[]{1});
            Assert.AreEqual("0x0100000000000000000000000000000000000000000000000000000000000000", root.ToHexString(true));
        }
        
        [Test]
        public void Can_merkleize_uint256_vector_longer()
        {
            Span<byte> root = stackalloc byte[32];
            Merkle.Ize(root, new UInt256[]{1, 2, 3, 4});
            Assert.AreEqual("0xbfe3c665d2e561f13b30606c580cb703b2041287e212ade110f0bfd8563e21bb", root.ToHexString(true));
        }
        
        [Test]
        public void Can_merkleize_uint128_vector_full()
        {
            Span<byte> root = stackalloc byte[32];
            Merkle.Ize(root, new UInt128[]{1, 3});
            Assert.AreEqual("0x0100000000000000000000000000000003000000000000000000000000000000", root.ToHexString(true));
        }
        
        [Test]
        public void Can_merkleize_bitlist()
        {
            Span<byte> root = stackalloc byte[32];
            Merkle.IzeBits(root, new byte[]{123}, 0);
            Assert.AreEqual("0xe5e12694be373406e317c583b5fd9e7a642913dc20a5c4947edb202dafbbc0ee", root.ToHexString(true));
        }

        [Test]
        public void Can_merkleize_bitlist_with_limit()
        {
            Span<byte> root = stackalloc byte[32];
            Merkle.IzeBits(root, new byte[]{17}, 2);
            Assert.AreEqual("0x60d461bd1cec1a858ba48a27799c9686c15ad1625743bafa70674afc530f981a", root.ToHexString(true));
        }
        
        [Test]
        public void Can_merkleize_bitlist_high_limit_and_null()
        {
            Span<byte> root = stackalloc byte[32];
            Merkle.IzeBits(root, new byte[]{0}, 8);
            Assert.AreEqual("0x881690bb860e3a4f7681f51f1eccc59dac2718eeb0c0585cd698ad0650938b33", root.ToHexString(true));
        }
        
        [Test]
        public void Can_merkleize_bitlist_high_limit_and_small()
        {
            Span<byte> root = stackalloc byte[32];
            Merkle.IzeBits(root, new byte[]{3}, 8);
            Assert.AreEqual("0x9e1ff035a32c3d3085074e676356984c077f70bed47814956a9ef8852dcb8161", root.ToHexString(true));
        }
        
        [Test]
        public void Can_merkleize_bitvector()
        {
            Span<byte> root = stackalloc byte[32];
            Merkle.Ize(root, new byte[]{123});
            Assert.AreEqual("0x7b00000000000000000000000000000000000000000000000000000000000000", root.ToHexString(true));
        }
    }
}