// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.State.Flat.Hsst;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Canonical cross-format round-trip authority. The same per-format corpus must
/// round-trip identically through Add → Get (exact seek) → Floor seek →
/// Enumerate, regardless of the on-disk layout. This catches encoding-family
/// bugs (LE/BE PackedArray, key-first BTree, descending DenseByteIndex, etc.)
/// in a single place instead of forcing every format to reinvent the same
/// round-trip plumbing.
/// </summary>
/// <remarks>
/// Each format gets its own (keySize, valueSize, count) shape because formats
/// have incompatible constraints — DenseByteIndex caps at 256 entries with
/// 1-byte keys and strictly-descending insertion; TwoByteSlotValue requires
/// 2-byte keys with a u16 cumulative-value cap; BTree/PackedArray take any
/// shape. The TestCaseSource encodes those per-format ranges so the same
/// test body runs against every supported configuration.
/// </remarks>
[TestFixture]
public class HsstCrossFormatTests
{
    public enum Format { BTree, BTreeKeyFirst, PackedArrayBe, PackedArrayLe, TwoByteSlotValue, TwoByteSlotValueLarge, DenseByteIndex }

    public static IEnumerable<TestCaseData> AllShapes()
    {
        // BTree / BTreeKeyFirst: 8-byte keys × 8-byte values; counts span the multi-level B-tree boundary (65 forces 2 levels).
        foreach (int count in new[] { 1, 2, 65, 1000, 5000 })
            yield return new TestCaseData(Format.BTree, 8, 8, count).SetArgDisplayNames("BTree", count.ToString());
        foreach (int count in new[] { 1, 2, 65, 1000, 5000 })
            yield return new TestCaseData(Format.BTreeKeyFirst, 8, 8, count).SetArgDisplayNames("BTreeKeyFirst", count.ToString());

        // PackedArrayBe / PackedArrayLe: 8-byte keys × 8-byte values; counts span the SIMD/scalar boundary.
        foreach (int count in new[] { 1, 7, 256, 5000 })
            yield return new TestCaseData(Format.PackedArrayBe, 8, 8, count).SetArgDisplayNames("PackedArrayBe", count.ToString());
        foreach (int count in new[] { 1, 7, 256, 5000 })
            yield return new TestCaseData(Format.PackedArrayLe, 8, 8, count).SetArgDisplayNames("PackedArrayLe", count.ToString());

        // TwoByteSlotValue: 2-byte keys × 8-byte values; cumulative bytes stay under the u16 cap.
        foreach (int count in new[] { 1, 256, 1024 })
            yield return new TestCaseData(Format.TwoByteSlotValue, 2, 8, count).SetArgDisplayNames("TwoByteSlotValue", count.ToString());

        // TwoByteSlotValueLarge: 2-byte keys × 32-byte values; cumulative stays under the u24 cap (4096 × 32 = 128 KiB).
        foreach (int count in new[] { 256, 4096 })
            yield return new TestCaseData(Format.TwoByteSlotValueLarge, 2, 32, count).SetArgDisplayNames("TwoByteSlotValueLarge", count.ToString());

        // DenseByteIndex: 1-byte keys × 8-byte values; format caps at 256 entries (one per byte position).
        foreach (int count in new[] { 1, 32, 256 })
            yield return new TestCaseData(Format.DenseByteIndex, 1, 8, count).SetArgDisplayNames("DenseByteIndex", count.ToString());
    }

