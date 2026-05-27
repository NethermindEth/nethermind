// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Merkleization;
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
        Merkle.Merkleize(out UInt256 root, true);
        Assert.That(root.ToHexString(true), Is.EqualTo("0x0100000000000000000000000000000000000000000000000000000000000000"));
    }

    [Test]
    public void Can_merkleize_byte()
    {
        Merkle.Merkleize(out UInt256 root, (byte)34);
        Assert.That(root.ToHexString(true), Is.EqualTo("0x2200000000000000000000000000000000000000000000000000000000000000"));
    }

    [Test]
    public void Can_merkleize_ushort()
    {
        Merkle.Merkleize(out UInt256 root, (ushort)(34 + byte.MaxValue));
        Assert.That(root.ToHexString(true), Is.EqualTo("0x2101000000000000000000000000000000000000000000000000000000000000"));
    }

    [Test]
    public void Can_merkleize_uint()
    {
        Merkle.Merkleize(out UInt256 root, (uint)34 + byte.MaxValue + ushort.MaxValue);
        Assert.That(root.ToHexString(true), Is.EqualTo("0x2001010000000000000000000000000000000000000000000000000000000000"));
    }

    [Test]
    public void Can_merkleize_int()
    {
        Merkle.Merkleize(out UInt256 root, 34 + byte.MaxValue + ushort.MaxValue);
        Assert.That(root.ToHexString(true), Is.EqualTo("0x2001010000000000000000000000000000000000000000000000000000000000"));
    }

    [Test]
    public void Can_merkleize_ulong()
    {
        Merkle.Merkleize(out UInt256 root, (ulong)34 + byte.MaxValue + ushort.MaxValue + uint.MaxValue);
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

        Merkle.Merkleize(out UInt256 root, input);
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

        Merkle.Merkleize(out UInt256 root, input);
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
    public void Merkleizer_Feed_list_produces_correct_root()
    {
        // List[uint8, 64] containing just [0xAB]
        //   pack:        0xAB padded to 32 bytes = 1 chunk
        //   chunk_limit: ceil(64 * 1 / 32) = 2
        //   merkleize:   hash tree with 2 leaf slots (our chunk + 1 zero chunk)
        //   mix_in:      SHA256(merkle_root || little_endian(actual_count = 1))
        byte[] data = [0xAB];
        int limit = 64;

        // Manually compute expected root
        // Step 1: merkleize(pack(data), chunk_limit=2)
        ulong chunkCount = ((ulong)limit * sizeof(byte) + 31) / 32; // = 2
        Merkle.Merkleize(out UInt256 expectedRoot, data, chunkCount);
        // Step 2: mix_in_length(root, actual_count=1)
        Merkle.MixIn(ref expectedRoot, data.Length);

        // Compute via Merkleizer.Feed
        Merkleizer merkleizer = new(0);
        merkleizer.Feed((ReadOnlySpan<byte>)data, limit);
        UInt256 actualRoot = merkleizer.CalculateRoot();

        Assert.That(actualRoot, Is.EqualTo(expectedRoot));
    }

    [Test]
    public void Feed_memory_enumerable_is_single_pass()
    {
        SinglePassReadOnlyMemoryEnumerable items = new(
        [
            new byte[] { 1 },
            new byte[] { 2 },
            new byte[] { 3 },
        ]);

        Merkleizer merkleizer = new(3);

        merkleizer.Feed(items, 4);

        Assert.That(items.EnumerationCount, Is.EqualTo(1));
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
            Merkleizer merkleizer = new(depth);
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
        Merkleizer merkleizer = new(6);
        for (int i = 0; i < 7; i++)
        {
            merkleizer.Feed(Merkle.ZeroHashes[0]);
        }

        UInt256 result = merkleizer.CalculateRoot();

        Assert.That(result, Is.EqualTo(Merkle.ZeroHashes[6]));
    }

    private sealed class SinglePassReadOnlyMemoryEnumerable(byte[][] items) : IEnumerable<ReadOnlyMemory<byte>>
    {
        public int EnumerationCount { get; private set; }

        public IEnumerator<ReadOnlyMemory<byte>> GetEnumerator()
        {
            EnumerationCount++;
            Assert.That(EnumerationCount, Is.EqualTo(1));

            foreach (byte[] item in items)
            {
                yield return item;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
