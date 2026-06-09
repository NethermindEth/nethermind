// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.Hsst.BTree;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Hsst;

[TestFixture]
public class HsstPartitionedBTreeTests
{
    private const int KeyLength = 30;

    // 30-byte key with a big-endian counter in the last 4 bytes → strictly ascending, distinct.
    private static byte[] Key(int i)
    {
        byte[] k = new byte[KeyLength];
        BinaryPrimitives.WriteUInt32BigEndian(k.AsSpan(KeyLength - 4), (uint)i);
        return k;
    }

    // Distinct, variable-length value per key.
    private static byte[] Val(int i)
    {
        byte[] v = new byte[(i % 5) + 1];
        for (int j = 0; j < v.Length; j++) v[j] = (byte)(i + j + 1);
        return v;
    }

    private static byte[] BuildPartitioned(int count, HsstBTreeOptions options)
    {
        using PooledByteBufferWriter pooled = new(1 << 20);
        using HsstPartitionedBTreeBuilderBuffersContainer buffers = new();
        HsstPartitionedBTreeBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> b =
            new(ref pooled.GetWriter(), ref buffers.Buffers, KeyLength, options);
        try
        {
            for (int i = 0; i < count; i++) b.Add(Key(i), Val(i));
            b.Build();
            return pooled.WrittenSpan.ToArray();
        }
        finally
        {
            b.Dispose();
        }
    }

    // Key-after-value (keyFirst:false) partitioned build, exercising both the up-front Add and the
    // streaming BeginValueWrite/FinishValueWrite path (alternating by key index).
    private static byte[] BuildPartitionedKeyAfterValue(int count, HsstBTreeOptions options)
    {
        using PooledByteBufferWriter pooled = new(1 << 20);
        using HsstPartitionedBTreeBuilderBuffersContainer buffers = new();
        HsstPartitionedBTreeBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> b =
            new(ref pooled.GetWriter(), ref buffers.Buffers, KeyLength, options, keyFirst: false);
        try
        {
            for (int i = 0; i < count; i++)
            {
                byte[] key = Key(i);
                byte[] val = Val(i);
                if ((i & 1) == 0)
                {
                    b.Add(key, val);
                }
                else
                {
                    // Stream the value in (size unknown to the builder up front).
                    ref PooledByteBufferWriter.Writer w = ref b.BeginValueWrite();
                    val.CopyTo(w.GetSpan(val.Length));
                    w.Advance(val.Length);
                    b.FinishValueWrite(key, val.Length);
                }
            }
            b.Build();
            return pooled.WrittenSpan.ToArray();
        }
        finally
        {
            b.Dispose();
        }
    }

    // Equivalent plain key-after-value 0x01 build of the same entries, for parity checks.
    private static byte[] BuildPlainKeyAfterValue(int count)
    {
        using PooledByteBufferWriter pooled = new(1 << 20);
        using HsstBTreeBuilderBuffersContainer buffers = new();
        HsstBTreeBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> b =
            new(ref pooled.GetWriter(), ref buffers.Buffers, KeyLength, keyFirst: false);
        try
        {
            for (int i = 0; i < count; i++) b.Add(Key(i), Val(i));
            b.Build();
            return pooled.WrittenSpan.ToArray();
        }
        finally
        {
            b.Dispose();
        }
    }

    // Equivalent plain key-first 0x07 build of the same entries, for parity checks.
    private static byte[] BuildPlainKeyFirst(int count)
    {
        using PooledByteBufferWriter pooled = new(1 << 20);
        using HsstBTreeBuilderBuffersContainer buffers = new();
        HsstBTreeBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> b =
            new(ref pooled.GetWriter(), ref buffers.Buffers, KeyLength, keyFirst: true);
        try
        {
            for (int i = 0; i < count; i++) b.Add(Key(i), Val(i));
            b.Build();
            return pooled.WrittenSpan.ToArray();
        }
        finally
        {
            b.Dispose();
        }
    }

    private static List<(byte[] key, byte[] val)> Enumerate(byte[] data)
    {
        List<(byte[], byte[])> result = [];
        SpanByteReader reader = new(data);
        HsstEnumerator<SpanByteReader, NoOpPin> e = new(in reader, new Bound(0, data.Length));
        Span<byte> keyBuf = stackalloc byte[64];
        while (e.MoveNext(in reader))
        {
            ReadOnlySpan<byte> k = e.CopyCurrentLogicalKey(in reader, keyBuf);
            Bound vb = e.CurrentValue;
            byte[] v = vb.Length == 0 ? [] : data.AsSpan((int)vb.Offset, (int)vb.Length).ToArray();
            result.Add((k.ToArray(), v));
        }
        return result;
    }

