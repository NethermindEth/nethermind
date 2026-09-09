// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
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
    [TestCase(ulong.MaxValue / 2 + 2, 64UL)]
    [TestCase(ulong.MaxValue, 64UL)]
    public void Can_get_the_next_power_of_two_exponent(ulong value, ulong expectedResult) => Assert.That(Merkle.NextPowerOfTwoExponent(value), Is.EqualTo(expectedResult));

    [Test]
    public void Zero_hashes_0_is_correct() => Assert.That(Merkle.ZeroHashes[0], Is.EqualTo(UInt256.Zero));

    /// <remarks>
    /// The table is baked-in constant data, and the zero-subtree tests below cannot check it:
    /// <see cref="Merkle.HashConcatenation"/> short-circuits an all-zero pair straight back to the
    /// table, so they never run SHA-256 and compare it against itself. Rebuild the chain here.
    /// </remarks>
    [Test]
    public void Zero_hashes_match_an_independently_computed_chain()
    {
        byte[] expected = new byte[32];
        Span<byte> actual = stackalloc byte[32];

        for (int level = 0; level < Merkle.ZeroHashes.Length; level++)
        {
            Merkle.ZeroHashes[level].ToLittleEndian(actual);
            Assert.That(actual.ToArray(), Is.EqualTo(expected), $"ZeroHashes[{level}]");
            expected = HashUtility.Hash(expected, expected);
        }
    }

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
        UInt256 root = MerkleizeWithConverter(34UL + byte.MaxValue + ushort.MaxValue + uint.MaxValue, UInt64SszBasicTypeConverter.Feed);
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
    public void Merkleize_matches_independent_tree(
        [Values(0, 1, 31, 32, 33, 63, 64, 65, 511, 512, 513, 1024, 8193)] int byteLength,
        [Values] bool extendLimit)
    {
        byte[] bytes = new byte[byteLength];
        new Random(42).NextBytes(bytes);
        ulong limit = extendLimit ? (ulong)(byteLength / 32 + 17) : 0;

        Merkle.Merkleize(out UInt256 actual, bytes, limit);

        Assert.That(actual, Is.EqualTo(ReferenceRoot(bytes, limit)));
    }

    [Test]
    public void Merkleize_supports_full_ulong_limit([Values(0, 1, 33)] int byteLength)
    {
        byte[] bytes = new byte[byteLength];
        bytes.AsSpan().Fill(0xA5);

        Merkle.Merkleize(out UInt256 actual, bytes, ulong.MaxValue);

        Assert.That(actual, Is.EqualTo(ReferenceRoot(bytes, ulong.MaxValue)));
    }

    [TestCase(33, 1UL)]
    [TestCase(128, 3UL)]
    [TestCase(1048576, 1UL)]
    public void Merkleize_rejects_chunk_count_above_limit(int byteLength, ulong limit) =>
        Assert.That(() => Merkle.Merkleize(out _, new byte[byteLength], limit), Throws.TypeOf<ArgumentOutOfRangeException>());

    [Test]
    public void Merkleizer_empty_root_ignores_scratch_contents([Values(0, 1, 6, 63, 64)] int depth)
    {
        Span<UInt256> scratch = stackalloc UInt256[depth + 1];
        scratch.Fill(UInt256.MaxValue);
        Merkleizer merkleizer = new(scratch);

        merkleizer.CalculateRoot(out UInt256 actual);

        Assert.That(actual, Is.EqualTo(ReferenceRoot(ReadOnlySpan<byte>.Empty, depth == 64 ? ulong.MaxValue : 1UL << depth)));
    }

    [Test]
    public void Merkleizer_rejects_more_leaves_than_capacity([Values(0, 1, 3)] int depth) =>
        Assert.That(() => OverfillMerkleizer(depth), Throws.TypeOf<InvalidOperationException>());

    [Test]
    public void Merkleizer_rejects_invalid_depth([Values(-1, 65, int.MaxValue)] int depth) =>
        Assert.That(() => { _ = new Merkleizer(depth); }, Throws.TypeOf<ArgumentOutOfRangeException>());

    [Test]
    public void Merkleizer_rejects_invalid_scratch_length([Values(0, 66)] int length) =>
        Assert.That(() => { _ = new Merkleizer(new UInt256[length]); }, Throws.TypeOf<ArgumentOutOfRangeException>());

    private static void OverfillMerkleizer(int depth)
    {
        Merkleizer merkleizer = new(depth);
        for (int i = 0; i <= (1 << depth); i++)
        {
            merkleizer.Feed(new UInt256((ulong)i + 1));
        }
    }

    [Test]
    public void Merkleizer_root_does_not_modify_accumulated_leaves()
    {
        Span<UInt256> scratch = stackalloc UInt256[4];
        Merkleizer merkleizer = new(scratch);
        byte[] leaves = new byte[4 * 32];
        new Random(42).NextBytes(leaves);

        for (int i = 0; i < 4; i++)
        {
            merkleizer.Feed(new UInt256(leaves.AsSpan(i * 32, 32)));
            merkleizer.CalculateRoot(out UInt256 actual);
            Assert.That(actual, Is.EqualTo(ReferenceRoot(leaves.AsSpan(0, (i + 1) * 32), 8)));
        }
    }

    [Test]
    public void Merkleize_progressive_matches_independent_tree(
        [Values(0, 1, 2, 5, 6, 21, 22, 85, 86, 341, 342)] int chunks,
        [Values(0, 1, 31)] int remainder)
    {
        byte[] bytes = new byte[chunks * 32 + remainder];
        new Random(42).NextBytes(bytes);

        Merkle.MerkleizeProgressive(out UInt256 actual, bytes);

        Assert.That(actual, Is.EqualTo(ReferenceProgressiveRoot(bytes)));
    }

    internal static UInt256 ReferenceProgressiveRoot(ReadOnlySpan<byte> bytes, int subtreeBytes = 32)
    {
        if (bytes.IsEmpty)
        {
            return UInt256.Zero;
        }

        int count = Math.Min(subtreeBytes, bytes.Length);
        UInt256 left = ReferenceRoot(bytes[..count], (ulong)subtreeBytes / 32);
        UInt256 right = ReferenceProgressiveRoot(bytes[count..], subtreeBytes * 4);
        return new UInt256(HashUtility.Hash(left.ToLittleEndian(), right.ToLittleEndian()));
    }

    [Test]
    public void Merkleize_large_input_matches_independent_tree(
        [Values(1048544, 1048575, 1048576, 1048577, 1048608, 2097185)] int byteLength,
        [Values(0UL, 65539UL, ulong.MaxValue)] ulong limit,
        [Values] bool allZero)
    {
        byte[] bytes = new byte[byteLength];
        if (!allZero) new Random(42).NextBytes(bytes);
        UInt256 expected = ReferenceRoot(bytes, limit);
        byte[] original = (byte[])bytes.Clone();

        Merkle.Merkleize(out UInt256 actual, bytes, limit);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Is.EqualTo(expected));
            Assert.That(bytes, Is.EqualTo(original));
        }
    }

    [Test]
    public void Merkleize_large_inputs_are_independent_under_concurrency()
    {
        byte[][] inputs = new byte[4][];
        UInt256[] expected = new UInt256[inputs.Length];
        for (int i = 0; i < inputs.Length; i++)
        {
            inputs[i] = new byte[1048576 + i];
            new Random(i).NextBytes(inputs[i]);
            expected[i] = ReferenceRoot(inputs[i]);
        }

        Parallel.For(0, 16, i =>
        {
            int index = i % inputs.Length;
            Merkle.Merkleize(out UInt256 actual, inputs[index]);
            Assert.That(actual, Is.EqualTo(expected[index]));
        });
    }

    internal static UInt256 ReferenceRoot(ReadOnlySpan<byte> bytes, ulong limit = 0)
    {
        int width = 1;
        while (width < (bytes.Length + 31) / 32)
        {
            width *= 2;
        }

        byte[] nodes = new byte[width * 32];
        bytes.CopyTo(nodes);
        byte[] zero = new byte[32];
        int depth = 0;
        for (int count = width; count > 1; count /= 2)
        {
            for (int i = 0; i < count / 2; i++)
            {
                SHA256.HashData(nodes.AsSpan(i * 64, 64), nodes.AsSpan(i * 32, 32));
            }

            zero = HashUtility.Hash(zero, zero);
            depth++;
        }

        int limitDepth = 0;
        for (ulong remaining = limit > 0 ? limit - 1 : 0; remaining > 0; remaining >>= 1)
        {
            limitDepth++;
        }

        byte[] root = nodes.AsSpan(0, 32).ToArray();
        while (depth++ < limitDepth)
        {
            root = HashUtility.Hash(root, zero);
            zero = HashUtility.Hash(zero, zero);
        }

        return new UInt256(root);
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
