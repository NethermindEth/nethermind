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
public class HsstFlatTests
{
    private const int KeySize = 16;
    private const int ValueSize = 8;

    private static byte[] BuildFlat(byte[][] keys, byte[][] values, int strideBytes = HsstFlatBuilder<PooledByteBufferWriter.Writer>.DefaultBinaryIndexStrideBytes, bool useHashIndex = true)
    {
        using PooledByteBufferWriter pooled = new(10 * 1024 * 1024);
        HsstFlatBuilder<PooledByteBufferWriter.Writer> builder = new(
            ref pooled.GetWriter(),
            keySize: KeySize,
            valueSize: ValueSize,
            binaryIndexStrideBytes: strideBytes,
            expectedKeyCount: keys.Length,
            useHashIndex: useHashIndex);
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
        byte[] data = BuildFlat(keys, values);

        Assert.That(data[^1], Is.EqualTo((byte)IndexType.FlatEntries));

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
        HsstFlatBuilder<PooledByteBufferWriter.Writer> builder = new(ref pooled.GetWriter(), KeySize, ValueSize);
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
        HsstFlatBuilder<PooledByteBufferWriter.Writer> builder = new(ref pooled.GetWriter(), KeySize, ValueSize);
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

    [TestCase(1, false)]
    [TestCase(7, false)]
    [TestCase(256, false)]
    [TestCase(5000, false)]
    public void NoHashIndex_HitsAndFloorAndMisses(int count, bool _)
    {
        (byte[][] keys, byte[][] values) = MakeSortedKeys(count, seed: 23);
        byte[] data = BuildFlat(keys, values, useHashIndex: false);

        Assert.That(data[^1], Is.EqualTo((byte)IndexType.FlatEntries));

        // Exact-match hits.
        for (int i = 0; i < count; i++)
        {
            Assert.That(TryGet(data, keys[i], out byte[] got), Is.True, $"missing key {i}");
            Assert.That(got, Is.EqualTo(values[i]));
        }

        // Floor lookups agree with linear search.
        Random rng = new(31);
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

    [Test]
    public void RecursiveSummary_MultiLevel_RoundTrips()
    {
        // 5000 entries × 24 bytes = 120 000 data bytes. With a small 128-byte stride this
        // forces ~937 level-0 checkpoints, ~146 level-1, ~22 level-2, ~3 level-3, etc. —
        // enough to exercise depth ≥ 3 in the recursive descent.
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

    // Drives the SIMD floor-scan path in HsstFlatReader.SearchSummaryLevel for the two
    // supported key sizes (4 and 8). With a small stride we force multiple summary
    // levels so the recursive descent goes through SearchSummaryLevel repeatedly. We
    // run with the SIMD flag both off and on to ensure parity with the scalar path.
    [TestCase(4, true)]
    [TestCase(4, false)]
    [TestCase(8, true)]
    [TestCase(8, false)]
    public void SmallKey_SimdToggle_MatchesScalar(int keySize, bool simdEnabled)
    {
        const int count = 5000;
        const int valueSize = 4;

        Random rng = new(keySize * 7 + (simdEnabled ? 1 : 0));
        HashSet<string> seen = new();
        List<byte[]> ks = new(count);
        while (ks.Count < count)
        {
            byte[] k = new byte[keySize];
            rng.NextBytes(k);
            if (seen.Add(Convert.ToHexString(k))) ks.Add(k);
        }
        ks.Sort((a, b) => a.AsSpan().SequenceCompareTo(b));
        byte[][] keys = ks.ToArray();
        byte[][] values = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            values[i] = new byte[valueSize];
            BinaryPrimitives.WriteInt32LittleEndian(values[i], i);
        }

        byte[] data;
        using (PooledByteBufferWriter pooled = new(2 * 1024 * 1024))
        {
            HsstFlatBuilder<PooledByteBufferWriter.Writer> builder = new(
                ref pooled.GetWriter(),
                keySize: keySize,
                valueSize: valueSize,
                binaryIndexStrideBytes: 128,
                expectedKeyCount: count,
                useHashIndex: false);
            try
            {
                for (int i = 0; i < count; i++) builder.Add(keys[i], values[i]);
                builder.Build();
                data = pooled.WrittenSpan.ToArray();
            }
            finally { builder.Dispose(); }
        }

        bool prev = BSearchIndexReaderSimd.Enabled;
        BSearchIndexReaderSimd.Enabled = simdEnabled;
        try
        {
            // Exact-match hits: covers the floor + SequenceEqual branch in the SIMD path.
            for (int i = 0; i < count; i++)
            {
                SpanByteReader reader = new(data);
                using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
                Assert.That(r.TrySeek(keys[i], out _), Is.True, $"missing key {i} (simd={simdEnabled})");
                Bound b = r.GetBound();
                Assert.That(data.AsSpan((int)b.Offset, b.Length).ToArray(), Is.EqualTo(values[i]));
            }

            // Floor probes: covers floor < 0, exact-equal, and floor + 1 conversion.
            Random probeRng = new(keySize * 13 + 1);
            for (int t = 0; t < 64; t++)
            {
                byte[] probe = new byte[keySize];
                probeRng.NextBytes(probe);
                int floorIdx = -1;
                for (int i = 0; i < count; i++)
                {
                    if (keys[i].AsSpan().SequenceCompareTo(probe) <= 0) floorIdx = i; else break;
                }

                SpanByteReader reader = new(data);
                using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
                bool ok = r.TrySeekFloor(probe, out _);
                if (floorIdx < 0)
                {
                    Assert.That(ok, Is.False);
                }
                else
                {
                    Assert.That(ok, Is.True);
                    Bound b = r.GetBound();
                    Assert.That(data.AsSpan((int)b.Offset, b.Length).ToArray(), Is.EqualTo(values[floorIdx]));
                }
            }

            // Edge cases: probes equal to the very first and last key (drive the
            // floor==-1-equivalent ceiling and floor==n-1 branches).
            byte[] beforeAll = new byte[keySize]; // all-zero, smaller than any present key by construction (very likely)
            byte[] afterAll = new byte[keySize];
            for (int i = 0; i < keySize; i++) afterAll[i] = 0xFF;
            {
                SpanByteReader reader = new(data);
                using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
                // Seek for first key: must hit.
                Assert.That(r.TrySeek(keys[0], out _), Is.True);
            }
            {
                SpanByteReader reader = new(data);
                using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
                Assert.That(r.TrySeek(keys[count - 1], out _), Is.True);
            }
            {
                SpanByteReader reader = new(data);
                using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
                // Floor of all-FF must be the last key.
                Assert.That(r.TrySeekFloor(afterAll, out _), Is.True);
                Bound b = r.GetBound();
                Assert.That(data.AsSpan((int)b.Offset, b.Length).ToArray(), Is.EqualTo(values[count - 1]));
            }
        }
        finally
        {
            BSearchIndexReaderSimd.Enabled = prev;
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