    // Forces several partitions, each with a hashtable.
    private static HsstBTreeOptions MultiPartitionWithHashtable => new()
    {
        PartitionThresholdBytes = 10 * KeyLength, // ~10 keys per partition
        HashtableMinBytes = 0,                    // every partition gets a hashtable
    };

    [Test]
    public void IndexType_Byte_Is_PartitionedBTreeKeyFirst_At_Tail()
    {
        // Many keys with hashtables forced → directory + 0x08 trailer.
        byte[] data = BuildPartitioned(50, MultiPartitionWithHashtable);
        Assert.That(data[^1], Is.EqualTo((byte)IndexType.PartitionedBTreeKeyFirst));
    }

    [Test]
    public void Single_Small_Partition_Degrades_To_Plain_KeyFirst()
    {
        // Few keys, default thresholds → one partition, sub-4KiB → no hashtable → plain 0x07,
        // byte-identical to a standalone key-first build.
        byte[] partitioned = BuildPartitioned(5, HsstBTreeOptions.Default);
        Assert.That(partitioned[^1], Is.EqualTo((byte)IndexType.BTreeKeyFirst), "small contract must stay 0x07");
        byte[] plain = BuildPlainKeyFirst(5);
        Assert.That(partitioned, Is.EqualTo(plain), "single hashtable-less partition must be byte-identical to plain key-first");
    }

    [TestCase(1)]
    [TestCase(5)]
    [TestCase(50)]
    [TestCase(200)]
    public void RoundTrip_AllKeys_Found_With_Correct_Values(int count)
    {
        byte[] data = BuildPartitioned(count, MultiPartitionWithHashtable);
        for (int i = 0; i < count; i++)
        {
            Assert.That(HsstTestUtil.TryGet(data, Key(i), out byte[] value), Is.True, $"key {i} not found");
            Assert.That(value, Is.EqualTo(Val(i)), $"wrong value for key {i}");
        }
    }

    [Test]
    public void Absent_Keys_Return_False()
    {
        byte[] data = BuildPartitioned(40, MultiPartitionWithHashtable);
        // Below-range, in-range gap, and above-range absent keys.
        Assert.That(HsstTestUtil.TryGet(data, Key(1000), out _), Is.False, "above-range key must be absent");
        byte[] between = Key(5);
        between[0] = 0x7f; // perturb a high byte so it falls between partition first keys but matches nothing
        Assert.That(HsstTestUtil.TryGet(data, between, out _), Is.False, "perturbed key must be absent");
    }

    [Test]
    public void Floor_Seek_Returns_Largest_Key_Not_Exceeding()
    {
        // Even keys only, so odd probes floor to the preceding even key.
        using PooledByteBufferWriter pooled = new(1 << 20);
        using HsstPartitionedBTreeBuilderBuffersContainer buffers = new();
        HsstPartitionedBTreeBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> b =
            new(ref pooled.GetWriter(), ref buffers.Buffers, KeyLength, MultiPartitionWithHashtable);
        for (int i = 0; i < 60; i += 2) b.Add(Key(i), Val(i));
        b.Build();
        byte[] data = pooled.WrittenSpan.ToArray();
        b.Dispose();

        // Floor of an odd key i (3..59) is even key i-1; its value is Val(i-1).
        for (int i = 3; i < 60; i += 2)
        {
            Assert.That(HsstTestUtil.TryGetFloor(data, Key(i), out byte[] value), Is.True, $"floor of {i}");
            Assert.That(value, Is.EqualTo(Val(i - 1)), $"floor value of {i}");
        }
        // Exact even key still resolves; a key below all entries floors to nothing.
        Assert.That(HsstTestUtil.TryGetFloor(data, Key(0), out byte[] zero), Is.True);
        Assert.That(zero, Is.EqualTo(Val(0)));
    }

    [Test]
    public void Span_Triggered_Split_RoundTrips()
    {
        // Large values + a tiny span cap force span-based partition splits (no hashtable),
        // exercising the PartitionMaxSpanBytes path without materialising 2 GiB.
        HsstBTreeOptions opts = new()
        {
            PartitionThresholdBytes = long.MaxValue, // never trigger the key-bytes split
            PartitionMaxSpanBytes = 256,             // split on span instead
            HashtableMinBytes = int.MaxValue,        // never write a hashtable
        };
        using PooledByteBufferWriter pooled = new(1 << 20);
        using HsstPartitionedBTreeBuilderBuffersContainer buffers = new();
        HsstPartitionedBTreeBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> b =
            new(ref pooled.GetWriter(), ref buffers.Buffers, KeyLength, opts);
        byte[] big = new byte[120];
        for (int i = 0; i < 30; i++) { for (int j = 0; j < big.Length; j++) big[j] = (byte)(i + j); b.Add(Key(i), (byte[])big.Clone()); }
        b.Build();
        byte[] data = pooled.WrittenSpan.ToArray();
        b.Dispose();

        Assert.That(data[^1], Is.EqualTo((byte)IndexType.PartitionedBTreeKeyFirst), "span split must yield ≥2 partitions → 0x08");
        for (int i = 0; i < 30; i++)
        {
            Assert.That(HsstTestUtil.TryGet(data, Key(i), out byte[] value), Is.True, $"key {i}");
            for (int j = 0; j < big.Length; j++) Assert.That(value[j], Is.EqualTo((byte)(i + j)));
        }
    }

