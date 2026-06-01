// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz.Merkleization;
using Nethermind.Serialization.Ssz.SszBasicTypeConverters;
using NUnit.Framework;

namespace Nethermind.Serialization.Ssz.Test;

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
    [TestCase(ulong.MinValue, 0UL)]
    [TestCase(1UL, 0UL)]
    [TestCase(2UL, 1UL)]
    [TestCase(3UL, 2UL)]
    [TestCase(4UL, 2UL)]
    [TestCase(ulong.MaxValue / 2, 63UL)]
    [TestCase(ulong.MaxValue / 2 + 1, 63UL)]
    public void Can_get_the_next_power_of_two_exponent(ulong value, ulong expectedResult) => Assert.That(Merkle.NextPowerOfTwoExponent(value), Is.EqualTo(expectedResult));

    [Test]
    public void Zero_hashes_0_is_correct() => Assert.That(Merkle.ZeroHashes[0], Is.EqualTo(UInt256.Zero));

    [Test]
    public void Can_merkleize_bool()
    {
        UInt256 root = MerkleizeWithConverter(true, BooleanSszBasicTypeConverter.Feed);
        Assert.That(root.ToHexString(true), Is.EqualTo("0x0100000000000000000000000000000000000000000000000000000000000000"));
    }

    [Test]
    public void Can_merkleize_byte()
    {
        UInt256 root = MerkleizeWithConverter((byte)34, ByteSszBasicTypeConverter.Feed);
        Assert.That(root.ToHexString(true), Is.EqualTo("0x2200000000000000000000000000000000000000000000000000000000000000"));
    }

    [Test]
    public void Can_merkleize_ushort()
    {
        UInt256 root = MerkleizeWithConverter((ushort)(34 + byte.MaxValue), UInt16SszBasicTypeConverter.Feed);
        Assert.That(root.ToHexString(true), Is.EqualTo("0x2101000000000000000000000000000000000000000000000000000000000000"));
    }

    [Test]
    public void Can_merkleize_uint()
    {
        UInt256 root = MerkleizeWithConverter((uint)34 + byte.MaxValue + ushort.MaxValue, UInt32SszBasicTypeConverter.Feed);
        Assert.That(root.ToHexString(true), Is.EqualTo("0x2001010000000000000000000000000000000000000000000000000000000000"));
    }

    [Test]
    public void Can_merkleize_int()
    {
        UInt256 root = MerkleizeWithConverter(34 + byte.MaxValue + ushort.MaxValue, Int32SszBasicTypeConverter.Feed);
        Assert.That(root.ToHexString(true), Is.EqualTo("0x2001010000000000000000000000000000000000000000000000000000000000"));
    }

    [Test]
    public void Can_merkleize_negative_int()
    {
        UInt256 root = MerkleizeWithConverter(-1, Int32SszBasicTypeConverter.Feed);
        Assert.That(root.ToHexString(true), Is.EqualTo("0xffffffff00000000000000000000000000000000000000000000000000000000"));
    }

    [Test]
    public void Can_merkleize_ulong()
    {
        UInt256 root = MerkleizeWithConverter((ulong)34 + byte.MaxValue + ushort.MaxValue + uint.MaxValue, UInt64SszBasicTypeConverter.Feed);
        Assert.That(root.ToHexString(true), Is.EqualTo("0x1f01010001000000000000000000000000000000000000000000000000000000"));
    }

    [Test]
    public void Can_merkleize_negative_long()
    {
        UInt256 root = MerkleizeWithConverter(-1L, Int64SszBasicTypeConverter.Feed);
        Assert.That(root.ToHexString(true), Is.EqualTo("0xffffffffffffffff000000000000000000000000000000000000000000000000"));
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

        UInt256 root = MerkleizeWithConverter(input, UInt128SszBasicTypeConverter.Feed);
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

        UInt256 root = MerkleizeWithConverter(input, UInt256SszBasicTypeConverter.Feed);
        Assert.That(root.ToHexString(true), Is.EqualTo("0x1e01010001000000010000000000000000000000000000000000000000000000"));
    }

    [Test]
    public void Can_merkleize_uint256_vector()
    {
        Merkle.Merkleize(out UInt256 root, new UInt256[] { 1 });
        Assert.That(root.ToHexString(true), Is.EqualTo("0x0100000000000000000000000000000000000000000000000000000000000000"));
    }

    [Test]
    public void Can_merkleize_uint256_vector_longer()
    {
        Merkle.Merkleize(out UInt256 root, new UInt256[] { 1, 2, 3, 4 });
        Assert.That(root.ToHexString(true), Is.EqualTo("0xbfe3c665d2e561f13b30606c580cb703b2041287e212ade110f0bfd8563e21bb"));
    }

    [Test]
    public void Can_merkleize_bitvector()
    {
        Merkle.Merkleize(out UInt256 root, new byte[] { 123 });
        Assert.That(root.ToHexString(true), Is.EqualTo("0x7b00000000000000000000000000000000000000000000000000000000000000"));
    }

    [Test]
    public void Merkleize_byte_span_zero_pads_partial_chunk()
    {
        byte[] data = [0xAB, 0xCD];
        byte[] padded = new byte[32];
        data.CopyTo(padded.AsSpan());

        Merkle.Merkleize(out UInt256 actual, data);

        Assert.That(actual, Is.EqualTo(new UInt256(padded)));
    }

    [Test]
    public void MixInActiveFields_zero_pads_active_fields_chunk()
    {
        UInt256 baseRoot = new(123UL);
        Merkle.Merkleize(out UInt256 activeFieldsRoot, new byte[] { 0x05 });
        Merkle.Merkleize(out UInt256 expected, new UInt256[] { baseRoot, activeFieldsRoot });

        UInt256 actual = baseRoot;
        Merkle.MixInActiveFields(ref actual, [0x05]);

        Assert.That(actual, Is.EqualTo(expected));
    }

    [TestCase(2, 1)]
    [TestCase(4, 2)]
    [TestCase(8, 3)]
    [TestCase(16, 4)]
    [TestCase(32, 5)]
    [TestCase(64, 6)]
    public void Feed_test(int leafs, int depth)
    {
        Merkleizer merkleizer = new(depth);
        for (int i = 0; i < leafs; i++)
        {
            merkleizer.Feed(Merkle.ZeroHashes[0]);
        }

        merkleizer.CalculateRoot(out UInt256 actual);

        Assert.That(actual, Is.EqualTo(Merkle.ZeroHashes[depth]));
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
            Merkleizer merkleizer = new(depth);
            for (int i = j; i < leafs; i++)
            {
                merkleizer.Feed(Merkle.ZeroHashes[0]);
            }

            merkleizer.CalculateRoot(out result);
        }

        Assert.That(result, Is.EqualTo(Merkle.ZeroHashes[depth]));
    }

    [Test]
    public void Fill()
    {
        Merkleizer merkleizer = new(6);
        for (int i = 0; i < 7; i++)
        {
            merkleizer.Feed(Merkle.ZeroHashes[0]);
        }

        merkleizer.CalculateRoot(out UInt256 result);

        Assert.That(result, Is.EqualTo(Merkle.ZeroHashes[6]));
    }

    private delegate void FeedItem<T>(ref Merkleizer merkleizer, T value);

    private static UInt256 MerkleizeWithConverter<T>(T value, FeedItem<T> feed)
    {
        Merkleizer merkleizer = new(0);
        feed(ref merkleizer, value);
        merkleizer.CalculateRoot(out UInt256 root);
        return root;
    }
}
