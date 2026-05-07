// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.State.Flat.Hsst;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class HsstByteTagMapTests
{
    private static byte[] Build(byte[] tags, byte[][] values)
    {
        Assert.That(tags.Length, Is.EqualTo(values.Length));
        using PooledByteBufferWriter pooled = new(64 * 1024);
        using HsstByteTagMapBuilder<PooledByteBufferWriter.Writer> b = new(ref pooled.GetWriter());
        for (int i = 0; i < tags.Length; i++) b.Add(tags[i], values[i]);
        b.Build();
        return pooled.WrittenSpan.ToArray();
    }

    private static bool TryGet(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key, out byte[] value)
    {
        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        if (!r.TrySeek(key, out _)) { value = []; return false; }
        Bound b = r.GetBound();
        value = data.Slice((int)b.Offset, (int)b.Length).ToArray();
        return true;
    }

    private static bool TryGetFloor(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key, out byte tag, out byte[] value)
    {
        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        if (!r.TrySeekFloor(key, out _)) { value = []; tag = 0; return false; }
        Bound b = r.GetBound();
        value = data.Slice((int)b.Offset, (int)b.Length).ToArray();
        tag = 0;
        return true;
    }

    private static List<(byte Tag, byte[] Value)> Materialize(ReadOnlySpan<byte> data)
    {
        List<(byte, byte[])> entries = [];
        SpanByteReader reader = new(data);
        using HsstRefEnumerator<SpanByteReader, NoOpPin> e = new(in reader, new Bound(0, data.Length));
        while (e.MoveNext())
        {
            Bound kb = e.Current.KeyBound;
            Bound vb = e.Current.ValueBound;
            Assert.That(kb.Length, Is.EqualTo(1), "tag is one byte");
            byte tag = data[(int)kb.Offset];
            byte[] v = vb.Length == 0 ? [] : data.Slice((int)vb.Offset, (int)vb.Length).ToArray();
            entries.Add((tag, v));
        }
        return entries;
    }

    [TestCase(1)]
    [TestCase(3)]
    [TestCase(7)]
    [TestCase(32)]
    [TestCase(256)]
    public void RoundTrip_HitsMissesAndIteration(int n)
    {
        // Tags strictly ascending; mix small + larger values; include an empty value.
        // For n=256 the byte space is exhausted so use sequential 0..255; for smaller
        // n keep the i*7+3 stride pattern (still ascending and distinct under 256).
        byte[] tags = new byte[n];
        byte[][] vals = new byte[n][];
        for (int i = 0; i < n; i++)
        {
            tags[i] = n == 256 ? (byte)i : (byte)(i * 7 + 3);
            int len = (i % 5 == 0) ? 0 : (i + 1) * 11;
            vals[i] = new byte[len];
            for (int k = 0; k < len; k++) vals[i][k] = (byte)((i * 17 + k * 13) & 0xff);
        }

        byte[] data = Build(tags, vals);
        // Trailer: [..., Count = N-1, OffsetSize, IndexType].
        Assert.That(data[^1], Is.EqualTo((byte)IndexType.ByteTagMap));
        Assert.That(data[^2], Is.AnyOf(1, 2, 4, 6));
        Assert.That(data[^3], Is.EqualTo((byte)(n - 1)));

        // Hits.
        for (int i = 0; i < n; i++)
        {
            Assert.That(TryGet(data, [tags[i]], out byte[] got), Is.True, $"missing tag 0x{tags[i]:X2}");
            Assert.That(got, Is.EqualTo(vals[i]));
        }

        // Misses (every tag NOT in the set).
        HashSet<byte> used = new(tags);
        for (int t = 0; t < 256; t++)
        {
            if (used.Contains((byte)t)) continue;
            Assert.That(TryGet(data, [(byte)t], out _), Is.False, $"unexpected hit on 0x{t:X2}");
        }

        // Iteration in tag order, every entry visible exactly once.
        List<(byte Tag, byte[] Value)> mat = Materialize(data);
        Assert.That(mat.Count, Is.EqualTo(n));
        for (int i = 0; i < n; i++)
        {
            Assert.That(mat[i].Tag, Is.EqualTo(tags[i]));
            Assert.That(mat[i].Value, Is.EqualTo(vals[i]));
        }
    }

    [Test]
    public void Floor_PicksLargestTagLessOrEqual()
    {
        // tags: 0x10, 0x40, 0x80 → values "a", "b", "c"
        byte[] tags = [0x10, 0x40, 0x80];
        byte[][] vals = ["a"u8.ToArray(), "b"u8.ToArray(), "c"u8.ToArray()];
        byte[] data = Build(tags, vals);

        // Floor of 0x40 = 0x40 (exact).
        Assert.That(TryGetFloor(data, [0x40], out _, out byte[] v40), Is.True);
        Assert.That(v40, Is.EqualTo("b"u8.ToArray()));

        // Floor of 0x41 = 0x40.
        Assert.That(TryGetFloor(data, [0x41], out _, out byte[] v41), Is.True);
        Assert.That(v41, Is.EqualTo("b"u8.ToArray()));

        // Floor of 0x09 = none (precedes everything).
        Assert.That(TryGetFloor(data, [0x09], out _, out _), Is.False);

        // Floor of 0xFF = 0x80.
        Assert.That(TryGetFloor(data, [0xff], out _, out byte[] vff), Is.True);
        Assert.That(vff, Is.EqualTo("c"u8.ToArray()));
    }

    [TestCase(32)]
    [TestCase(256)]
    public void Floor_LargeN_BinarySearchPath(int n)
    {
        // Exercise the binary-search floor path (threshold is 16 entries). Tags are
        // strictly ascending with gaps so we can probe between-tag, equal-to-tag,
        // below-min, and above-max targets.
        byte[] tags = new byte[n];
        byte[][] vals = new byte[n][];
        for (int i = 0; i < n; i++)
        {
            // n=256 fills the keyspace; n=32 uses stride 7 with offset 3 → 3..220.
            tags[i] = n == 256 ? (byte)i : (byte)(i * 7 + 3);
            vals[i] = [(byte)i];
        }
        byte[] data = Build(tags, vals);

        // Equal-to-tag: every tag floors to itself.
        for (int i = 0; i < n; i++)
        {
            Assert.That(TryGetFloor(data, [tags[i]], out _, out byte[] v), Is.True);
            Assert.That(v, Is.EqualTo(new[] { (byte)i }));
        }

        // Between-tag (only meaningful when there are gaps, i.e. n != 256).
        if (n != 256)
        {
            for (int i = 1; i < n; i++)
            {
                byte between = (byte)(tags[i] - 1); // strictly between tags[i-1] and tags[i]
                Assert.That(TryGetFloor(data, [between], out _, out byte[] v), Is.True);
                Assert.That(v, Is.EqualTo(new[] { (byte)(i - 1) }), $"between-tag floor for 0x{between:X2}");
            }
        }

        // Below smallest: no floor.
        if (tags[0] > 0)
        {
            Assert.That(TryGetFloor(data, [(byte)(tags[0] - 1)], out _, out _), Is.False);
        }

        // Above largest: floors to the last tag.
        if (tags[^1] < 0xFF)
        {
            Assert.That(TryGetFloor(data, [0xFF], out _, out byte[] vMax), Is.True);
            Assert.That(vMax, Is.EqualTo(new[] { (byte)(n - 1) }));
        }
    }

    [Test]
    public void RejectsUnsortedDuplicateOversizeAndMultiByteTags()
    {
        // Each case: fresh builder, perform the legal setup, then attempt the illegal call
        // inside a try/catch (ref struct locals can't be captured by Assert.Throws's lambda).
        bool dup = false;
        using (PooledByteBufferWriter p1 = new(1024))
        {
            using HsstByteTagMapBuilder<PooledByteBufferWriter.Writer> b1 = new(ref p1.GetWriter());
            b1.Add(0x05, [0x01]);
            try { b1.Add(0x05, [0x02]); } catch (ArgumentException) { dup = true; }
        }
        Assert.That(dup, Is.True, "duplicate tag must throw");

        bool ooo = false;
        using (PooledByteBufferWriter p2 = new(1024))
        {
            using HsstByteTagMapBuilder<PooledByteBufferWriter.Writer> b2 = new(ref p2.GetWriter());
            b2.Add(0x05, [0x01]);
            try { b2.Add(0x04, [0x02]); } catch (ArgumentException) { ooo = true; }
        }
        Assert.That(ooo, Is.True, "out-of-order tag must throw");

        bool over = false;
        using (PooledByteBufferWriter p3 = new(64 * 1024))
        {
            using HsstByteTagMapBuilder<PooledByteBufferWriter.Writer> b3 = new(ref p3.GetWriter());
            for (int i = 0; i < HsstByteTagMapBuilder<PooledByteBufferWriter.Writer>.MaxEntries; i++)
                b3.Add((byte)i, [(byte)i]);
            // 256 distinct byte tags exhaust the keyspace; the next Add must throw on the count cap
            // before the ascending check rejects the duplicate.
            try { b3.Add(0xFF, [0xFF]); } catch (InvalidOperationException) { over = true; }
        }
        Assert.That(over, Is.True, "exceeding MaxEntries must throw");

        bool multi = false;
        using (PooledByteBufferWriter p4 = new(1024))
        {
            using HsstByteTagMapBuilder<PooledByteBufferWriter.Writer> b4 = new(ref p4.GetWriter());
            try { b4.Add([0x05, 0x06], [0x01]); } catch (ArgumentException) { multi = true; }
        }
        Assert.That(multi, Is.True, "multi-byte tag span must throw");
    }

    [Test]
    public void Empty_BuildThrows()
    {
        // The Count byte stores N - 1 so the empty map cannot be represented; callers
        // must skip Build() for zero-entry maps.
        bool threw = false;
        using (PooledByteBufferWriter p = new(64))
        {
            using HsstByteTagMapBuilder<PooledByteBufferWriter.Writer> b = new(ref p.GetWriter());
            try { b.Build(); } catch (InvalidOperationException) { threw = true; }
        }
        Assert.That(threw, Is.True, "Build on an empty ByteTagMap must throw");
    }

    [Test]
    public void TrailerLayout_MatchesSpec_3EntryFixture()
    {
        // Three entries: tag 0x01 → "AB", tag 0x02 → "" (empty), tag 0x03 → "Z".
        byte[] data = Build([0x01, 0x02, 0x03], ["AB"u8.ToArray(), [], "Z"u8.ToArray()]);

        // valuesTotal = 3 ≤ 255 → OffsetSize = 1.
        // Expected layout: [Value_0=2][Value_1=0][Value_2=1][Ends: 3*1][Tags: 3][Count:1][OffsetSize:1][IndexType:1]
        // Ends: [2, 2, 3] (cumulative end offsets from byte 0 of HSST). Count stores N-1 = 2.
        Assert.That(data.Length, Is.EqualTo(2 + 0 + 1 + 3 + 3 + 1 + 1 + 1));
        Assert.That(data[^1], Is.EqualTo((byte)IndexType.ByteTagMap));
        Assert.That(data[^2], Is.EqualTo((byte)1)); // OffsetSize
        Assert.That(data[^3], Is.EqualTo((byte)2)); // Count = N - 1
        // Tags adjacent to count.
        Assert.That(data[^6..^3], Is.EqualTo(new byte[] { 0x01, 0x02, 0x03 }));
        // Ends right before tags: 3 single-byte LE values.
        ReadOnlySpan<byte> endsSpan = data.AsSpan(data.Length - 6 - 3, 3);
        Assert.That(endsSpan[0], Is.EqualTo((byte)2));
        Assert.That(endsSpan[1], Is.EqualTo((byte)2));
        Assert.That(endsSpan[2], Is.EqualTo((byte)3));
        // Values up front.
        Assert.That(data[..2], Is.EqualTo("AB"u8.ToArray()));
        Assert.That(data[2], Is.EqualTo((byte)'Z'));
    }

    [Test]
    public void OffsetSize_GrowsWithValuesTotal_AndRoundTripsCorrectly()
    {
        // For each target OffsetSize regime, build a small ByteTagMap whose cumulative
        // values total falls into that bucket, then verify the trailer's OffsetSize byte
        // and that every entry round-trips by lookup and by enumeration.
        // OffsetSize = 6 would require >4 GiB of payload — skipped for cost reasons.
        (int valLen, int expectedOffsetSize)[] cases =
        [
            (50, 1),     // 4 entries × 50 bytes = 200 ≤ 255
            (300, 2),    // 4 entries × 300 = 1200 > 255 → OffsetSize 2
            (20_000, 4), // 4 entries × 20000 = 80000 > 65535 → OffsetSize 4
        ];

        foreach ((int valLen, int expectedOffsetSize) in cases)
        {
            byte[] tags = [0x10, 0x20, 0x40, 0x80];
            byte[][] vals = new byte[4][];
            for (int i = 0; i < 4; i++)
            {
                vals[i] = new byte[valLen];
                for (int k = 0; k < valLen; k++) vals[i][k] = (byte)((i * 31 + k) & 0xff);
            }

            byte[] data = Build(tags, vals);
            Assert.That(data[^1], Is.EqualTo((byte)IndexType.ByteTagMap));
            Assert.That(data[^2], Is.EqualTo((byte)expectedOffsetSize),
                $"valLen={valLen} expected OffsetSize {expectedOffsetSize} but trailer says {data[^2]}");
            Assert.That(data[^3], Is.EqualTo((byte)3));

            // Round-trip via lookup.
            for (int i = 0; i < 4; i++)
            {
                Assert.That(TryGet(data, [tags[i]], out byte[] got), Is.True);
                Assert.That(got, Is.EqualTo(vals[i]));
            }
            // Round-trip via enumeration.
            List<(byte Tag, byte[] Value)> mat = Materialize(data);
            Assert.That(mat.Count, Is.EqualTo(4));
            for (int i = 0; i < 4; i++)
            {
                Assert.That(mat[i].Tag, Is.EqualTo(tags[i]));
                Assert.That(mat[i].Value, Is.EqualTo(vals[i]));
            }
        }
    }
}