    [Test]
    public void Enumeration_Order_Matches_Plain_KeyFirst()
    {
        const int count = 120;
        List<(byte[] key, byte[] val)> partitioned = Enumerate(BuildPartitioned(count, MultiPartitionWithHashtable));
        List<(byte[] key, byte[] val)> plain = Enumerate(BuildPlainKeyFirst(count));

        Assert.That(partitioned.Count, Is.EqualTo(count));
        Assert.That(plain.Count, Is.EqualTo(count));
        for (int i = 0; i < count; i++)
        {
            Assert.That(partitioned[i].key, Is.EqualTo(plain[i].key), $"key mismatch at {i}");
            Assert.That(partitioned[i].val, Is.EqualTo(plain[i].val), $"value mismatch at {i}");
            Assert.That(partitioned[i].key, Is.EqualTo(Key(i)));
        }
    }

    // Crossing the configurable upper threshold (PartitionThresholdBytes) is what splits the
    // blob into multiple partitions (→ 0x08); staying under it leaves a single partition that,
    // sub-HashtableMinBytes, degrades to a plain 0x07. Every key must read back either way.
    [TestCase(300L, 0, 50, (byte)0x08)]            // over upper threshold → multi-partition + hashtable
    [TestCase(300L, int.MaxValue, 50, (byte)0x08)] // multi-partition: HashtableMinBytes does NOT suppress per-partition hashtables → still 0x08, reads OK
    [TestCase(4L * 1024 * 1024, 4096, 5, (byte)0x07)] // under both thresholds → single partition degrades to 0x07
    [TestCase(4L * 1024 * 1024, 0, 50, (byte)0x08)]  // single partition + hashtable → one-entry directory 0x08
    public void Upper_Threshold_Controls_Partitioning_And_Reads_Stay_Correct(
        long partitionThresholdBytes, int hashtableMinBytes, int count, byte expectedTail)
    {
        HsstBTreeOptions opts = new() { PartitionThresholdBytes = partitionThresholdBytes, HashtableMinBytes = hashtableMinBytes };
        byte[] data = BuildPartitioned(count, opts);

        Assert.That(data[^1], Is.EqualTo(expectedTail), "partition layout (0x08) must engage exactly when the upper threshold is exceeded");
        for (int i = 0; i < count; i++)
        {
            Assert.That(HsstTestUtil.TryGet(data, Key(i), out byte[] value), Is.True, $"key {i} not found (incl. keys in non-first partitions)");
            Assert.That(value, Is.EqualTo(Val(i)), $"wrong value for key {i}");
        }
    }

    // One partition that warrants a hashtable is emitted as a normal one-entry directory 0x08
    // (no special single-partition trailer). Hit, fallback (floor), and iteration all work.
    [Test]
    public void Single_Partition_With_Hashtable_Is_0x08()
    {
        const int count = 60;
        // Default 4 MiB partition threshold ⇒ one partition; HashtableMinBytes=0 ⇒ it gets a hashtable.
        byte[] data = BuildPartitioned(count, new HsstBTreeOptions { HashtableMinBytes = 0 });

        Assert.That(data[^1], Is.EqualTo((byte)IndexType.PartitionedBTreeKeyFirst), "single partition + hashtable must be a one-entry directory 0x08");

        // Hashtable-hit path: every key resolves with the right value.
        for (int i = 0; i < count; i++)
        {
            Assert.That(HsstTestUtil.TryGet(data, Key(i), out byte[] value), Is.True, $"key {i}");
            Assert.That(value, Is.EqualTo(Val(i)), $"value {i}");
        }

        // Absent key (above range) → not found.
        Assert.That(HsstTestUtil.TryGet(data, Key(count + 500), out _), Is.False);

        // Floor skips the hashtable → exercises the inner-B-tree fallback.
        Assert.That(HsstTestUtil.TryGetFloor(data, Key(count + 500), out byte[] floorVal), Is.True);
        Assert.That(floorVal, Is.EqualTo(Val(count - 1)));

        // Enumeration drains the one-entry directory → same sequence as the plain 0x07 build.
        List<(byte[] key, byte[] val)> partitioned = Enumerate(data);
        List<(byte[] key, byte[] val)> plain = Enumerate(BuildPlainKeyFirst(count));
        Assert.That(partitioned.Count, Is.EqualTo(count));
        for (int i = 0; i < count; i++)
        {
            Assert.That(partitioned[i].key, Is.EqualTo(plain[i].key), $"key mismatch at {i}");
            Assert.That(partitioned[i].val, Is.EqualTo(plain[i].val), $"value mismatch at {i}");
        }
    }

