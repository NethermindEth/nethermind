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
public class HsstNodeHashIndexTests
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
    public void NodeHashIndex_RoundTrip_MatchesPlainBTree(int count)
    {
        (byte[][] keys, byte[][] values) = MakeSortedKeys(count);

        byte[] withNodeHash = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer> b) =>
        {
            for (int i = 0; i < count; i++) b.Add(keys[i], values[i]);
        }, useNodeHashIndex: true);

        byte[] plain = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer> b) =>
        {
            for (int i = 0; i < count; i++) b.Add(keys[i], values[i]);
        });

        Assert.That(withNodeHash[^1], Is.EqualTo((byte)IndexType.BTreeNodeHashIndex));
        Assert.That(plain[^1], Is.EqualTo((byte)IndexType.BTree));

        for (int i = 0; i < count; i++)
        {
            Assert.That(TryGet(withNodeHash, keys[i], out byte[] gotHash), Is.True, $"node hash idx: missing key {i}");
            Assert.That(gotHash, Is.EqualTo(values[i]));

            Assert.That(TryGet(plain, keys[i], out byte[] gotPlain), Is.True);
            Assert.That(gotPlain, Is.EqualTo(values[i]));
        }

        Random rng = new(99);
        for (int t = 0; t < 32; t++)
        {
            byte[] missing = new byte[16];
            rng.NextBytes(missing);
            if (Array.BinarySearch(keys, missing, Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b))) >= 0) continue;

            Assert.That(TryGet(withNodeHash, missing, out _), Is.False);
            Assert.That(TryGet(plain, missing, out _), Is.False);

            bool nhFloor = TryGetFloor(withNodeHash, missing, out byte[] nhFloorVal);
            bool plainFloor = TryGetFloor(plain, missing, out byte[] plainFloorVal);
            Assert.That(nhFloor, Is.EqualTo(plainFloor));
            if (nhFloor) Assert.That(nhFloorVal, Is.EqualTo(plainFloorVal));
        }
    }

    [TestCase(1)]
    [TestCase(7)]
    [TestCase(256)]
    [TestCase(5000)]
    public void NodeHashIndex_Inline_RoundTrip_MatchesPlainInline(int count)
    {
        (byte[][] keys, byte[][] values) = MakeSortedKeys(count, seed: 11);

        byte[] withNodeHash = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer> b) =>
        {
            for (int i = 0; i < count; i++) b.Add(keys[i], values[i]);
        }, inlineValues: true, useNodeHashIndex: true);

        byte[] plain = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer> b) =>
        {
            for (int i = 0; i < count; i++) b.Add(keys[i], values[i]);
        }, inlineValues: true);

        Assert.That(withNodeHash[^1], Is.EqualTo((byte)IndexType.BTreeNodeHashIndexInlineValue));
        Assert.That(plain[^1], Is.EqualTo((byte)IndexType.BTreeInlineValue));

        for (int i = 0; i < count; i++)
        {
            Assert.That(TryGet(withNodeHash, keys[i], out byte[] got), Is.True, $"inline node hash idx: missing key {i}");
            Assert.That(got, Is.EqualTo(values[i]));
        }

        // Enumerator parity.
        List<(byte[] K, byte[] V)> a = Materialize(withNodeHash);
        List<(byte[] K, byte[] V)> b2 = Materialize(plain);
        Assert.That(a.Count, Is.EqualTo(count));
        for (int i = 0; i < count; i++)
        {
            Assert.That(a[i].K, Is.EqualTo(b2[i].K));
            Assert.That(a[i].V, Is.EqualTo(b2[i].V));
        }
    }

    [TestCase(1)]
    [TestCase(7)]
    [TestCase(256)]
    [TestCase(5000)]
    public void NodeHashIndex_Enumerator_MatchesPlainBTree(int count)
    {
        (byte[][] keys, byte[][] values) = MakeSortedKeys(count, seed: 42);

        byte[] withNodeHash = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer> b) =>
        {
            for (int i = 0; i < count; i++) b.Add(keys[i], values[i]);
        }, useNodeHashIndex: true);
        byte[] plain = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer> b) =>
        {
            for (int i = 0; i < count; i++) b.Add(keys[i], values[i]);
        });

        List<(byte[] K, byte[] V)> a = Materialize(withNodeHash);
        List<(byte[] K, byte[] V)> b2 = Materialize(plain);

        Assert.That(a.Count, Is.EqualTo(count));
        Assert.That(b2.Count, Is.EqualTo(count));
        for (int i = 0; i < count; i++)
        {
            Assert.That(a[i].K, Is.EqualTo(b2[i].K));
            Assert.That(a[i].V, Is.EqualTo(b2[i].V));
        }
    }

    [Test]
    public void NodeHashIndex_TableSize_IsSizedOffLeafCount()
    {
        // 1000 entries with default maxLeafEntries=256 -> 4 leaves. At target 0.75:
        //   ceil(4/0.75)=6 -> next pow2 = 8 -> log2 = 3.
        // (Compare with BTreeHashIndex at the same count which would use log2≈11.)
        const int count = 1000;
        (byte[][] keys, byte[][] values) = MakeSortedKeys(count);

        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer> b) =>
        {
            for (int i = 0; i < count; i++) b.Add(keys[i], values[i]);
        }, useNodeHashIndex: true, hashIndexTargetUtilization: 0.75);

        Assert.That(data[^1], Is.EqualTo((byte)IndexType.BTreeNodeHashIndex));
        Assert.That(data[^2], Is.EqualTo((byte)3));
    }

    [Test]
    public void NodeHashIndex_EmptyHsst_FallsBackToPlainBTree()
    {
        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer> _) => { },
            useNodeHashIndex: true);

        Assert.That(data[^1], Is.EqualTo((byte)IndexType.BTree));
        Assert.That(TryGet(data, "anything"u8, out _), Is.False);
    }

    [Test]
    public void NodeHashIndex_LeafCollision_FallsThroughToBTree()
    {
        // Many entries spread across many leaves at saturating target -> some slots
        // will be hit by entries from distinct leaves and end up as Collision.
        // Every key must still resolve via the b-tree fallback.
        (byte[][] keys, byte[][] values) = MakeSortedKeys(2000, seed: 7);

        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer> b) =>
        {
            for (int i = 0; i < keys.Length; i++) b.Add(keys[i], values[i]);
        }, useNodeHashIndex: true, hashIndexTargetUtilization: 1.0, maxLeafEntries: 8);

        for (int i = 0; i < keys.Length; i++)
        {
            Assert.That(TryGet(data, keys[i], out byte[] got), Is.True, $"missing key {i}");
            Assert.That(got, Is.EqualTo(values[i]));
        }
    }

    [Test]
    public void NodeHashIndex_RejectsCombinationWithValueHashIndex() =>
        Assert.Throws<ArgumentException>(() =>
            HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer> _) => { },
                useHashIndex: true, useNodeHashIndex: true));
}
