// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.Hsst.BTree;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Hsst;

/// <summary>
/// End-to-end tests for <see cref="HsstPartitionedBTreeBuilder{TWriter,TReader,TPin}"/>: the
/// hashtable-accelerated partitioned B-tree read back through the standard
/// <see cref="HsstReader{TReader,TPin}"/> dispatch. Stresses the paths the partitioned design adds
/// — single small (degraded) blob, single hashtabled partition, forced multi-partition with a
/// directory, multi-level directory, floor seek and enumeration across partition boundaries, and
/// inner-root common-prefix carriage — i.e. exactly where a base/offset bug would surface.
/// </summary>
[TestFixture]
public class HsstPartitionedBTreeTests
{
    private const int KeyLength = 20;

    private static byte[] Key(int i, int keyLength = KeyLength)
    {
        byte[] k = new byte[keyLength];
        BinaryPrimitives.WriteInt32BigEndian(k.AsSpan(keyLength - 4), i);
        return k;
    }

    private static byte[] Val(int i)
    {
        byte[] v = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(v, (0x5AL << 56) | (uint)i);
        return v;
    }

    private static List<(byte[] key, byte[] val)> Kvs(int count, int keyLength = KeyLength)
    {
        List<(byte[], byte[])> list = new(count);
        for (int i = 0; i < count; i++) list.Add((Key(i, keyLength), Val(i)));
        return list;
    }

    private static byte[] BuildPartitioned(IReadOnlyList<(byte[] key, byte[] val)> kvs, int keyLength, bool keyFirst, HsstBTreeOptions options)
    {
        using PooledByteBufferWriter pooled = new(64 * 1024 * 1024);
        using HsstPartitionedBTreeBuilderBuffersContainer buffers = new();
        HsstPartitionedBTreeBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder =
            new(ref pooled.GetWriter(), ref buffers.Buffers, keyLength, options, keyFirst: keyFirst);
        try
        {
            foreach ((byte[] key, byte[] val) in kvs)
            {
                if (keyFirst)
                {
                    builder.Add(key, val);
                }
                else
                {
                    ref PooledByteBufferWriter.Writer w = ref builder.BeginValueWrite();
                    val.CopyTo(w.GetSpan(val.Length));
                    w.Advance(val.Length);
                    builder.FinishValueWrite(key, val.Length);
                }
            }
            builder.Build();
            return pooled.WrittenSpan.ToArray();
        }
        finally
        {
            builder.Dispose();
        }
    }

    // Tiny threshold => a fresh partition every few keys (multi-partition with a directory).
    private static HsstBTreeOptions ForceMultiPartition => new() { PartitionThresholdBytes = 64, HashtableMinKeys = 1 };
    // Large threshold => one partition; HashtableMinKeys 1 forces a single hashtabled partition.
    private static HsstBTreeOptions SingleHashtabled => new() { PartitionThresholdBytes = long.MaxValue, HashtableMinKeys = 1 };
    // Large threshold + default 1024 gate => small blobs degrade to a plain B-tree.
    private static HsstBTreeOptions DefaultGate => new() { PartitionThresholdBytes = long.MaxValue };