    // Once a blob partitions, EVERY partition carries a hashtable — even tail partitions far
    // below HashtableMinBytes. A hashtable-less table is a plain 0x07 blob, never a directory
    // partition. (Fails on the old per-partition HashtableMinBytes gate.)
    [Test]
    public void MultiPartition_All_Partitions_Have_Hashtable()
    {
        const int count = 55;
        // Tiny partition threshold ⇒ several partitions, each (incl. the tail) well under the
        // default 4 KiB HashtableMinBytes.
        byte[] data = BuildPartitioned(count, new HsstBTreeOptions { PartitionThresholdBytes = 300 });
        Assert.That(data[^1], Is.EqualTo((byte)IndexType.PartitionedBTreeKeyFirst), "expected a multi-partition 0x08 blob");

        // The top-level tree of a 0x08 blob IS the directory; walk it directly and check each
        // partition's metadata record carries a hashtable (u24 HashtableBucketCount at record bytes 24..27 > 0).
        SpanByteReader reader = new(data);
        HsstBTreeEnumerator<SpanByteReader, NoOpPin> dir = new(in reader, new Bound(0, data.Length), keyFirst: true);
        int partitions = 0;
        while (dir.MoveNext(in reader))
        {
            int o = (int)dir.CurrentValue.Offset;
            int bucketCount = data[o + 24] | (data[o + 25] << 8) | (data[o + 26] << 16);
            Assert.That(bucketCount, Is.GreaterThan(0), $"partition {partitions} has no hashtable");
            partitions++;
        }
        Assert.That(partitions, Is.GreaterThan(1), "test must produce multiple partitions");

        // And every key still reads back.
        for (int i = 0; i < count; i++)
        {
            Assert.That(HsstTestUtil.TryGet(data, Key(i), out byte[] value), Is.True, $"key {i}");
            Assert.That(value, Is.EqualTo(Val(i)), $"value {i}");
        }
    }

    // BucketCountFor sizes the table to exactly ceil(keyCount / 6) buckets — no power-of-two
    // rounding — so capacity holds every key and the load is at the ~75% target.
    [Test]
    public void BucketCount_Targets_Configured_Utilization()
    {
        Assert.That(HsstPartitionHashtable.TargetUtilizationPercent, Is.EqualTo(75));
        Assert.That(HsstPartitionHashtable.TargetKeysPerBucket, Is.EqualTo(6));
        Assert.That(HsstPartitionHashtable.WaysPerBucket, Is.EqualTo(8));

        foreach (int keyCount in new[] { 7, 48, 49, 137, 1000, 100_000 })
        {
            int buckets = HsstPartitionHashtable.BucketCountFor(keyCount);
            Assert.That(buckets, Is.EqualTo((keyCount + 5) / 6), $"bucket count for {keyCount} must be ceil(k/6), not a power of two");
            long capacity = (long)buckets * HsstPartitionHashtable.WaysPerBucket;
            Assert.That(capacity, Is.GreaterThanOrEqualTo(keyCount), $"capacity must hold all {keyCount} keys");
            Assert.That((double)keyCount / capacity, Is.LessThanOrEqualTo(0.75 + 1e-9), $"load for {keyCount} keys exceeds 75% target");
        }
    }

