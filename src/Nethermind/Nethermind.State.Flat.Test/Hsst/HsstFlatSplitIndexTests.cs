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
public class HsstFlatSplitIndexTests
{
    private const int KeySize = 16;
    private const int ValueSize = 8;

    private static byte[] BuildSplit(byte[][] keys, byte[][] values, int strideBytes = HsstFlatSplitIndexBuilder<PooledByteBufferWriter.Writer>.DefaultBinaryIndexStrideBytes)
    {
        using PooledByteBufferWriter pooled = new(10 * 1024 * 1024);
        HsstFlatSplitIndexBuilder<PooledByteBufferWriter.Writer> builder = new(
            ref pooled.GetWriter(),
            keySize: KeySize,
            valueSize: ValueSize,
            binaryIndexStrideBytes: strideBytes,
            expectedKeyCount: keys.Length);
        try
        {
            for (int i = 0; i < keys.Length; i++) builder.Add(keys[i], values[i]);
            builder.Build();
            return pooled.WrittenSpan.ToArray();
        }
        finally
        {
            builder.Dispose();
        }
    }

    private static byte[] BuildFlat(byte[][] keys, byte[][] values, int strideBytes = HsstFlatBuilder<PooledByteBufferWriter.Writer>.DefaultBinaryIndexStrideBytes)
    {
        using PooledByteBufferWriter pooled = new(10 * 1024 * 1024);
        HsstFlatBuilder<PooledByteBufferWriter.Writer> builder = new(
            ref pooled.GetWriter(),
            keySize: KeySize,
            valueSize: ValueSize,
            binaryIndexStrideBytes: strideBytes,
            expectedKeyCount: keys.Length);
        try
        {
            for (int i = 0; i < keys.Length; i++) builder.Add(keys[i], values[i]);
            builder.Build();
            return pooled.WrittenSpan.ToArray();
        }
        finally
        {
            builder.Dispose();
        }
    }

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
            byte[] k = new byte[KeySize];
            rng.NextBytes(k);
            if (seen.Add(Convert.ToHexString(k))) ks.Add(k);
        }
        ks.Sort((a, b) => a.AsSpan().SequenceCompareTo(b));
        byte[][] vs = ks.Select((_, i) =>
        {
            byte[] v = new byte[ValueSize];
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
    public void RoundTrip_HitsAndMisses(int count)
    {
        (byte[][] keys, byte[][] values) = MakeSortedKeys(count);
        byte[] data = BuildSplit(keys, values);

        Assert.That(data[^1], Is.EqualTo((byte)IndexType.FlatEntriesSplitIndex));

        for (int i = 0; i < count; i++)
        {
            Assert.That(TryGet(data, keys[i], out byte[] got), Is.True, $"missing key {i}");
            Assert.That(got, Is.EqualTo(values[i]));
        }

        Random rng = new(99);
        for (int t = 0; t < 64; t++)
        {
            byte[] missing = new byte[KeySize];
            rng.NextBytes(missing);
            if (Array.BinarySearch(keys, missing, Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b))) >= 0) continue;
            Assert.That(TryGet(data, missing, out _), Is.False);
        }
    }

    [TestCase(1)]
    [TestCase(7)]
    [TestCase(256)]
    [TestCase(5000)]
    public void Floor_AgreesWithLinearSearch(int count)
    {
        (byte[][] keys, byte[][] values) = MakeSortedKeys(count, seed: 5);
        byte[] data = BuildSplit(keys, values);

        Random rng = new(11);
        for (int t = 0; t < 64; t++)
        {
            byte[] probe = new byte[KeySize];
            rng.NextBytes(probe);

            int floorIdx = -1;
            for (int i = 0; i < count; i++)
            {
                if (keys[i].AsSpan().SequenceCompareTo(probe) <= 0) floorIdx = i; else break;
            }

            bool ok = TryGetFloor(data, probe, out byte[] got);
            if (floorIdx < 0)
            {
                Assert.That(ok, Is.False);
            }
            else
            {
                Assert.That(ok, Is.True);
                Assert.That(got, Is.EqualTo(values[floorIdx]));
            }
        }
    }

    [TestCase(1)]
    [TestCase(7)]
    [TestCase(256)]
    [TestCase(5000)]
    public void Enumerator_YieldsEntriesInOrder(int count)
    {
        (byte[][] keys, byte[][] values) = MakeSortedKeys(count, seed: 42);
        byte[] data = BuildSplit(keys, values);

        List<(byte[] K, byte[] V)> seen = Materialize(data);
        Assert.That(seen.Count, Is.EqualTo(count));
        for (int i = 0; i < count; i++)
        {
            Assert.That(seen[i].K, Is.EqualTo(keys[i]));
            Assert.That(seen[i].V, Is.EqualTo(values[i]));
        }
    }

    [Test]
    public void Add_RejectsMismatchedKeyOrValueSize()
    {
        using PooledByteBufferWriter pooled = new(1024);
        HsstFlatSplitIndexBuilder<PooledByteBufferWriter.Writer> builder = new(ref pooled.GetWriter(), KeySize, ValueSize);
        try
        {
            byte[] shortKey = new byte[KeySize - 1];
            byte[] value = new byte[ValueSize];
            bool threw = false;
            try { builder.Add(shortKey, value); } catch (ArgumentException) { threw = true; }
            Assert.That(threw, Is.True, "short key should throw");

            byte[] key = new byte[KeySize];
            byte[] longValue = new byte[ValueSize + 1];
            threw = false;
            try { builder.Add(key, longValue); } catch (ArgumentException) { threw = true; }
            Assert.That(threw, Is.True, "long value should throw");
        }
        finally
        {
            builder.Dispose();
        }
    }

    [Test]
    public void Add_RejectsOutOfOrderKeys()
    {
        using PooledByteBufferWriter pooled = new(1024);
        HsstFlatSplitIndexBuilder<PooledByteBufferWriter.Writer> builder = new(ref pooled.GetWriter(), KeySize, ValueSize);
        try
        {
            byte[] k1 = new byte[KeySize]; k1[0] = 1;
            byte[] k2 = new byte[KeySize]; k2[0] = 2;
            byte[] v = new byte[ValueSize];
            builder.Add(k2, v);
            bool threw = false;
            try { builder.Add(k1, v); } catch (InvalidOperationException) { threw = true; }
            Assert.That(threw, Is.True);
        }
        finally
        {
            builder.Dispose();
        }
    }

    [Test]
    public void StrideBytes_ChangesIndexCount()
    {
        (byte[][] keys, byte[][] values) = MakeSortedKeys(5000, seed: 17);

        byte[] dense = BuildSplit(keys, values, strideBytes: 256);
        byte[] sparse = BuildSplit(keys, values, strideBytes: 4096);

        Random rng = new(3);
        for (int t = 0; t < 16; t++)
        {
            int idx = rng.Next(keys.Length);
            Assert.That(TryGet(dense, keys[idx], out byte[] gotDense), Is.True);
            Assert.That(TryGet(sparse, keys[idx], out byte[] gotSparse), Is.True);
            Assert.That(gotDense, Is.EqualTo(values[idx]));
            Assert.That(gotSparse, Is.EqualTo(values[idx]));
        }

        Assert.That(dense.Length, Is.GreaterThan(sparse.Length));
    }

    [TestCase(7)]
    [TestCase(5000)]
    public void Matches_FlatEntries_ByteCount_AndContent(int count)
    {
        // Same input produces blobs of identical total length and byte-identical Data /
        // HashTable / Metadata sections; only the binary-index region differs in byte order.
        (byte[][] keys, byte[][] values) = MakeSortedKeys(count, seed: 71);

        byte[] flat = BuildFlat(keys, values);
        byte[] split = BuildSplit(keys, values);

        Assert.That(flat.Length, Is.EqualTo(split.Length));
        Assert.That(flat[^1], Is.EqualTo((byte)IndexType.FlatEntries));
        Assert.That(split[^1], Is.EqualTo((byte)IndexType.FlatEntriesSplitIndex));

        // Both should answer every key identically.
        for (int i = 0; i < count; i++)
        {
            Assert.That(TryGet(flat, keys[i], out byte[] gotFlat), Is.True);
            Assert.That(TryGet(split, keys[i], out byte[] gotSplit), Is.True);
            Assert.That(gotFlat, Is.EqualTo(values[i]));
            Assert.That(gotSplit, Is.EqualTo(values[i]));
        }
    }
}