    [TestCaseSource(nameof(AllShapes))]
    public void AddGetEnumerate_RoundTrip(Format format, int keySize, int valueSize, int count)
    {
        (byte[][] keys, byte[][] values) = MakeCorpus(format, keySize, valueSize, count, seed: 42);
        byte[] data = Build(format, keySize, valueSize, keys, values);

        SpanByteReader reader = new(data);

        for (int i = 0; i < keys.Length; i++)
        {
            using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
            Assert.That(r.TrySeek(keys[i], out _), Is.True, $"missing key #{i} in {format}");
            Bound vb = r.GetBound();
            byte[] got = data.AsSpan().Slice((int)vb.Offset, (int)vb.Length).ToArray();
            Assert.That(got, Is.EqualTo(values[i]), $"value mismatch at #{i} in {format}");
        }

        // Probe a key not in the corpus; pick a value disjoint from any inserted key (and within format key range).
        byte[]? missing = TryMakeMissingKey(format, keySize, keys);
        if (missing is not null)
        {
            using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
            Assert.That(r.TrySeek(missing, out _), Is.False, $"unexpected hit for unstored key in {format}");
        }

        // DenseByteIndex is the persisted-snapshot outer / per-address container and is
        // intentionally not wired into HsstRefEnumerator (production paths use TryGet
        // directly). Skip enumeration for this format — the seek + miss assertions above
        // already cover the round-trip.
        if (format == Format.DenseByteIndex) return;

        List<(byte[] Key, byte[] Value)> enumerated = [];
        Span<byte> keyScratch = stackalloc byte[64];
        using (HsstRefEnumerator<SpanByteReader, NoOpPin> e = new(in reader, new Bound(0, data.Length)))
        {
            while (e.MoveNext())
            {
                ReadOnlySpan<byte> logicalKey = e.CopyCurrentLogicalKey(keyScratch);
                Bound vb = e.Current.ValueBound;
                enumerated.Add((
                    logicalKey.ToArray(),
                    data.AsSpan().Slice((int)vb.Offset, (int)vb.Length).ToArray()));
            }
        }

        Assert.That(enumerated.Count, Is.EqualTo(count), $"enumerated count mismatch in {format}");
        for (int i = 0; i < count; i++)
        {
            Assert.That(enumerated[i].Key, Is.EqualTo(keys[i]), $"enumerated key #{i} mismatch in {format}");
            Assert.That(enumerated[i].Value, Is.EqualTo(values[i]), $"enumerated value #{i} mismatch in {format}");
        }
    }

    [TestCaseSource(nameof(AllShapes))]
    public void Floor_AgreesWithLinearSearch(Format format, int keySize, int valueSize, int count)
    {
        (byte[][] keys, byte[][] values) = MakeCorpus(format, keySize, valueSize, count, seed: 99);
        byte[] data = Build(format, keySize, valueSize, keys, values);

        Random rng = new(count * 7 + (int)format);
        int probes = 32;
        for (int t = 0; t < probes; t++)
        {
            byte[] probe = new byte[keySize];
            rng.NextBytes(probe);
            CheckFloor(format, data, probe, keys, values);
        }

        // Boundary probes: equal-to-first, equal-to-last, smaller-than-all, larger-than-all.
        CheckFloor(format, data, keys[0], keys, values);
        CheckFloor(format, data, keys[^1], keys, values);
        CheckFloor(format, data, new byte[keySize], keys, values);
        byte[] huger = new byte[keySize];
        Array.Fill(huger, (byte)0xff);
        CheckFloor(format, data, huger, keys, values);
    }

    private static void CheckFloor(Format format, byte[] data, byte[] probe, byte[][] keys, byte[][] values)
    {
        // DenseByteIndex auto-fills missing tag positions with zero-length entries; the reader
        // skips those during floor resolution, so floor over a gap-filled-and-inserted layout
        // is functionally identical to a floor over the inserted set alone.
        int floorIdx = -1;
        for (int i = 0; i < keys.Length; i++)
        {
            if (keys[i].AsSpan().SequenceCompareTo(probe) <= 0) floorIdx = i; else break;
        }

        bool ok = HsstTestUtil.TryGetFloor(data, probe, out byte[] got);
        if (floorIdx < 0)
        {
            Assert.That(ok, Is.False, $"expected no floor for {Convert.ToHexString(probe)} in {format}");
        }
        else
        {
            Assert.That(ok, Is.True, $"expected floor for {Convert.ToHexString(probe)} in {format}");
            Assert.That(got, Is.EqualTo(values[floorIdx]), $"floor value mismatch for {Convert.ToHexString(probe)} in {format}");
        }
    }

