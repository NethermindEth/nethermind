// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Merkleization;
using NUnit.Framework;

namespace Nethermind.Serialization.Ssz.Test
{
    public static class UInt256Extensions
    {
        public static string ToHexString(this UInt256 @this, bool withZeroX)
        {
            Span<byte> bytes = stackalloc byte[32];
            @this.ToLittleEndian(bytes);
            return bytes.ToHexString(withZeroX);
        }
    }

    [TestFixture]
    public class MerkleTests
    {
        [TestCase(uint.MinValue, 1U)]
        [TestCase(1U, 1U)]
        [TestCase(2U, 2U)]
        [TestCase(3U, 4U)]
        [TestCase(4U, 4U)]
        [TestCase(uint.MaxValue / 2, 2147483648U)]
        [TestCase(uint.MaxValue / 2 + 1, 2147483648U)]
        public void Can_get_the_next_power_of_two_32(uint value, uint expectedResult)
        {
            Assert.That(Merkle.NextPowerOfTwo(value), Is.EqualTo(expectedResult));
        }

        [TestCase(ulong.MinValue, 1UL)]
        [TestCase(1UL, 1UL)]
        [TestCase(2UL, 2UL)]
        [TestCase(3UL, 4UL)]
        [TestCase(4UL, 4UL)]
        [TestCase(ulong.MaxValue / 2, 9223372036854775808UL)]
        [TestCase(ulong.MaxValue / 2 + 1, 9223372036854775808UL)]
        public void Can_get_the_next_power_of_two_64(ulong value, ulong expectedResult)
        {
            Assert.That(Merkle.NextPowerOfTwo(value), Is.EqualTo(expectedResult));
        }

        [TestCase(ulong.MinValue, 0UL)]
        [TestCase(1UL, 0UL)]
        [TestCase(2UL, 1UL)]
        [TestCase(3UL, 2UL)]
        [TestCase(4UL, 2UL)]
        [TestCase(ulong.MaxValue / 2, 63UL)]
        [TestCase(ulong.MaxValue / 2 + 1, 63UL)]
        public void Can_get_the_next_power_of_two_exponent(ulong value, ulong expectedResult)
        {
            Assert.That(Merkle.NextPowerOfTwoExponent(value), Is.EqualTo(expectedResult));
        }

        [Test]
        public void Zero_hashes_0_is_correct()
        {
            Assert.That(Merkle.ZeroHashes[0], Is.EqualTo(UInt256.Zero));
        }

        [Test]
        public void Can_merkleize_bool()
        {
            Merkle.Ize(out UInt256 root, true);
            Assert.That(root.ToHexString(true), Is.EqualTo("0x0100000000000000000000000000000000000000000000000000000000000000"));
        }

        [Test]
        public void Can_merkleize_byte()
        {
            Merkle.Ize(out UInt256 root, (byte)34);
            Assert.That(root.ToHexString(true), Is.EqualTo("0x2200000000000000000000000000000000000000000000000000000000000000"));
        }

        [Test]
        public void Can_merkleize_ushort()
        {
            Merkle.Ize(out UInt256 root, (ushort)(34 + byte.MaxValue));
            Assert.That(root.ToHexString(true), Is.EqualTo("0x2101000000000000000000000000000000000000000000000000000000000000"));
        }

        [Test]
        public void Can_merkleize_uint()
        {
            Merkle.Ize(out UInt256 root, (uint)34 + byte.MaxValue + ushort.MaxValue);
            Assert.That(root.ToHexString(true), Is.EqualTo("0x2001010000000000000000000000000000000000000000000000000000000000"));
        }

        [Test]
        public void Can_merkleize_int()
        {
            Merkle.Ize(out UInt256 root, 34 + byte.MaxValue + ushort.MaxValue);
            Assert.That(root.ToHexString(true), Is.EqualTo("0x2001010000000000000000000000000000000000000000000000000000000000"));
        }

        [Test]
        public void Can_merkleize_ulong()
        {
            Merkle.Ize(out UInt256 root, (ulong)34 + byte.MaxValue + ushort.MaxValue + uint.MaxValue);
            Assert.That(root.ToHexString(true), Is.EqualTo("0x1f01010001000000000000000000000000000000000000000000000000000000"));
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

            Merkle.Ize(out UInt256 root, input);
            Assert.That(root.ToHexString(true), Is.EqualTo("0x1e01010001000000010000000000000000000000000000000000000000000000"));
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

            Merkle.Ize(out UInt256 root, input);
            Assert.That(root.ToHexString(true), Is.EqualTo("0x1e01010001000000010000000000000000000000000000000000000000000000"));
        }

        [Test]
        public void Can_merkleize_bool_vector()
        {
            Merkle.Ize(out UInt256 root, new[] { true, false });
            Assert.That(root.ToHexString(true), Is.EqualTo("0x0100000000000000000000000000000000000000000000000000000000000000"));
        }

        [Test]
        public void Can_merkleize_ushort_vector()
        {
            Merkle.Ize(out UInt256 root, new[] { (ushort)1, (ushort)3 });
            Assert.That(root.ToHexString(true), Is.EqualTo("0x0100030000000000000000000000000000000000000000000000000000000000"));
        }

        [Test]
        public void Can_merkleize_uint_vector()
        {
            Merkle.Ize(out UInt256 root, new[] { 1U, 3U });
            Assert.That(root.ToHexString(true), Is.EqualTo("0x0100000003000000000000000000000000000000000000000000000000000000"));
        }

