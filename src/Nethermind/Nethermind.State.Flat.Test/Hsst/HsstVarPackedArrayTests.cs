// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.State.Flat.Hsst;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class HsstVarPackedArrayTests
{
    private const int KeySize = 16;

    private static byte[] BuildVar(byte[][] keys, byte[][] values, int strideBytes = HsstVarPackedArrayBuilder<PooledByteBufferWriter.Writer>.DefaultBinaryIndexStrideBytes)
    {
        using PooledByteBufferWriter pooled = new(10 * 1024 * 1024);
        HsstVarPackedArrayBuilder<PooledByteBufferWriter.Writer> builder = new(
            ref pooled.GetWriter(),
            keySize: KeySize,
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
        value = data.Slice((int)b.Offset, (int)b.Length).ToArray();
        return true;
    }

    private static bool TryGetFloor(ReadOnlySpan<byte> data, scoped ReadOnlySpan<byte> key, out byte[] value)
    {
        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        if (!r.TrySeekFloor(key, out _)) { value = []; return false; }
        Bound b = r.GetBound();
        value = data.Slice((int)b.Offset, (int)b.Length).ToArray();
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
            entries.Add((data.Slice((int)kb.Offset, (int)kb.Length).ToArray(), data.Slice((int)vb.Offset, (int)vb.Length).ToArray()));
        }
        return entries;
    }

    private static (byte[][] Keys, byte[][] Values) MakeSortedKeysWithVarValues(int count, int seed = 1, int maxValueSize = 64)
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
            int len = rng.Next(0, maxValueSize + 1);
            byte[] v = new byte[len];
            rng.NextBytes(v);
            return v;
        }).ToArray();
        return (ks.ToArray(), vs);
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(7)]
    [TestCase(256)]
    [TestCase(5000)]
    public void RoundTrip_HitsAndMisses_VarValues(int count)
    {
        (byte[][] keys, byte[][] values) = MakeSortedKeysWithVarValues(count);
        byte[] data = BuildVar(keys, values);

        Assert.That(data[^1], Is.EqualTo((byte)IndexType.VarPackedArray));

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
    public void Floor_AgreesWithLinearSearch_VarValues(int count)
    {
        (byte[][] keys, byte[][] values) = MakeSortedKeysWithVarValues(count, seed: 5);
        byte[] data = BuildVar(keys, values);

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
    public void Enumerator_YieldsEntriesInOrder_VarValues(int count)
    {
        (byte[][] keys, byte[][] values) = MakeSortedKeysWithVarValues(count, seed: 42);
        byte[] data = BuildVar(keys, values);

        List<(byte[] K, byte[] V)> seen = Materialize(data);
        Assert.That(seen.Count, Is.EqualTo(count));
        for (int i = 0; i < count; i++)
        {
            Assert.That(seen[i].K, Is.EqualTo(keys[i]));
            Assert.That(seen[i].V, Is.EqualTo(values[i]));
        }
    }

    [Test]
    public void Add_RejectsMismatchedKeySize()
    {
        using PooledByteBufferWriter pooled = new(1024);
        HsstVarPackedArrayBuilder<PooledByteBufferWriter.Writer> builder = new(ref pooled.GetWriter(), KeySize);
        try
        {
            byte[] shortKey = new byte[KeySize - 1];
            byte[] value = [1, 2, 3];
            bool threw = false;
            try { builder.Add(shortKey, value); } catch (ArgumentException) { threw = true; }
            Assert.That(threw, Is.True, "short key should throw");
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
        HsstVarPackedArrayBuilder<PooledByteBufferWriter.Writer> builder = new(ref pooled.GetWriter(), KeySize);
        try
        {
            byte[] k1 = new byte[KeySize]; k1[0] = 1;
            byte[] k2 = new byte[KeySize]; k2[0] = 2;
            byte[] v = [42];
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
    public void RecursiveSummary_MultiLevel_RoundTrips_VarValues()
    {
        // 5000 entries with mixed value sizes and a small 128-byte stride forces multi-level
        // summaries (depth ≥ 3), exercising the recursive descent and offset-table reads.
        const int count = 5000;
        (byte[][] keys, byte[][] values) = MakeSortedKeysWithVarValues(count, seed: 71, maxValueSize: 32);
        byte[] data = BuildVar(keys, values, strideBytes: 128);

        for (int i = 0; i < count; i++)
        {
            Assert.That(TryGet(data, keys[i], out byte[] got), Is.True);
            Assert.That(got, Is.EqualTo(values[i]));
        }

        Random rng = new(101);
        for (int t = 0; t < 32; t++)
        {
            byte[] probe = new byte[KeySize];
            rng.NextBytes(probe);
            int floorIdx = -1;
            for (int i = 0; i < count; i++)
            {
                if (keys[i].AsSpan().SequenceCompareTo(probe) <= 0) floorIdx = i; else break;
            }
            bool ok = TryGetFloor(data, probe, out byte[] got);
            if (floorIdx < 0) Assert.That(ok, Is.False);
            else
            {
                Assert.That(ok, Is.True);
                Assert.That(got, Is.EqualTo(values[floorIdx]));
            }
        }
    }

    // OffsetSize promotes from 1 byte (totals ≤ 255) to 2 bytes (≤ 65535) to 4 bytes (≤ 4 GiB).
    // 6-byte path is unreachable under the HSST 2 GiB cap so we stop at 4.
    [TestCase(50, 4, Description = "totals ≤ 255 → 1-byte offsets")]
    [TestCase(200, 100, Description = "totals > 255, ≤ 65535 → 2-byte offsets")]
    [TestCase(2000, 200, Description = "totals > 65535 → 4-byte offsets")]
    public void OffsetSize_PromotedAcrossThresholds(int count, int valueSize)
    {
        (byte[][] keys, _) = MakeSortedKeysWithVarValues(count, seed: 7, maxValueSize: 1);
        byte[][] values = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            values[i] = new byte[valueSize];
            for (int b = 0; b < valueSize; b++) values[i][b] = (byte)(i + b);
        }

        byte[] data = BuildVar(keys, values);

        for (int i = 0; i < count; i++)
        {
            Assert.That(TryGet(data, keys[i], out byte[] got), Is.True);
            Assert.That(got, Is.EqualTo(values[i]));
        }

        Assert.That(Materialize(data).Count, Is.EqualTo(count));
    }

    [Test]
    public void EmptyValues_Allowed()
    {
        (byte[][] keys, _) = MakeSortedKeysWithVarValues(32, seed: 13, maxValueSize: 1);
        byte[][] values = new byte[32][];
        for (int i = 0; i < 32; i++) values[i] = i % 3 == 0 ? [] : new byte[] { (byte)i };

        byte[] data = BuildVar(keys, values);

        for (int i = 0; i < 32; i++)
        {
            Assert.That(TryGet(data, keys[i], out byte[] got), Is.True);
            Assert.That(got, Is.EqualTo(values[i]));
        }

        List<(byte[] K, byte[] V)> seen = Materialize(data);
        for (int i = 0; i < 32; i++)
        {
            Assert.That(seen[i].V, Is.EqualTo(values[i]));
        }
    }
}