    // The struct-of-arrays bucket (8 tags then 8 u48 offsets) round-trips through TryInsert /
    // MatchMask / OffsetAt, the SIMD and scalar match masks agree, tag collisions surface all
    // matching ways, the u48 forward offset round-trips to its 256 TiB ceiling, and a 9th insert
    // (or an over-u48 offset) is dropped.
    [Test]
    public void Hashtable_Bucket_Codec_RoundTrips()
    {
        static ulong H(ushort tag) => (ulong)tag << 48; // bucketCount 1 ⇒ Lemire always picks bucket 0; Tag(h) = tag (≥1)
        const long MaxU48 = (1L << 48) - 1;

        Span<byte> bucket = stackalloc byte[HsstPartitionHashtable.BucketBytes];
        Assert.That(HsstPartitionHashtable.TryInsert(bucket, 1, H(5), 100), Is.True);     // way 0, tag 5
        Assert.That(HsstPartitionHashtable.TryInsert(bucket, 1, H(9), 200), Is.True);     // way 1, tag 9
        Assert.That(HsstPartitionHashtable.TryInsert(bucket, 1, H(5), MaxU48), Is.True);  // way 2, tag 5 (collision), max u48 offset

        // SIMD path agrees with the scalar reference.
        foreach (ushort probe in new ushort[] { 5, 9, 1234 })
            Assert.That(HsstPartitionHashtable.MatchMask(bucket, probe), Is.EqualTo(HsstPartitionHashtable.MatchMaskScalar(bucket, probe)), $"mask mismatch for {probe}");

        // tag 5 → ways 0 and 2; tag 9 → way 1; absent tag → no match.
        Assert.That(HsstPartitionHashtable.MatchMask(bucket, 5), Is.EqualTo(0b101u));
        Assert.That(HsstPartitionHashtable.MatchMask(bucket, 9), Is.EqualTo(0b010u));
        Assert.That(HsstPartitionHashtable.MatchMask(bucket, 1234), Is.EqualTo(0u));

        // Forward (u48) offsets read back, including the 256 TiB ceiling.
        Assert.That(HsstPartitionHashtable.OffsetAt(bucket, 0), Is.EqualTo(100L));
        Assert.That(HsstPartitionHashtable.OffsetAt(bucket, 1), Is.EqualTo(200L));
        Assert.That(HsstPartitionHashtable.OffsetAt(bucket, 2), Is.EqualTo(MaxU48));

        // Fill to 8 ways, then the 9th overflows (best-effort drop).
        for (ushort i = 0; i < 5; i++)
            Assert.That(HsstPartitionHashtable.TryInsert(bucket, 1, H((ushort)(20 + i)), 300 + i), Is.True);
        Assert.That(HsstPartitionHashtable.TryInsert(bucket, 1, H(99), 999), Is.False, "9th insert into a full bucket must overflow");

        // u48 offset guard: an offset beyond MaxOffset is rejected; the ceiling itself fits.
        Span<byte> b2 = stackalloc byte[HsstPartitionHashtable.BucketBytes];
        Assert.That(HsstPartitionHashtable.TryInsert(b2, 1, H(7), HsstPartitionHashtable.MaxOffset + 1L), Is.False, "offset > u48 must be rejected");
        Assert.That(HsstPartitionHashtable.TryInsert(b2, 1, H(7), HsstPartitionHashtable.MaxOffset), Is.True, "offset at the u48 ceiling fits");
    }

    // The key-after-value partitioned builder (the per-address layout) round-trips via streaming
    // BeginValueWrite/FinishValueWrite and Add: multi-partition and single+hashtable both → 0x0A
    // (a one-entry directory in the single case), single small → plain 0x01. Every key reads back
    // (hashtable hit + floor fallback), and enumeration matches the plain 0x01 build. (A mixed
    // Add/streaming build is not byte-identical to an all-Add build — a streamed value flushes any
    // pending leaf first — so only the trailer tag is asserted for the single-small case.)
    [TestCase(60, /*threshold*/ 300L, /*htMin*/ 0, (byte)0x0A)]   // multi-partition + hashtable
    [TestCase(60, 4L * 1024 * 1024, 0, (byte)0x0A)]               // single partition + hashtable → one-entry directory 0x0A
    [TestCase(5, 4L * 1024 * 1024, 4096, (byte)0x01)]             // single small → plain 0x01
    public void KeyAfterValue_Partitioned_Streaming_RoundTrips(int count, long thresholdBytes, int htMinBytes, byte expectedTail)
    {
        HsstBTreeOptions opts = new() { PartitionThresholdBytes = thresholdBytes, HashtableMinBytes = htMinBytes };
        byte[] data = BuildPartitionedKeyAfterValue(count, opts);

        Assert.That(data[^1], Is.EqualTo(expectedTail), "key-after-value trailer must match the partition layout");

        // Hashtable-hit path: every key resolves with the right value.
        for (int i = 0; i < count; i++)
        {
            Assert.That(HsstTestUtil.TryGet(data, Key(i), out byte[] value), Is.True, $"key {i} not found");
            Assert.That(value, Is.EqualTo(Val(i)), $"wrong value for key {i}");
        }

        // Absent + floor (inner-B-tree fallback, skips the hashtable).
        Assert.That(HsstTestUtil.TryGet(data, Key(count + 500), out _), Is.False);
        Assert.That(HsstTestUtil.TryGetFloor(data, Key(count + 500), out byte[] floorVal), Is.True);
        Assert.That(floorVal, Is.EqualTo(Val(count - 1)));

        // Enumeration yields the same key-sorted sequence as the plain 0x01 build.
        List<(byte[] key, byte[] val)> partitioned = Enumerate(data);
        List<(byte[] key, byte[] val)> plain = Enumerate(BuildPlainKeyAfterValue(count));
        Assert.That(partitioned.Count, Is.EqualTo(count));
        for (int i = 0; i < count; i++)
        {
            Assert.That(partitioned[i].key, Is.EqualTo(plain[i].key), $"key mismatch at {i}");
            Assert.That(partitioned[i].val, Is.EqualTo(plain[i].val), $"value mismatch at {i}");
        }
    }