    [TestCase(1, true)]
    [TestCase(2, true)]
    [TestCase(7, true)]
    [TestCase(50, true)]
    [TestCase(200, true)]
    [TestCase(1024, true)]
    [TestCase(1, false)]
    [TestCase(7, false)]
    [TestCase(50, false)]
    [TestCase(200, false)]
    [TestCase(1024, false)]
    public void MultiPartition_RoundTrip_AllKeys_And_Enumerates(int count, bool keyFirst)
    {
        List<(byte[] key, byte[] val)> kvs = Kvs(count);
        byte[] data = BuildPartitioned(kvs, KeyLength, keyFirst, ForceMultiPartition);

        for (int i = 0; i < count; i++)
        {
            Assert.That(HsstTestUtil.TryGet(data, kvs[i].key, out byte[] got), Is.True, $"missing key {i}");
            Assert.That(got, Is.EqualTo(kvs[i].val), $"value mismatch key {i}");
        }

        AssertEnumeratesAll(data, kvs, keyFirst);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void ManyKeysPerPartition_Overflow_RoundTrip(bool keyFirst)
    {
        // ~69 keys/partition (2048 / 30-byte keys) overflows some 8-way buckets, so some keys are
        // dropped from the hashtable and must resolve via the inner-B-tree fallback on exact lookup.
        const int Kl = 30;
        List<(byte[] key, byte[] val)> kvs = Kvs(1500, Kl);
        byte[] data = BuildPartitioned(kvs, Kl, keyFirst, new HsstBTreeOptions { PartitionThresholdBytes = 2048, HashtableMinKeys = 1 });
        List<int> bad = [];
        for (int i = 0; i < kvs.Count; i++)
            if (!HsstTestUtil.TryGet(data, kvs[i].key, out byte[] got) || !got.AsSpan().SequenceEqual(kvs[i].val))
                bad.Add(i);
        Assert.That(bad, Is.Empty, $"missing/wrong keys: [{string.Join(",", bad)}]");
    }

    [TestCase(1024, true)]
    [TestCase(2000, true)]
    [TestCase(1024, false)]
    [TestCase(2000, false)]
    public void SingleHashtabled_Partition_RoundTrip_And_Enumerates(int count, bool keyFirst)
    {
        List<(byte[] key, byte[] val)> kvs = Kvs(count);
        byte[] data = BuildPartitioned(kvs, KeyLength, keyFirst, SingleHashtabled);

        for (int i = 0; i < count; i++)
        {
            Assert.That(HsstTestUtil.TryGet(data, kvs[i].key, out byte[] got), Is.True, $"missing key {i}");
            Assert.That(got, Is.EqualTo(kvs[i].val));
        }
        AssertEnumeratesAll(data, kvs, keyFirst);
    }

    [Test]
    public void MultiLevel_Directory_RoundTrip([Values(true, false)] bool keyFirst)
    {
        // One key per partition (threshold below one key's bytes) over 700 keys forces a directory
        // tall enough to need intermediate levels.
        List<(byte[] key, byte[] val)> kvs = Kvs(700);
        byte[] data = BuildPartitioned(kvs, KeyLength, keyFirst, new HsstBTreeOptions { PartitionThresholdBytes = 1, HashtableMinKeys = 1 });
        for (int i = 0; i < kvs.Count; i++)
        {
            Assert.That(HsstTestUtil.TryGet(data, kvs[i].key, out byte[] got), Is.True, $"missing key {i}");
            Assert.That(got, Is.EqualTo(kvs[i].val));
        }
        AssertEnumeratesAll(data, kvs, keyFirst);
    }

    [Test]
    public void Floor_Seek_Across_Partitions([Values(true, false)] bool keyFirst)
    {
        // Even-numbered keys only; odd probes must floor to the previous even key, including across
        // partition boundaries (the inner B-tree of the routed partition resolves the floor).
        List<(byte[] key, byte[] val)> kvs = [];
        for (int i = 0; i < 400; i += 2) kvs.Add((Key(i), Val(i)));
        byte[] data = BuildPartitioned(kvs, KeyLength, keyFirst, ForceMultiPartition);

        for (int probe = 1; probe < 400; probe += 2)
        {
            Assert.That(HsstTestUtil.TryGetFloor(data, Key(probe), out byte[] got), Is.True, $"floor missing for {probe}");
            Assert.That(got, Is.EqualTo(Val(probe - 1)), $"floor value for {probe}");
        }
    }

    [Test]
    public void Absent_Keys_Return_False([Values(true, false)] bool keyFirst)
    {
        List<(byte[] key, byte[] val)> kvs = Kvs(300);
        byte[] data = BuildPartitioned(kvs, KeyLength, keyFirst, ForceMultiPartition);

        Assert.That(HsstTestUtil.TryGet(data, Key(10_000), out _), Is.False, "above range");
        byte[] perturbed = (byte[])kvs[100].key.Clone();
        perturbed[0] ^= 0xFF;
        Assert.That(HsstTestUtil.TryGet(data, perturbed, out _), Is.False, "perturbed key");
    }

    [Test]
    public void InnerRoot_With_Common_Prefix_RoundTrips([Values(true, false)] bool keyFirst)
    {
        // All keys share a long common prefix, so each partition's inner B-tree root elides a
        // non-zero CommonPrefixLen — exercising the prefix-in-node-record carriage.
        List<(byte[] key, byte[] val)> kvs = [];
        for (int i = 0; i < 300; i++)
        {
            byte[] k = new byte[KeyLength];
            for (int b = 0; b < 16; b++) k[b] = 0xAB; // shared 16-byte prefix
            BinaryPrimitives.WriteInt32BigEndian(k.AsSpan(16), i);
            kvs.Add((k, Val(i)));
        }
        byte[] data = BuildPartitioned(kvs, KeyLength, keyFirst, ForceMultiPartition);
        for (int i = 0; i < kvs.Count; i++)
        {
            Assert.That(HsstTestUtil.TryGet(data, kvs[i].key, out byte[] got), Is.True, $"missing key {i}");
            Assert.That(got, Is.EqualTo(kvs[i].val));
        }
        AssertEnumeratesAll(data, kvs, keyFirst);
    }

    [TestCase(5, true)]
    [TestCase(500, true)]
    [TestCase(1023, true)]
    [TestCase(5, false)]
    [TestCase(1023, false)]
    public void Below_Gate_Degrades_To_ByteIdentical_Plain_BTree(int count, bool keyFirst)
    {
        List<(byte[] key, byte[] val)> kvs = Kvs(count);
        HsstBTreeOptions options = DefaultGate; // HashtableMinKeys = 1024 default
        byte[] partitioned = BuildPartitioned(kvs, KeyLength, keyFirst, options);
        byte[] standalone = BuildStandalone(kvs, KeyLength, keyFirst, options);
        Assert.That(partitioned, Is.EqualTo(standalone), "sub-gate partitioned blob must be byte-identical to a plain B-tree");
    }

    private static byte[] BuildStandalone(IReadOnlyList<(byte[] key, byte[] val)> kvs, int keyLength, bool keyFirst, HsstBTreeOptions options)
    {
        using PooledByteBufferWriter pooled = new(64 * 1024 * 1024);
        using HsstBTreeBuilderBuffersContainer buffers = new(kvs.Count);
        HsstBTreeBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder =
            new(ref pooled.GetWriter(), ref buffers.Buffers, keyLength, options, kvs.Count, keyFirst);
        try
        {
            foreach ((byte[] key, byte[] val) in kvs)
            {
                if (keyFirst)
                {
                    builder.Add(key, val);
                }
                else
                {
                    ref PooledByteBufferWriter.Writer w = ref builder.BeginValueWrite();
                    val.CopyTo(w.GetSpan(val.Length));
                    w.Advance(val.Length);
                    builder.FinishValueWrite(key, val.Length);
                }
            }
            builder.Build();
            return pooled.WrittenSpan.ToArray();
        }
        finally
        {
            builder.Dispose();
        }
    }

    private static void AssertEnumeratesAll(byte[] data, IReadOnlyList<(byte[] key, byte[] val)> kvs, bool keyFirst)
    {
        SpanByteReader reader = new(data);
        using HsstRefEnumerator<SpanByteReader, NoOpPin> e = new(in reader, new Bound(0, data.Length));
        Span<byte> keyBuf = new byte[KeyLength];
        int i = 0;
        while (e.MoveNext())
        {
            Assert.That(i, Is.LessThan(kvs.Count), "enumerator yielded more entries than added");
            ReadOnlySpan<byte> k = e.CopyCurrentLogicalKey(keyBuf);
            Bound vb = e.Current.ValueBound;
            ReadOnlySpan<byte> v = data.AsSpan((int)vb.Offset, (int)vb.Length);
            Assert.That(k.SequenceEqual(kvs[i].key), Is.True, $"enumerated key {i}");
            Assert.That(v.SequenceEqual(kvs[i].val), Is.True, $"enumerated value {i}");
            i++;
        }
        Assert.That(i, Is.EqualTo(kvs.Count), "enumerator entry count");
    }
}
