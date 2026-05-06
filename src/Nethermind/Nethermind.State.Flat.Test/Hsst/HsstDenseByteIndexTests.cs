// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.State.Flat.Hsst;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class HsstDenseByteIndexTests
{
    private static byte[] Build(byte[] tags, byte[][] values)
    {
        Assert.That(tags.Length, Is.EqualTo(values.Length));
        using PooledByteBufferWriter pooled = new(64 * 1024);
        using HsstDenseByteIndexBuilder<PooledByteBufferWriter.Writer> b = new(ref pooled.GetWriter());
        for (int i = 0; i < tags.Length; i++) b.Add(tags[i], values[i]);
        b.Build();
        return pooled.WrittenSpan.ToArray();
    }

    private static bool TryGet(ReadOnlySpan<byte> data, byte key, out byte[] value)
    {
        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        if (!r.TrySeek([key], out _)) { value = []; return false; }
        Bound b = r.GetBound();
        value = b.Length == 0 ? [] : data.Slice((int)b.Offset, (int)b.Length).ToArray();
        return true;
    }

    private static bool TryGetFloor(ReadOnlySpan<byte> data, byte key, out byte[] value)
    {
        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        if (!r.TrySeekFloor([key], out _)) { value = []; return false; }
        Bound b = r.GetBound();
        value = b.Length == 0 ? [] : data.Slice((int)b.Offset, (int)b.Length).ToArray();
        return true;
    }

    [TestCase(1)]
    [TestCase(3)]
    [TestCase(7)]
    [TestCase(32)]
    [TestCase(256)]
    public void RoundTrip_AllPositionsFilled_HitsAndMisses(int n)
    {
        // Fill positions 0..n-1 with non-empty values. Tag = position byte.
        byte[] tags = new byte[n];
        byte[][] vals = new byte[n][];
        for (int i = 0; i < n; i++)
        {
            tags[i] = (byte)i;
            int len = (i % 5 == 0) ? 0 : (i + 1) * 11;
            vals[i] = new byte[len];
            for (int k = 0; k < len; k++) vals[i][k] = (byte)((i * 17 + k * 13) & 0xff);
        }

        byte[] data = Build(tags, vals);
        Assert.That(data[^1], Is.EqualTo((byte)IndexType.DenseByteIndex));
        Assert.That(data[^2], Is.AnyOf(1, 2, 4, 6));
        Assert.That(data[^3], Is.EqualTo((byte)(n - 1)));

        // Hits — every tag returns the stored value (possibly empty by design).
        for (int i = 0; i < n; i++)
        {
            Assert.That(TryGet(data, (byte)i, out byte[] got), Is.True, $"missing tag 0x{i:X2}");
            Assert.That(got, Is.EqualTo(vals[i]));
        }

        // Misses: tags >= n must miss.
        for (int t = n; t < 256; t++)
            Assert.That(TryGet(data, (byte)t, out _), Is.False, $"unexpected hit on 0x{t:X2}");
    }

    [Test]
    public void GapFill_SkippedPositionsAreEmptyAndAddressable()
    {
        // Add tags 0x02 and 0x05 only; positions 0x00, 0x01, 0x03, 0x04 should auto-fill empty.
        byte[] data = Build([0x02, 0x05], ["AB"u8.ToArray(), "Z"u8.ToArray()]);

        Assert.That(data[^1], Is.EqualTo((byte)IndexType.DenseByteIndex));
        Assert.That(data[^2], Is.EqualTo((byte)1)); // OffsetSize: total 3 bytes ≤ 255
        Assert.That(data[^3], Is.EqualTo((byte)5)); // N - 1 where N = 6

        // Gap positions return success with empty value.
        Assert.That(TryGet(data, 0x00, out byte[] v0), Is.True);
        Assert.That(v0, Is.EqualTo(Array.Empty<byte>()));
        Assert.That(TryGet(data, 0x01, out byte[] v1), Is.True);
        Assert.That(v1.Length, Is.EqualTo(0));
        Assert.That(TryGet(data, 0x03, out byte[] v3), Is.True);
        Assert.That(v3.Length, Is.EqualTo(0));
        Assert.That(TryGet(data, 0x04, out byte[] v4), Is.True);
        Assert.That(v4.Length, Is.EqualTo(0));

        // Real entries.
        Assert.That(TryGet(data, 0x02, out byte[] v2), Is.True);
        Assert.That(v2, Is.EqualTo("AB"u8.ToArray()));
        Assert.That(TryGet(data, 0x05, out byte[] v5), Is.True);
        Assert.That(v5, Is.EqualTo("Z"u8.ToArray()));

        // Out-of-range.
        Assert.That(TryGet(data, 0x06, out _), Is.False);
        Assert.That(TryGet(data, 0xFF, out _), Is.False);
    }

    [Test]
    public void Floor_SkipsEmptyEntries()
    {
        // Fill 0x02 and 0x05; floor of 0x04 should land on 0x02 (skipping empty 0x03, 0x04).
        byte[] data = Build([0x02, 0x05], ["X"u8.ToArray(), "Y"u8.ToArray()]);

        Assert.That(TryGetFloor(data, 0x04, out byte[] f4), Is.True);
        Assert.That(f4, Is.EqualTo("X"u8.ToArray()));
        Assert.That(TryGetFloor(data, 0x05, out byte[] f5), Is.True);
        Assert.That(f5, Is.EqualTo("Y"u8.ToArray()));
        Assert.That(TryGetFloor(data, 0xFF, out byte[] fff), Is.True);
        Assert.That(fff, Is.EqualTo("Y"u8.ToArray()));
        // Below all real entries: 0x01 falls to no non-empty entry.
        Assert.That(TryGetFloor(data, 0x01, out _), Is.False);
    }

    [Test]
    public void RejectsUnsortedAndMultiByteAndEmpty()
    {
        bool ooo = false;
        using (PooledByteBufferWriter p = new(1024))
        {
            using HsstDenseByteIndexBuilder<PooledByteBufferWriter.Writer> b = new(ref p.GetWriter());
            b.Add(0x05, [0x01]);
            try { b.Add(0x05, [0x02]); } catch (ArgumentException) { ooo = true; }
        }
        Assert.That(ooo, Is.True, "duplicate / non-ascending tag must throw");

        bool multi = false;
        using (PooledByteBufferWriter p = new(1024))
        {
            using HsstDenseByteIndexBuilder<PooledByteBufferWriter.Writer> b = new(ref p.GetWriter());
            try { b.Add([0x05, 0x06], [0x01]); } catch (ArgumentException) { multi = true; }
        }
        Assert.That(multi, Is.True, "multi-byte tag span must throw");

        bool empty = false;
        using (PooledByteBufferWriter p = new(64))
        {
            using HsstDenseByteIndexBuilder<PooledByteBufferWriter.Writer> b = new(ref p.GetWriter());
            try { b.Build(); } catch (InvalidOperationException) { empty = true; }
        }
        Assert.That(empty, Is.True, "Build on empty map must throw");
    }

    [Test]
    public void TrailerLayout_NoTagsArray_ThreeEntryFixture()
    {
        // Three entries at positions 0x00, 0x02, 0x03 → values "AB", "Z", "" (empty).
        // Position 0x01 is gap-filled empty → N = 4. valuesTotal = 3 ≤ 255 → OffsetSize = 1.
        byte[] data = Build([0x00, 0x02, 0x03], ["AB"u8.ToArray(), "Z"u8.ToArray(), []]);

        // Layout: [Value_0=2][Value_2=1][Ends: 4·1][Count:1][OffsetSize:1][IndexType:1]
        //       = 2 + 1 + 4 + 3 = 10
        Assert.That(data.Length, Is.EqualTo(2 + 1 + 4 + 3));
        Assert.That(data[^1], Is.EqualTo((byte)IndexType.DenseByteIndex));
        Assert.That(data[^2], Is.EqualTo((byte)1)); // OffsetSize
        Assert.That(data[^3], Is.EqualTo((byte)3)); // N - 1

        // Ends sit immediately before the trailer; cumulative ends 2, 2, 3, 3 (1 byte each).
        ReadOnlySpan<byte> endsSpan = data.AsSpan(data.Length - 3 - 4, 4);
        Assert.That(endsSpan[0], Is.EqualTo((byte)2));
        Assert.That(endsSpan[1], Is.EqualTo((byte)2));
        Assert.That(endsSpan[2], Is.EqualTo((byte)3));
        Assert.That(endsSpan[3], Is.EqualTo((byte)3));

        // Values up front.
        Assert.That(data[..2], Is.EqualTo("AB"u8.ToArray()));
        Assert.That(data[2], Is.EqualTo((byte)'Z'));
    }

    [Test]
    public void OffsetSize_GrowsWithValuesTotal_AndRoundTripsCorrectly()
    {
        // For each target OffsetSize regime, build a small DenseByteIndex whose cumulative
        // values total falls into that bucket; verify the trailer's OffsetSize byte and
        // that lookups round-trip including gap-filled entries.
        (int valLen, int expectedOffsetSize)[] cases =
        [
            (50, 1),     // 4 entries × 50 = 200 ≤ 255
            (300, 2),    // 4 entries × 300 = 1200 > 255 → OffsetSize 2
            (20_000, 4), // 4 entries × 20000 = 80000 > 65535 → OffsetSize 4
        ];

        foreach ((int valLen, int expectedOffsetSize) in cases)
        {
            // Tags 0, 2, 4, 6 — gaps at 1, 3, 5 must round-trip as empty values regardless of OffsetSize.
            byte[] tags = [0x00, 0x02, 0x04, 0x06];
            byte[][] vals = new byte[4][];
            for (int i = 0; i < 4; i++)
            {
                vals[i] = new byte[valLen];
                for (int k = 0; k < valLen; k++) vals[i][k] = (byte)((i * 31 + k) & 0xff);
            }

            byte[] data = Build(tags, vals);
            Assert.That(data[^1], Is.EqualTo((byte)IndexType.DenseByteIndex));
            Assert.That(data[^2], Is.EqualTo((byte)expectedOffsetSize),
                $"valLen={valLen} expected OffsetSize {expectedOffsetSize} but trailer says {data[^2]}");
            Assert.That(data[^3], Is.EqualTo((byte)6)); // N - 1 where N = highestTag + 1 = 7

            // Round-trip filled positions.
            for (int i = 0; i < 4; i++)
            {
                Assert.That(TryGet(data, tags[i], out byte[] got), Is.True);
                Assert.That(got, Is.EqualTo(vals[i]));
            }
            // Gap positions 1, 3, 5 round-trip as empty.
            foreach (byte gap in new byte[] { 0x01, 0x03, 0x05 })
            {
                Assert.That(TryGet(data, gap, out byte[] g), Is.True);
                Assert.That(g.Length, Is.EqualTo(0));
            }
            // Above-range tag 0x07 misses.
            Assert.That(TryGet(data, 0x07, out _), Is.False);
        }
    }
}