    // A single streamed key-after-value entry that warrants a hashtable (htMin 0) → one-entry
    // directory 0x0A, with a large value (mirrors the per-address column: one address, big nested
    // value). The lone entry must read back via the hashtable probe and the floor/inner-tree fallback.
    [TestCase(1)]
    [TestCase(4000)]
    public void KeyAfterValue_Single_Streamed_Entry_With_Hashtable_RoundTrips(int valueLen)
    {
        byte[] key = Key(7);
        byte[] val = new byte[valueLen];
        for (int j = 0; j < val.Length; j++) val[j] = (byte)(j * 31 + 1);

        using PooledByteBufferWriter pooled = new(1 << 20);
        using HsstPartitionedBTreeBuilderBuffersContainer buffers = new();
        HsstPartitionedBTreeBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> b =
            new(ref pooled.GetWriter(), ref buffers.Buffers, KeyLength, new HsstBTreeOptions { HashtableMinBytes = 0 }, keyFirst: false);
        ref PooledByteBufferWriter.Writer w = ref b.BeginValueWrite();
        val.CopyTo(w.GetSpan(val.Length));
        w.Advance(val.Length);
        b.FinishValueWrite(key, val.Length);
        b.Build();
        byte[] data = pooled.WrittenSpan.ToArray();
        b.Dispose();

        Assert.That(data[^1], Is.EqualTo((byte)IndexType.PartitionedBTree), "single streamed entry + hashtable must be a one-entry directory 0x0A");
        Assert.That(HsstTestUtil.TryGet(data, key, out byte[] got), Is.True, "hashtable hit must find the lone entry");
        Assert.That(got, Is.EqualTo(val), "value mismatch");
        // Floor of a higher key skips the hashtable → inner-tree fallback must still resolve it.
        Assert.That(HsstTestUtil.TryGetFloor(data, Key(99), out byte[] floor), Is.True, "floor fallback must find the lone entry");
        Assert.That(floor, Is.EqualTo(val), "floor value mismatch");
    }

    // Shared assertion: every key 0..count-1 must resolve by point lookup (hashtable hit or
    // per-partition B-tree fallback) AND appear, in sorted order with the right value, in the
    // forward enumeration.
    private static void AssertEveryKvReadableAndEnumerable(byte[] data, int count)
    {
        for (int i = 0; i < count; i++)
        {
            Assert.That(HsstTestUtil.TryGet(data, Key(i), out byte[] value), Is.True, $"point lookup missed key {i}");
            Assert.That(value, Is.EqualTo(Val(i)), $"point lookup wrong value for key {i}");
        }

        List<(byte[] key, byte[] val)> got = Enumerate(data);
        Assert.That(got.Count, Is.EqualTo(count), "enumerator must yield every key exactly once");
        for (int i = 0; i < count; i++)
        {
            Assert.That(got[i].key, Is.EqualTo(Key(i)), $"enumerator order wrong at {i}");
            Assert.That(got[i].val, Is.EqualTo(Val(i)), $"enumerator value wrong at {i}");
        }
    }

    // SINGLE partition + hashtable — a one-entry directory 0x08 (key-first) / 0x0A (key-after-value).
    // PartitionThresholdBytes is unbounded so all keys land in one partition; HashtableMinBytes=0
    // forces the hashtable. Every KV reads back by point lookup and via the enumerator, across small
    // counts and a count that spans several hashtable buckets.
    [TestCase(true, 1)]
    [TestCase(true, 6)]
    [TestCase(true, 7)]
    [TestCase(true, 80)]
    [TestCase(false, 1)]
    [TestCase(false, 6)]
    [TestCase(false, 7)]
    [TestCase(false, 80)]
    public void Hashtable_SinglePartition_EveryKv_ReadBack_And_Enumerated(bool keyFirst, int count)
    {
        HsstBTreeOptions opts = new() { HashtableMinBytes = 0, PartitionThresholdBytes = long.MaxValue };
        byte[] data = keyFirst ? BuildPartitioned(count, opts) : BuildPartitionedKeyAfterValue(count, opts);

        Assert.That(data[^1], Is.EqualTo((byte)(keyFirst
            ? IndexType.PartitionedBTreeKeyFirst
            : IndexType.PartitionedBTree)), "single partition + hashtable → one-entry directory expected");

        AssertEveryKvReadableAndEnumerable(data, count);
    }

