// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Nethermind.State.Flat.BSearchIndex;
using Nethermind.State.Flat.Hsst;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class HsstPackedArrayTests
{
    private const int KeySize = 16;
    private const int ValueSize = 8;

    private static byte[] BuildFlat(byte[][] keys, byte[][] values, int strideBytes = HsstPackedArrayBuilder<PooledByteBufferWriter.Writer>.DefaultBinaryIndexStrideBytes)
    {
        using PooledByteBufferWriter pooled = new(10 * 1024 * 1024);
        HsstPackedArrayBuilder<PooledByteBufferWriter.Writer> builder = new(
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
        using HsstRefEnumerator<SpanByteReader, NoOpPin> e = new(in reader, new Bound(0, data.Length));
        while (e.MoveNext())
        {
            Bound kb = e.Current.KeyBound;
            Bound vb = e.Current.ValueBound;
            entries.Add((data.Slice((int)kb.Offset, (int)kb.Length).ToArray(), data.Slice((int)vb.Offset, (int)vb.Length).ToArray()));
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
        byte[] data = BuildFlat(keys, values);

        Assert.That(data[^1], Is.EqualTo((byte)IndexType.PackedArray));

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
        byte[] data = BuildFlat(keys, values);

        Random rng = new(11);
        for (int t = 0; t < 64; t++)
        {
            byte[] probe = new byte[KeySize];
            rng.NextBytes(probe);

            // Reference: largest key <= probe.
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
        byte[] data = BuildFlat(keys, values);

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
        // Ref-struct builders can't be captured in lambdas, so we manually try/catch.
        using PooledByteBufferWriter pooled = new(1024);
        HsstPackedArrayBuilder<PooledByteBufferWriter.Writer> builder = new(ref pooled.GetWriter(), KeySize, ValueSize);
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
        HsstPackedArrayBuilder<PooledByteBufferWriter.Writer> builder = new(ref pooled.GetWriter(), KeySize, ValueSize);
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
    public void RecursiveSummary_MultiLevel_RoundTrips()
    {
        // 5000 entries × 24 bytes = 120 000 data bytes. With a 128-byte stride this yields
        // N=4, M=8 → counts 1250 / 157 / 20 / 3, capped at MaxSummaryDepth=4 (the would-be
        // 5th level is dropped; the top level binary-searches its 3 records directly).
        const int count = 5000;
        (byte[][] keys, byte[][] values) = MakeSortedKeys(count, seed: 71);
        byte[] data = BuildFlat(keys, values, strideBytes: 128);

        for (int i = 0; i < count; i++)
        {
            Assert.That(TryGet(data, keys[i], out byte[] got), Is.True);
            Assert.That(got, Is.EqualTo(values[i]));
        }

        // Spot-check floor as well.
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

    private static byte[] BuildFlatLe(byte[][] keys, byte[][] values, int keySize, int valueSize, int strideBytes, bool isLE)
    {
        using PooledByteBufferWriter pooled = new(10 * 1024 * 1024);
        HsstPackedArrayBuilder<PooledByteBufferWriter.Writer> builder = new(
            ref pooled.GetWriter(),
            keySize: keySize,
            valueSize: valueSize,
            binaryIndexStrideBytes: strideBytes,
            expectedKeyCount: keys.Length,
            isLittleEndian: isLE);
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

    private static (byte[][] Keys, byte[][] Values) MakeUniqueAscendingKeys(int count, int keySize, int valueSize, int seed)
    {
        Random rng = new(seed);
        HashSet<string> seen = [];
        List<byte[]> ks = new(count);
        while (ks.Count < count)
        {
            byte[] k = new byte[keySize];
            rng.NextBytes(k);
            if (seen.Add(Convert.ToHexString(k))) ks.Add(k);
        }
        ks.Sort((a, b) => a.AsSpan().SequenceCompareTo(b));
        byte[][] vs = ks.Select((_, i) =>
        {
            byte[] v = new byte[valueSize];
            for (int b = 0; b < valueSize; b++) v[b] = (byte)((i * 31 + b) & 0xff);
            return v;
        }).ToArray();
        return (ks.ToArray(), vs);
    }

    private static bool TryGetSpan(ReadOnlySpan<byte> data, scoped ReadOnlySpan<byte> key, out byte[] value)
    {
        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        if (!r.TrySeek(key, out _)) { value = []; return false; }
        Bound b = r.GetBound();
        value = data.Slice((int)b.Offset, (int)b.Length).ToArray();
        return true;
    }

    private static bool TryGetFloorSpan(ReadOnlySpan<byte> data, scoped ReadOnlySpan<byte> key, out byte[] value)
    {
        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        if (!r.TrySeekFloor(key, out _)) { value = []; return false; }
        Bound b = r.GetBound();
        value = data.Slice((int)b.Offset, (int)b.Length).ToArray();
        return true;
    }

    // Cross-product: KeySize ∈ {2,4,8} × IsLittleEndian ∈ {false,true} × SIMD ∈ {off,on} ×
    // counts spanning the SIMD/scalar boundary and crossing 8/16/32-lane batch boundaries.
    [Test, Pairwise]
    public void LeAndSimd_AgreeWithScalarLinearSearch(
        [Values(2, 4, 8)] int keySize,
        [Values(false, true)] bool isLE,
        [Values(false, true)] bool simdOn,
        [Values(1, 7, 15, 16, 17, 31, 32, 33, 64, 257, 1023, 1024, 1025)] int count,
        [Values(8, 0)] int valueSize,
        [Values(64, 256, 4096)] int strideBytes)
    {
        bool savedEnabled = BSearchIndexReaderSimd.Enabled;
        BSearchIndexReaderSimd.Enabled = simdOn;
        try
        {
            (byte[][] keys, byte[][] values) = MakeUniqueAscendingKeys(count, keySize, valueSize, seed: keySize * 1000 + count);
            byte[] data = BuildFlatLe(keys, values, keySize, valueSize, strideBytes, isLE);

            // Every stored key must round-trip via exact seek.
            for (int i = 0; i < count; i++)
            {
                Assert.That(TryGetSpan(data, keys[i], out byte[] got), Is.True, $"missing key #{i} (keySize={keySize}, isLE={isLE}, simdOn={simdOn}, count={count})");
                Assert.That(got, Is.EqualTo(values[i]));
            }

            // Floor probes: smaller-than-all, larger-than-all, between every consecutive pair,
            // exact at first/last.
            byte[] tinier = new byte[keySize];
            byte[] huger = Enumerable.Repeat((byte)0xff, keySize).ToArray();
            CheckFloor(data, tinier, keys, values);
            CheckFloor(data, huger, keys, values);
            CheckFloor(data, keys[0], keys, values);
            CheckFloor(data, keys[count - 1], keys, values);

            // A handful of random in-between probes.
            Random rng = new(count * 7 + (isLE ? 1 : 0) + (simdOn ? 2 : 0));
            for (int t = 0; t < 32; t++)
            {
                byte[] probe = new byte[keySize];
                rng.NextBytes(probe);
                CheckFloor(data, probe, keys, values);
            }
        }
        finally
        {
            BSearchIndexReaderSimd.Enabled = savedEnabled;
        }
    }

    private static void CheckFloor(byte[] data, byte[] probe, byte[][] keys, byte[][] values)
    {
        int floorIdx = -1;
        for (int i = 0; i < keys.Length; i++)
        {
            if (keys[i].AsSpan().SequenceCompareTo(probe) <= 0) floorIdx = i; else break;
        }
        bool ok = TryGetFloorSpan(data, probe, out byte[] got);
        if (floorIdx < 0)
        {
            Assert.That(ok, Is.False, $"expected no floor for {Convert.ToHexString(probe)}");
        }
        else
        {
            Assert.That(ok, Is.True, $"expected floor for {Convert.ToHexString(probe)}");
            Assert.That(got, Is.EqualTo(values[floorIdx]));
        }
    }

    [Test]
    public void LeBuilder_RejectsNonStandardKeySize()
    {
        using PooledByteBufferWriter pooled = new(1024);
        Assert.Throws<ArgumentException>(() =>
        {
            HsstPackedArrayBuilder<PooledByteBufferWriter.Writer> builder = new(
                ref pooled.GetWriter(),
                keySize: 16, valueSize: 0, isLittleEndian: true);
            builder.Dispose();
        });
    }

    [TestCase(2)]
    [TestCase(4)]
    [TestCase(8)]
    public void LeAndBe_LayoutsRoundTripIdentically(int keySize)
    {
        const int count = 500;
        const int valueSize = 4;
        (byte[][] keys, byte[][] values) = MakeUniqueAscendingKeys(count, keySize, valueSize, seed: keySize + 99);

        byte[] beData = BuildFlatLe(keys, values, keySize, valueSize, strideBytes: 256, isLE: false);
        byte[] leData = BuildFlatLe(keys, values, keySize, valueSize, strideBytes: 256, isLE: true);

        for (int i = 0; i < count; i++)
        {
            Assert.That(TryGetSpan(beData, keys[i], out byte[] beGot), Is.True);
            Assert.That(TryGetSpan(leData, keys[i], out byte[] leGot), Is.True);
            Assert.That(beGot, Is.EqualTo(values[i]));
            Assert.That(leGot, Is.EqualTo(values[i]));
        }
    }

    [Test]
    public void StrideBytes_ChangesIndexCount()
    {
        // 5000 entries × 24 bytes/entry = 120 000 data bytes. With 256-byte stride we get many
        // more checkpoints than with 4096-byte stride.
        (byte[][] keys, byte[][] values) = MakeSortedKeys(5000, seed: 17);

        byte[] dense = BuildFlat(keys, values, strideBytes: 256);
        byte[] sparse = BuildFlat(keys, values, strideBytes: 4096);

        // Both must remain functionally identical.
        Random rng = new(3);
        for (int t = 0; t < 16; t++)
        {
            int idx = rng.Next(keys.Length);
            Assert.That(TryGet(dense, keys[idx], out byte[] gotDense), Is.True);
            Assert.That(TryGet(sparse, keys[idx], out byte[] gotSparse), Is.True);
            Assert.That(gotDense, Is.EqualTo(values[idx]));
            Assert.That(gotSparse, Is.EqualTo(values[idx]));
        }

        // Smaller stride => strictly more (or equal) checkpoints, so the dense file is
        // larger in the binary-index region by at least one extra entry.
        Assert.That(dense.Length, Is.GreaterThan(sparse.Length));
    }
}
