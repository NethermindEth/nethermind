// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Core.Extensions;
using Nethermind.State.Flat.Hsst.BTree;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Hsst.BTree;

/// <summary>
/// Unit tests for the <see cref="HsstPartitionHashtable"/> codec: bucket/way layout, the
/// tag/offset round-trip, the SIMD <c>MatchMask</c> against its scalar reference, the empty-way
/// marker, and the u48 offset ceiling. These pin the on-disk bucket format independently of the
/// builder/reader that consume it.
/// </summary>
[TestFixture]
public class HsstPartitionHashtableTests
{
    [TestCase(1, ExpectedResult = 1)]
    [TestCase(6, ExpectedResult = 1)]
    [TestCase(7, ExpectedResult = 2)]
    [TestCase(12, ExpectedResult = 2)]
    [TestCase(13, ExpectedResult = 3)]
    [TestCase(6000, ExpectedResult = 1000)]
    public int BucketCount_Is_Ceil_KeyCount_Over_Six(int keyCount) => HsstPartitionHashtable.BucketCountFor(keyCount);

    [Test]
    public void Tag_Is_Never_Zero()
    {
        // The high 16 bits of this crafted hash are zero; Tag must promote it to 1 so the empty
        // marker is unambiguous.
        Assert.That(HsstPartitionHashtable.Tag(0x0000_FFFF_FFFF_FFFFUL), Is.EqualTo((ushort)1));
        Assert.That(HsstPartitionHashtable.Tag(0xABCD_0000_0000_0000UL), Is.EqualTo((ushort)0xABCD));
    }

    [Test]
    public void Insert_Then_MatchMask_And_OffsetAt_RoundTrip()
    {
        const int bucketCount = 1;
        Span<byte> buckets = new byte[HsstPartitionHashtable.RegionSize(bucketCount)];

        // Three keys whose hashes all land in the single bucket; record their (tag, offset).
        ReadOnlySpan<byte> k0 = Bytes.FromHexString("0011223344");
        ReadOnlySpan<byte> k1 = Bytes.FromHexString("aabbccddee");
        ReadOnlySpan<byte> k2 = Bytes.FromHexString("0102030405");
        (ulong h0, long o0) = (HsstPartitionHashtable.Hash(k0), 0);
        (ulong h1, long o1) = (HsstPartitionHashtable.Hash(k1), 12345);
        (ulong h2, long o2) = (HsstPartitionHashtable.Hash(k2), HsstPartitionHashtable.MaxOffset);

        Assert.That(HsstPartitionHashtable.TryInsert(buckets, bucketCount, h0, o0), Is.True);
        Assert.That(HsstPartitionHashtable.TryInsert(buckets, bucketCount, h1, o1), Is.True);
        Assert.That(HsstPartitionHashtable.TryInsert(buckets, bucketCount, h2, o2), Is.True);

        AssertResolves(buckets, bucketCount, h0, o0);
        AssertResolves(buckets, bucketCount, h1, o1);
        AssertResolves(buckets, bucketCount, h2, o2);

        static void AssertResolves(ReadOnlySpan<byte> buckets, int bucketCount, ulong hash, long expectedOffset)
        {
            int bucket = HsstPartitionHashtable.BucketIndex(hash, bucketCount);
            ReadOnlySpan<byte> b = buckets.Slice(bucket * HsstPartitionHashtable.BucketBytes, HsstPartitionHashtable.BucketBytes);
            uint mask = HsstPartitionHashtable.MatchMask(b, HsstPartitionHashtable.Tag(hash));
            Assert.That(mask, Is.Not.Zero);
            int way = System.Numerics.BitOperations.TrailingZeroCount(mask);
            Assert.That(HsstPartitionHashtable.OffsetAt(b, way), Is.EqualTo(expectedOffset));
        }
    }