    // MULTI partition — 0x08 (key-first) / 0x0A (key-after-value). A small per-partition threshold
    // (~10 keys/partition) forces ≥2 partitions, so reads exercise the directory floor-seek + each
    // partition's hashtable, and enumeration drains partitions in directory order. Every KV reads
    // back by point lookup and via the enumerator.
    [TestCase(true, 25)]
    [TestCase(true, 80)]
    [TestCase(true, 200)]
    [TestCase(false, 25)]
    [TestCase(false, 80)]
    [TestCase(false, 200)]
    public void Hashtable_MultiPartition_EveryKv_ReadBack_And_Enumerated(bool keyFirst, int count)
    {
        HsstBTreeOptions opts = new() { HashtableMinBytes = 0, PartitionThresholdBytes = 10 * KeyLength };
        byte[] data = keyFirst ? BuildPartitioned(count, opts) : BuildPartitionedKeyAfterValue(count, opts);

        Assert.That(data[^1], Is.EqualTo((byte)(keyFirst
            ? IndexType.PartitionedBTreeKeyFirst
            : IndexType.PartitionedBTree)), "multi-partition expected");

        // The 0x08/0x0A blob's top-level tree IS the directory (always key-first); count its
        // entries = partition count and require more than one so this really is multi-partition.
        SpanByteReader reader = new(data);
        HsstBTreeEnumerator<SpanByteReader, NoOpPin> dir = new(in reader, new Bound(0, data.Length), keyFirst: true);
        int partitions = 0;
        while (dir.MoveNext(in reader)) partitions++;
        Assert.That(partitions, Is.GreaterThan(1), "test must produce multiple partitions");

        AssertEveryKvReadableAndEnumerable(data, count);
    }

    // Force a MULTI-LEVEL directory B-tree: with PartitionThresholdBytes == KeyLength every entry
    // is its own partition, so 700 entries ⇒ 700 directory entries ⇒ the directory leaf splits and
    // gains intermediate nodes. Exercises the directory floor-seek across B-tree levels (every
    // earlier partitioned test had a single-leaf directory). Run for 0x08 (key-first) and 0x0A
    // (key-after-value); every key must resolve (hashtable hit and directory→inner-tree fallback),
    // floor works, and absent keys return false.
    [TestCase(true)]
    [TestCase(false)]
    public void MultiLevel_Directory_AllKeys_RoundTrip(bool keyFirst)
    {
        const int count = 700;
        HsstBTreeOptions opts = new() { PartitionThresholdBytes = KeyLength, HashtableMinBytes = 0 };
        byte[] data = keyFirst ? BuildPartitioned(count, opts) : BuildPartitionedKeyAfterValue(count, opts);

        Assert.That(data[^1], Is.EqualTo((byte)(keyFirst
            ? IndexType.PartitionedBTreeKeyFirst : IndexType.PartitionedBTree)), "multi-partition expected");

        // The blob's top-level tree IS the directory (always key-first); count its entries =
        // partition count, and require it to exceed one leaf so the directory is multi-level.
        SpanByteReader reader = new(data);
        HsstBTreeEnumerator<SpanByteReader, NoOpPin> dir = new(in reader, new Bound(0, data.Length), keyFirst: true);
        int partitions = 0;
        while (dir.MoveNext(in reader)) partitions++;
        Assert.That(partitions, Is.EqualTo(count), "each entry should be its own partition");
        Assert.That(partitions, Is.GreaterThan(HsstBTreeOptions.DefaultMaxLeafEntries), "directory must be multi-level (intermediate nodes)");

        // Every key resolves with the correct value (across the multi-level directory).
        for (int i = 0; i < count; i++)
        {
            Assert.That(HsstTestUtil.TryGet(data, Key(i), out byte[] value), Is.True, $"key {i} not found (keyFirst={keyFirst})");
            Assert.That(value, Is.EqualTo(Val(i)), $"value {i} (keyFirst={keyFirst})");
        }
        // Floor of an odd-perturbed past-the-end key lands on the last key; a below-range key is absent.
        Assert.That(HsstTestUtil.TryGetFloor(data, Key(count + 5000), out byte[] floorVal), Is.True);
        Assert.That(floorVal, Is.EqualTo(Val(count - 1)));
        Assert.That(HsstTestUtil.TryGet(data, Key(count + 5000), out _), Is.False, "above-range key absent");

        // ENUMERATION must yield every key in sorted order with the right value — this is what the
        // compaction N-way merge relies on. A multi-level directory walked out of order / with gaps
        // would corrupt the merge (stale/lost entries).
        List<(byte[] key, byte[] val)> got = Enumerate(data);
        Assert.That(got.Count, Is.EqualTo(count), "enumeration must yield every key exactly once");
        for (int i = 0; i < count; i++)
        {
            Assert.That(got[i].key, Is.EqualTo(Key(i)), $"enumeration order wrong at {i} (keyFirst={keyFirst})");
            Assert.That(got[i].val, Is.EqualTo(Val(i)), $"enumeration value wrong at {i} (keyFirst={keyFirst})");
        }
    }