    private static byte[] Build(Format format, int keySize, int valueSize, byte[][] keys, byte[][] values)
    {
        using PooledByteBufferWriter pooled = new(64 * 1024);
        switch (format)
        {
            case Format.BTree:
            case Format.BTreeKeyFirst:
                {
                    HsstBTreeBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> b
                        = new(ref pooled.GetWriter(), keySize, keyFirst: format == Format.BTreeKeyFirst);
                    try
                    {
                        for (int i = 0; i < keys.Length; i++) b.Add(keys[i], values[i]);
                        b.Build();
                    }
                    finally { b.Dispose(); }
                    break;
                }
            case Format.PackedArrayBe:
            case Format.PackedArrayLe:
                {
                    HsstPackedArrayBuilder<PooledByteBufferWriter.Writer> b = new(
                        ref pooled.GetWriter(),
                        keySize: keySize,
                        valueSize: valueSize,
                        expectedKeyCount: keys.Length,
                        isLittleEndian: format == Format.PackedArrayLe);
                    try
                    {
                        for (int i = 0; i < keys.Length; i++) b.Add(keys[i], values[i]);
                        b.Build();
                    }
                    finally { b.Dispose(); }
                    break;
                }
            case Format.TwoByteSlotValue:
                {
                    HsstTwoByteSlotValueBuilder<PooledByteBufferWriter.Writer> b = new(ref pooled.GetWriter());
                    try
                    {
                        for (int i = 0; i < keys.Length; i++) b.Add(keys[i], values[i]);
                        b.Build();
                    }
                    finally { b.Dispose(); }
                    break;
                }
            case Format.TwoByteSlotValueLarge:
                {
                    HsstTwoByteSlotValueLargeBuilder<PooledByteBufferWriter.Writer> b = new(ref pooled.GetWriter());
                    try
                    {
                        for (int i = 0; i < keys.Length; i++) b.Add(keys[i], values[i]);
                        b.Build();
                    }
                    finally { b.Dispose(); }
                    break;
                }
            case Format.DenseByteIndex:
                {
                    // DenseByteIndex requires strictly-descending insertion; feed the (ascending) corpus tail-first.
                    HsstDenseByteIndexBuilder<PooledByteBufferWriter.Writer> b = new(ref pooled.GetWriter());
                    try
                    {
                        for (int i = keys.Length - 1; i >= 0; i--) b.Add(keys[i], values[i]);
                        b.Build();
                    }
                    finally { b.Dispose(); }
                    break;
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
        return pooled.WrittenSpan.ToArray();
    }

    private static (byte[][] Keys, byte[][] Values) MakeCorpus(Format format, int keySize, int valueSize, int count, int seed)
    {
        Random rng = new(seed);

        byte[][] ks;
        if (format == Format.DenseByteIndex)
        {
            // 1-byte keys must be unique 0..255 — draw a sorted subset of {0..255}.
            int[] positions = Enumerable.Range(0, 256).OrderBy(_ => rng.Next()).Take(count).OrderBy(x => x).ToArray();
            ks = positions.Select(p => new[] { (byte)p }).ToArray();
        }
        else
        {
            HashSet<string> seen = [];
            List<byte[]> tmp = new(count);
            while (tmp.Count < count)
            {
                byte[] k = new byte[keySize];
                rng.NextBytes(k);
                if (seen.Add(Convert.ToHexString(k))) tmp.Add(k);
            }
            tmp.Sort((a, b) => a.AsSpan().SequenceCompareTo(b));
            ks = tmp.ToArray();
        }

        byte[][] vs = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            byte[] v = new byte[valueSize];
            rng.NextBytes(v);
            vs[i] = v;
        }
        return (ks, vs);
    }

    private static byte[]? TryMakeMissingKey(Format format, int keySize, byte[][] keys)
    {
        if (format == Format.DenseByteIndex)
        {
            // DenseByteIndex resolves any in-range tag (including gap-filled ones) as a
            // zero-length hit on TrySeek, so an in-range "missing" tag would NOT miss —
            // it'd return TRUE with an empty bound. Probe a tag strictly above the
            // highest inserted one (which is genuinely out-of-range) when available.
            int highest = keys[^1][0];
            return highest < 255 ? [(byte)(highest + 1)] : null;
        }

        byte[] missing = new byte[keySize];
        Array.Fill(missing, (byte)0xab);
        return keys.Any(k => k.AsSpan().SequenceEqual(missing)) ? null : missing;
    }
}