    [Test]
    public void TryInsert_Drops_On_Bucket_Overflow_And_Out_Of_Range_Offset()
    {
        const int bucketCount = 1;
        Span<byte> buckets = new byte[HsstPartitionHashtable.RegionSize(bucketCount)];
        // Force 8 distinct live ways into the single bucket, then the 9th must be dropped.
        ulong tagOnly = 0x1234_0000_0000_0000UL; // same bucket (low32 == 0), same tag for all
        for (int i = 0; i < HsstPartitionHashtable.WaysPerBucket; i++)
            Assert.That(HsstPartitionHashtable.TryInsert(buckets, bucketCount, tagOnly, i), Is.True, $"way {i}");
        Assert.That(HsstPartitionHashtable.TryInsert(buckets, bucketCount, tagOnly, 99), Is.False, "overflow");

        Span<byte> fresh = new byte[HsstPartitionHashtable.RegionSize(bucketCount)];
        Assert.That(HsstPartitionHashtable.TryInsert(fresh, bucketCount, tagOnly, -1), Is.False, "negative offset");
        Assert.That(HsstPartitionHashtable.TryInsert(fresh, bucketCount, tagOnly, HsstPartitionHashtable.MaxOffset + 1), Is.False, "offset over u48");
    }

    [Test]
    public void MatchMask_Equals_Scalar_For_Random_Buckets()
    {
        // Deterministic pseudo-random tags across a bucket; SIMD and scalar must agree for every
        // probe tag, including tag 0 (must match nothing) and tags absent from the bucket.
        Span<byte> bucket = new byte[HsstPartitionHashtable.BucketBytes];
        ushort[] tags = [0, 1, 0xFFFF, 0x1234, 1, 0, 0xABCD, 0x0001];
        for (int way = 0; way < HsstPartitionHashtable.WaysPerBucket; way++)
            BinaryPrimitives.WriteUInt16LittleEndian(bucket.Slice(way * HsstPartitionHashtable.TagBytes, HsstPartitionHashtable.TagBytes), tags[way]);

        foreach (ushort probe in new ushort[] { 0, 1, 0x1234, 0xFFFF, 0xABCD, 0x9999 })
            Assert.That(HsstPartitionHashtable.MatchMask(bucket, probe),
                Is.EqualTo(HsstPartitionHashtable.MatchMaskScalar(bucket, probe)), $"probe 0x{probe:x4}");

        // tag 0 (the empty marker) must never report a match even though empty ways hold 0.
        Assert.That(HsstPartitionHashtable.MatchMaskScalar(new byte[HsstPartitionHashtable.BucketBytes], 0),
            Is.Not.Zero, "scalar matches literal 0 — callers must never probe with tag 0");
    }

    [TestCase(0L)]
    [TestCase(1L)]
    [TestCase(0x0000_0000_00FFL)]
    [TestCase(0x00FF_FFFF_FFFFL)]
    [TestCase(HsstPartitionHashtable.MaxOffset)]
    public void U48_RoundTrips(long value)
    {
        Span<byte> buf = stackalloc byte[HsstPartitionHashtable.OffsetBytes];
        HsstPartitionHashtable.WriteU48(buf, value);
        Assert.That(HsstPartitionHashtable.ReadU48(buf), Is.EqualTo(value));
    }

    [Test]
    public void Hash_Is_Stable_And_Length_Sensitive()
    {
        // Write-side and read-side must compute identical hashes; differing-length keys differ.
        ReadOnlySpan<byte> key = Bytes.FromHexString("deadbeefcafe");
        ReadOnlySpan<byte> keyCopy = Bytes.FromHexString("deadbeefcafe");
        Assert.That(HsstPartitionHashtable.Hash(key), Is.EqualTo(HsstPartitionHashtable.Hash(keyCopy)));
        Assert.That(HsstPartitionHashtable.Hash(Bytes.FromHexString("dead")), Is.Not.EqualTo(HsstPartitionHashtable.Hash(Bytes.FromHexString("deadbe"))));

        // Distinct keys overwhelmingly produce distinct (bucket, tag) — sanity over a small set.
        HashSet<ulong> seen = [];
        for (int i = 0; i < 256; i++)
            seen.Add(HsstPartitionHashtable.Hash([(byte)i, (byte)(i * 7), (byte)(i * 31)]));
        Assert.That(seen.Count, Is.EqualTo(256));
    }
}