    // Force a bucket overflow: pick 12 strictly-ascending keys that ALL hash to bucket 0 (for
    // the bucketCount the builder derives from 12 keys), so bucket 0 holds 12 keys but only 8
    // ways. The 4 that overflow are dropped from the hashtable and can only be found via the
    // inner-B-tree fallback. Every key must still read back with the right value — for both the
    // key-first (0x08) and key-after-value (0x0A) entry layouts (the latter exercises the
    // DecodeEntry/TrySeekFromRoot keyFirst:false fallback that a non-overflowing table never hits).
    [TestCase(true)]
    [TestCase(false)]
    public void BucketOverflow_OverflowedKeys_FoundViaBTreeFallback(bool keyFirst)
    {
        const int count = 12;
        int bucketCount = HsstPartitionHashtable.BucketCountFor(count); // ceil(12/6) = 2
        Assert.That(bucketCount, Is.GreaterThan(0));

        // Collect ascending ids whose key maps to bucket 0 → all collide in one bucket.
        List<int> ids = [];
        for (int i = 0; ids.Count < count && i < 1_000_000; i++)
        {
            ulong h = HsstPartitionHashtable.Hash(Key(i));
            if (HsstPartitionHashtable.BucketIndex(h, bucketCount) == 0)
                ids.Add(i);
        }
        Assert.That(ids.Count, Is.EqualTo(count), "could not gather enough colliding keys");

        using PooledByteBufferWriter pooled = new(1 << 20);
        using HsstPartitionedBTreeBuilderBuffersContainer buffers = new();
        HsstPartitionedBTreeBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> b =
            new(ref pooled.GetWriter(), ref buffers.Buffers, KeyLength, new HsstBTreeOptions { HashtableMinBytes = 0 }, keyFirst: keyFirst);
        try
        {
            foreach (int i in ids)
            {
                if (keyFirst)
                {
                    b.Add(Key(i), Val(i));
                }
                else
                {
                    byte[] v = Val(i);
                    ref PooledByteBufferWriter.Writer w = ref b.BeginValueWrite();
                    v.CopyTo(w.GetSpan(v.Length));
                    w.Advance(v.Length);
                    b.FinishValueWrite(Key(i), v.Length);
                }
            }
            b.Build();
        }
        finally { b.Dispose(); }
        byte[] data = pooled.WrittenSpan.ToArray();

        Assert.That(data[^1], Is.EqualTo((byte)(keyFirst
            ? IndexType.PartitionedBTreeKeyFirst
            : IndexType.PartitionedBTree)), "single partition + hashtable → one-entry directory expected");

        // All 12 keys must resolve — the 4 that overflowed bucket 0 only via the B-tree fallback.
        foreach (int i in ids)
        {
            Assert.That(HsstTestUtil.TryGet(data, Key(i), out byte[] value), Is.True, $"key {i} not found (overflow fallback, keyFirst={keyFirst})");
            Assert.That(value, Is.EqualTo(Val(i)), $"wrong value for key {i} (keyFirst={keyFirst})");
        }
    }

    // Bucket selection is Lemire's multiply-shift (no integer division/modulo) and works for
    // any non-power-of-two bucket count, always landing in [0, numBuckets).
    [Test]
    public void BucketIndex_Uses_Lemire_Reduction()
    {
        foreach (int numBuckets in new[] { 1, 3, 5, 23, 1000, 65537 })
        {
            for (int s = 0; s < 64; s++)
            {
                ulong h = HsstPartitionHashtable.Hash(Key(s));
                int idx = HsstPartitionHashtable.BucketIndex(h, numBuckets);
                Assert.That(idx, Is.EqualTo((int)(((ulong)(uint)h * (ulong)numBuckets) >> 32)), "BucketIndex must equal the Lemire reduction of the low 32 bits");
                Assert.That(idx, Is.InRange(0, numBuckets - 1));
            }
        }
    }
}