        [Test]
        public void Can_merkleize_ulong_vector()
        {
            Merkle.Ize(out UInt256 root, new[] { 1UL, 3UL });
            Assert.That(root.ToHexString(true), Is.EqualTo("0x0100000000000000030000000000000000000000000000000000000000000000"));
        }

        [Test]
        public void Can_merkleize_uint128_vector()
        {
            Merkle.Ize(out UInt256 root, new UInt128[] { 1, 3, 5 });
            Assert.That(root.ToHexString(true), Is.EqualTo("0xf189891181de961f99a35c1aa21c0d909bf30bb8bebb760050f3d06dc56e488a"));
        }

        [Test]
        public void Can_merkleize_uint256_vector()
        {
            Merkle.Ize(out UInt256 root, new UInt256[] { 1 });
            Assert.That(root.ToHexString(true), Is.EqualTo("0x0100000000000000000000000000000000000000000000000000000000000000"));
        }

        [Test]
        public void Can_merkleize_uint256_vector_longer()
        {
            Merkle.Ize(out UInt256 root, new UInt256[] { 1, 2, 3, 4 });
            Assert.That(root.ToHexString(true), Is.EqualTo("0xbfe3c665d2e561f13b30606c580cb703b2041287e212ade110f0bfd8563e21bb"));
        }

        [Test]
        public void Can_merkleize_uint128_vector_full()
        {
            Merkle.Ize(out UInt256 root, new UInt128[] { 1, 3 });
            Assert.That(root.ToHexString(true), Is.EqualTo("0x0100000000000000000000000000000003000000000000000000000000000000"));
        }

        [Test]
        public void Can_merkleize_bitlist()
        {
            Merkle.IzeBits(out UInt256 root, new byte[] { 123 }, 0);
            Assert.That(root.ToHexString(true), Is.EqualTo("0xe5e12694be373406e317c583b5fd9e7a642913dc20a5c4947edb202dafbbc0ee"));
        }

        [Test]
        public void Can_merkleize_bitlist_with_limit()
        {
            Merkle.IzeBits(out UInt256 root, new byte[] { 17 }, 2);
            Assert.That(root.ToHexString(true), Is.EqualTo("0x60d461bd1cec1a858ba48a27799c9686c15ad1625743bafa70674afc530f981a"));
        }

        [Test]
        public void Can_merkleize_bitlist_high_limit_and_null()
        {
            Merkle.IzeBits(out UInt256 root, new byte[] { 0 }, 8);
            Assert.That(root.ToHexString(true), Is.EqualTo("0x881690bb860e3a4f7681f51f1eccc59dac2718eeb0c0585cd698ad0650938b33"));
        }

        [Test]
        public void Can_merkleize_bitlist_high_limit_and_small()
        {
            Merkle.IzeBits(out UInt256 root, new byte[] { 3 }, 8);
            Assert.That(root.ToHexString(true), Is.EqualTo("0x9e1ff035a32c3d3085074e676356984c077f70bed47814956a9ef8852dcb8161"));
        }

        [Test]
        public void Can_merkleize_bitvector()
        {
            Merkle.Ize(out UInt256 root, new byte[] { 123 });
            Assert.That(root.ToHexString(true), Is.EqualTo("0x7b00000000000000000000000000000000000000000000000000000000000000"));
        }

        [Test]
        public void Set_check()
        {
            Merkleizer context = new Merkleizer(1);
            for (int i = 0; i < 64; i++)
            {
                context.SetKthBit(i);
                Assert.True(context.IsKthBitSet(i), i.ToString());
                context.UnsetKthBit(i);
                Assert.False(context.IsKthBitSet(i), i.ToString());
            }
        }

        [Test]
        public void Check_false()
        {
            Merkleizer context = new Merkleizer(1);
            for (int i = 0; i < 64; i++)
            {
                Assert.False(context.IsKthBitSet(i), i.ToString());
            }
        }

        [TestCase(2, 1)]
        [TestCase(4, 2)]
        [TestCase(8, 3)]
        [TestCase(16, 4)]
        [TestCase(32, 5)]
        [TestCase(64, 6)]
        public void Feed_test(int leafs, int depth)
        {
            Merkleizer merkleizer = new Merkleizer(depth);
            for (int i = 0; i < leafs; i++)
            {
                merkleizer.Feed(Merkle.ZeroHashes[0]);
            }

            Assert.That(merkleizer.CalculateRoot(), Is.EqualTo(Merkle.ZeroHashes[depth]));
        }

        [TestCase(2, 1)]
        [TestCase(4, 2)]
        [TestCase(8, 3)]
        [TestCase(16, 4)]
        [TestCase(32, 5)]
        [TestCase(64, 6)]
        public void Feed_test_fill(int leafs, int depth)
        {
            UInt256 result = UInt256.Zero;
            for (int j = 0; j < leafs; j++)
            {
                Merkleizer merkleizer = new Merkleizer(depth);
                for (int i = j; i < leafs; i++)
                {
                    merkleizer.Feed(Merkle.ZeroHashes[0]);
                }

                result = merkleizer.CalculateRoot();
            }

            Assert.That(result, Is.EqualTo(Merkle.ZeroHashes[depth]));
        }

        [Test]
        public void Fill()
        {
            Merkleizer merkleizer = new Merkleizer(6);
            for (int i = 0; i < 7; i++)
            {
                merkleizer.Feed(Merkle.ZeroHashes[0]);
            }

            UInt256 result = merkleizer.CalculateRoot();

            Assert.That(result, Is.EqualTo(Merkle.ZeroHashes[6]));
        }
    }
}
