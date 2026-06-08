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
    [TestCase(300L, int.MaxValue, 50, (byte)0x08)] // over upper threshold, no hashtable → still multi-partition 0x08
    [TestCase(4L * 1024 * 1024, 4096, 5, (byte)0x07)] // under both thresholds → single partition degrades to 0x07
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

    // The bucket index must be a power-of-two mask of the hash, never an integer division/modulo.
    [Test]
    public void BucketIndex_Uses_PowerOfTwo_Mask()
    {
        foreach (int log2 in new[] { 1, 2, 4, 8, 16 })
        {
            int numBuckets = 1 << log2;
            for (int s = 0; s < 64; s++)
            {
                ulong h = HsstPartitionHashtable.Hash(Key(s));
                int idx = HsstPartitionHashtable.BucketIndex(h, log2);
                Assert.That(idx, Is.EqualTo((int)(h & ((1UL << log2) - 1))), "BucketIndex must equal hash & (NumBuckets-1)");
                Assert.That(idx, Is.InRange(0, numBuckets - 1));
            }
        }
    }
}
