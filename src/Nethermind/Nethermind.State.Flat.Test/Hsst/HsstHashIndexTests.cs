// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Nethermind.State.Flat.Hsst;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class HsstHashIndexTests
{
    private static bool TryGet(ReadOnlySpan<byte> data, scoped ReadOnlySpan<byte> key, out byte[] value)
    {
        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        if (!r.TrySeek(key, out _)) { value = []; return false; }
        Bound b = r.GetBound();
        value = data.Slice((int)b.Offset, b.Length).ToArray();
        return true;
    }

    private static bool TryGetFloor(ReadOnlySpan<byte> data, scoped ReadOnlySpan<byte> key, out byte[] value)
    {
        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        if (!r.TrySeekFloor(key, out _)) { value = []; return false; }
        Bound b = r.GetBound();
        value = data.Slice((int)b.Offset, b.Length).ToArray();
        return true;
    }

    private static List<(byte[] Key, byte[] Value)> Materialize(ReadOnlySpan<byte> data)
    {
        List<(byte[], byte[])> entries = [];
        SpanByteReader reader = new(data);
        using HsstEnumerator<SpanByteReader, NoOpPin> e = new(in reader, new Bound(0, data.Length));
        while (e.MoveNext())
        {
            Bound kb = e.Current.KeyBound;
            Bound vb = e.Current.ValueBound;
            entries.Add((data.Slice((int)kb.Offset, kb.Length).ToArray(), data.Slice((int)vb.Offset, vb.Length).ToArray()));
        }
        return entries;
    }

    private static (byte[][] Keys, byte[][] Values) MakeSortedKeys(int count, int seed = 1)
    {
        Random rng = new(seed);
        HashSet<string> seen = new();
        List<byte[]> ks = new(count);
        while (ks.Count < count)
        {
            byte[] k = new byte[16];
            rng.NextBytes(k);
            if (seen.Add(Convert.ToHexString(k))) ks.Add(k);
        }
        ks.Sort((a, b) => a.AsSpan().SequenceCompareTo(b));
        byte[][] vs = ks.Select((_, i) =>
        {
            byte[] v = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(v, i);
            BinaryPrimitives.WriteInt32LittleEndian(v.AsSpan(4), i * 31);
            return v;
        }).ToArray();
        return (ks.ToArray(), vs);
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(7)]
    [TestCase(256)]
    [TestCase(5000)]
    public void HashIndex_RoundTrip_MatchesPlainBTree(int count)
    {
        (byte[][] keys, byte[][] values) = MakeSortedKeys(count);

        byte[] withHash = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer> b) =>
        {
            for (int i = 0; i < count; i++) b.Add(keys[i], values[i]);
        }, useHashIndex: true);

        byte[] plain = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer> b) =>
        {
            for (int i = 0; i < count; i++) b.Add(keys[i], values[i]);
        });

        // Trailing tag is 0x03 for hash-index variant.
        Assert.That(withHash[^1], Is.EqualTo((byte)IndexType.BTreeHashIndex));
        Assert.That(plain[^1], Is.EqualTo((byte)IndexType.BTree));

        // Every present key resolves with same value via either format.
        for (int i = 0; i < count; i++)
        {
            Assert.That(TryGet(withHash, keys[i], out byte[] gotHash), Is.True, $"hash idx: missing key {i}");
            Assert.That(gotHash, Is.EqualTo(values[i]));

            Assert.That(TryGet(plain, keys[i], out byte[] gotPlain), Is.True);
            Assert.That(gotPlain, Is.EqualTo(values[i]));
        }

        // Absent-key probes return the same answer.
        Random rng = new(99);
        for (int t = 0; t < 32; t++)
        {
            byte[] missing = new byte[16];
            rng.NextBytes(missing);
            // skip if it accidentally hits
            if (Array.BinarySearch(keys, missing, Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b))) >= 0) continue;

            Assert.That(TryGet(withHash, missing, out _), Is.False);
            Assert.That(TryGet(plain, missing, out _), Is.False);

            bool hashFloor = TryGetFloor(withHash, missing, out byte[] hashFloorVal);
            bool plainFloor = TryGetFloor(plain, missing, out byte[] plainFloorVal);
            Assert.That(hashFloor, Is.EqualTo(plainFloor));
            if (hashFloor) Assert.That(hashFloorVal, Is.EqualTo(plainFloorVal));
        }
    }

    [TestCase(1)]
    [TestCase(7)]
    [TestCase(256)]
    [TestCase(5000)]
    public void HashIndex_Enumerator_MatchesPlainBTree(int count)
    {
        (byte[][] keys, byte[][] values) = MakeSortedKeys(count, seed: 42);

        byte[] withHash = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer> b) =>
        {
            for (int i = 0; i < count; i++) b.Add(keys[i], values[i]);
        }, useHashIndex: true);
        byte[] plain = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer> b) =>
        {
            for (int i = 0; i < count; i++) b.Add(keys[i], values[i]);
        });

        List<(byte[] K, byte[] V)> a = Materialize(withHash);
        List<(byte[] K, byte[] V)> b2 = Materialize(plain);

        Assert.That(a.Count, Is.EqualTo(count));
        Assert.That(b2.Count, Is.EqualTo(count));
        for (int i = 0; i < count; i++)
        {
            Assert.That(a[i].K, Is.EqualTo(b2[i].K));
            Assert.That(a[i].V, Is.EqualTo(b2[i].V));
            Assert.That(a[i].K, Is.EqualTo(keys[i]));
        }
    }

    [Test]
    public void HashIndex_TableSize_MatchesTargetUtilization()
    {
        // 100 entries at 0.75 utilization -> ceil(100/0.75) = 134. With Lemire's reduction
        // the bucket count is no longer rounded up to a power of two.
        const int count = 100;
        (byte[][] keys, byte[][] values) = MakeSortedKeys(count);

        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer> b) =>
        {
            for (int i = 0; i < count; i++) b.Add(keys[i], values[i]);
        }, useHashIndex: true, hashIndexTargetUtilization: 0.75);

        Assert.That(data[^1], Is.EqualTo((byte)IndexType.BTreeHashIndex));
        // TableSize is the 4-byte little-endian field immediately before IndexType.
        uint tableSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(data.Length - 5, 4));
        Assert.That(tableSize, Is.EqualTo(134u));
    }

    [Test]
    public void HashIndex_EmptyHsst_FallsBackToPlainBTree()
    {
        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer> _) => { },
            useHashIndex: true);

        // Empty HSST with hash index requested still emits BTree (no benefit, ambiguous sentinel).
        Assert.That(data[^1], Is.EqualTo((byte)IndexType.BTree));
        Assert.That(TryGet(data, "anything"u8, out _), Is.False);
    }

    [Test]
    public void HashIndex_Collision_FallsThroughToBTree()
    {
        // Force collisions by oversaturating: target=1.0 makes table = next pow2 ≥ N.
        // With many entries some hash slots will collide, the reader must still
        // resolve them via the b-tree fallback.
        (byte[][] keys, byte[][] values) = MakeSortedKeys(2000, seed: 7);

        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer> b) =>
        {
            for (int i = 0; i < keys.Length; i++) b.Add(keys[i], values[i]);
        }, useHashIndex: true, hashIndexTargetUtilization: 1.0);

        // Every key still resolves; the test verifies fallback path correctness.
        for (int i = 0; i < keys.Length; i++)
        {
            Assert.That(TryGet(data, keys[i], out byte[] got), Is.True, $"missing key {i}");
            Assert.That(got, Is.EqualTo(values[i]));
        }
    }
}
