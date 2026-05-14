// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Core.Extensions;
using Nethermind.State.Flat.Hsst;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class HsstTwoByteSlotValueTests
{
    private static byte[] Build(byte[][] keys, byte[][] values)
    {
        Assert.That(keys.Length, Is.EqualTo(values.Length));
        using PooledByteBufferWriter pooled = new(64 * 1024);
        using HsstTwoByteSlotValueBuilder<PooledByteBufferWriter.Writer> b = new(ref pooled.GetWriter());
        for (int i = 0; i < keys.Length; i++) b.Add(keys[i], values[i]);
        b.Build();
        return pooled.WrittenSpan.ToArray();
    }

    private static bool TryGet(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key, out byte[] value)
    {
        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        if (!r.TrySeek(key, out _)) { value = []; return false; }
        Bound b = r.GetBound();
        value = b.Length == 0 ? [] : data.Slice((int)b.Offset, (int)b.Length).ToArray();
        return true;
    }

    private static bool TryGetFloor(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key, out byte[] value)
    {
        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        if (!r.TrySeekFloor(key, out _)) { value = []; return false; }
        Bound b = r.GetBound();
        value = b.Length == 0 ? [] : data.Slice((int)b.Offset, (int)b.Length).ToArray();
        return true;
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(7)]
    [TestCase(32)]
    [TestCase(256)]
    [TestCase(1024)]
    public void RoundTrip_HitsAndMisses(int n)
    {
        // n unique ascending 2-byte keys; deterministic variable-length values
        // (some empty to exercise the zero-length / "deleted" marker path).
        byte[][] keys = new byte[n][];
        byte[][] vals = new byte[n][];
        // Spread keys across the 2-byte space.
        int stride = Math.Max(1, 65536 / Math.Max(1, n));
        for (int i = 0; i < n; i++)
        {
            ushort k = (ushort)(i * stride);
            keys[i] = [(byte)(k >> 8), (byte)(k & 0xff)];
            int len = (i % 7 == 0) ? 0 : (i % 31) + 1;
            vals[i] = new byte[len];
            for (int j = 0; j < len; j++) vals[i][j] = (byte)((i * 17 + j * 13) & 0xff);
        }

        byte[] data = Build(keys, vals);

        // Trailer pin: last byte = IndexType, prev 2 bytes = N-1 u16 LE.
        Assert.That(data[^1], Is.EqualTo((byte)IndexType.TwoByteSlotValue));
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(data.Length - 3)), Is.EqualTo((ushort)(n - 1)));

        // Hits — every key returns the stored value.
        for (int i = 0; i < n; i++)
        {
            Assert.That(TryGet(data, keys[i], out byte[] got), Is.True, $"missing key #{i}");
            Assert.That(got, Is.EqualTo(vals[i]));
        }

        // Miss: a 2-byte key not in the set.
        byte[] missing = [0xab, 0xcd];
        bool present = false;
        for (int i = 0; i < n; i++) if (keys[i].AsSpan().SequenceEqual(missing)) { present = true; break; }
        if (!present)
            Assert.That(TryGet(data, missing, out _), Is.False);
    }

    [Test]
    public void ZeroLengthValues_RoundTrip()
    {
        byte[][] keys =
        [
            [0x00, 0x01],
            [0x12, 0x34],
            [0xff, 0xfe],
        ];
        byte[][] vals = [[], Bytes.FromHexString("deadbeef"), []];

        byte[] data = Build(keys, vals);

        Assert.That(TryGet(data, keys[0], out byte[] g0), Is.True);
        Assert.That(g0.Length, Is.EqualTo(0));
        Assert.That(TryGet(data, keys[1], out byte[] g1), Is.True);
        Assert.That(g1, Is.EqualTo(vals[1]));
        Assert.That(TryGet(data, keys[2], out byte[] g2), Is.True);
        Assert.That(g2.Length, Is.EqualTo(0));
    }

    [Test]
    public void Floor_BeforeFirst_Misses()
    {
        byte[][] keys = [[0x10, 0x00], [0x20, 0x00]];
        byte[][] vals = [[1], [2]];
        byte[] data = Build(keys, vals);

        Assert.That(TryGetFloor(data, [0x05, 0x00], out _), Is.False);
    }

    [Test]
    public void Floor_BetweenKeys_ReturnsPredecessor()
    {
        byte[][] keys = [[0x10, 0x00], [0x20, 0x00], [0x30, 0x00]];
        byte[][] vals = [[1, 1], [2, 2], [3, 3]];
        byte[] data = Build(keys, vals);

        // Floor of (0x25, 0x00) is (0x20, 0x00).
        Assert.That(TryGetFloor(data, [0x25, 0x00], out byte[] got), Is.True);
        Assert.That(got, Is.EqualTo(new byte[] { 2, 2 }));

        // Floor of (0xff, 0xff) clamps to the last key.
        Assert.That(TryGetFloor(data, [0xff, 0xff], out byte[] got2), Is.True);
        Assert.That(got2, Is.EqualTo(new byte[] { 3, 3 }));

        // Exact hit on a stored key uses the same path.
        Assert.That(TryGetFloor(data, [0x20, 0x00], out byte[] got3), Is.True);
        Assert.That(got3, Is.EqualTo(new byte[] { 2, 2 }));
    }

    [Test]
    public void Add_NonAscendingKey_Throws()
    {
        bool dup = false, lower = false;
        using (PooledByteBufferWriter p = new(1024))
        {
            using HsstTwoByteSlotValueBuilder<PooledByteBufferWriter.Writer> b = new(ref p.GetWriter());
            b.Add([0x10, 0x00], [1]);
            try { b.Add([0x10, 0x00], [2]); } catch (ArgumentException) { dup = true; }
        }
        using (PooledByteBufferWriter p = new(1024))
        {
            using HsstTwoByteSlotValueBuilder<PooledByteBufferWriter.Writer> b = new(ref p.GetWriter());
            b.Add([0x10, 0x00], [1]);
            try { b.Add([0x09, 0xff], [2]); } catch (ArgumentException) { lower = true; }
        }
        Assert.That(dup, Is.True, "duplicate key must throw");
        Assert.That(lower, Is.True, "lower key must throw");
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(3)]
    public void Add_WrongKeyLength_Throws(int len)
    {
        bool threw = false;
        using PooledByteBufferWriter pooled = new(1024);
        using HsstTwoByteSlotValueBuilder<PooledByteBufferWriter.Writer> b = new(ref pooled.GetWriter());
        byte[] key = new byte[len];
        try { b.Add(key, [1]); } catch (ArgumentException) { threw = true; }
        Assert.That(threw, Is.True, $"{len}-byte key must throw");
    }

    [Test]
    public void TrySeek_WrongKeyLength_ReturnsFalse()
    {
        byte[][] keys = [[0x10, 0x00]];
        byte[][] vals = [[1]];
        byte[] data = Build(keys, vals);

        Assert.That(TryGet(data, [0x10], out _), Is.False);
        Assert.That(TryGet(data, [0x10, 0x00, 0x00], out _), Is.False);
    }

    [Test]
    public void Build_EmptyMap_Throws()
    {
        bool threw = false;
        using PooledByteBufferWriter pooled = new(1024);
        using HsstTwoByteSlotValueBuilder<PooledByteBufferWriter.Writer> b = new(ref pooled.GetWriter());
        try { b.Build(); } catch (InvalidOperationException) { threw = true; }
        Assert.That(threw, Is.True, "Build on empty map must throw");
    }

    [Test]
    public void FitsInOffsetWidth_BoundaryAndOverflow()
    {
        Assert.That(HsstTwoByteSlotValueBuilder<PooledByteBufferWriter.Writer>.FitsInOffsetWidth(0), Is.True);
        Assert.That(HsstTwoByteSlotValueBuilder<PooledByteBufferWriter.Writer>.FitsInOffsetWidth(ushort.MaxValue), Is.True);
        Assert.That(HsstTwoByteSlotValueBuilder<PooledByteBufferWriter.Writer>.FitsInOffsetWidth(ushort.MaxValue + 1), Is.False);
    }

    [Test]
    public void DataOverflow_AddThrows_WhenStartCrossesU16()
    {
        // Push the running writer past ushort.MaxValue, then attempt one more Add —
        // the next FinishValueWrite must reject because its start offset overflows u16.
        bool threw = false;
        using PooledByteBufferWriter pooled = new(128 * 1024);
        using HsstTwoByteSlotValueBuilder<PooledByteBufferWriter.Writer> b = new(ref pooled.GetWriter());
        b.Add([0x00, 0x01], new byte[30000]);
        b.Add([0x00, 0x02], new byte[30000]);
        b.Add([0x00, 0x03], new byte[5600]); // running total = 65600 > 65535
        try { b.Add([0x00, 0x04], new byte[10]); } catch (InvalidOperationException) { threw = true; }
        Assert.That(threw, Is.True, "Add must throw once start offset crosses ushort.MaxValue");
    }

    [Test]
    public void DataOverflow_BuildThrows_WhenDataSizeOverflows()
    {
        // One entry whose value already exceeds the u16 data cap → Build must reject.
        bool threw = false;
        using PooledByteBufferWriter pooled = new(128 * 1024);
        using HsstTwoByteSlotValueBuilder<PooledByteBufferWriter.Writer> b = new(ref pooled.GetWriter());
        b.Add([0x00, 0x01], new byte[ushort.MaxValue + 1]);
        try { b.Build(); } catch (InvalidOperationException) { threw = true; }
        Assert.That(threw, Is.True, "Build must reject data region > ushort.MaxValue");
    }

    [Test]
    public void Trailer_Shape_PinsWireFormat()
    {
        // Three entries, 2-byte values. Validate every byte of the trailer.
        byte[][] keys =
        [
            [0x00, 0x10],
            [0x00, 0x20],
            [0x00, 0x30],
        ];
        byte[][] vals =
        [
            Bytes.FromHexString("aabb"),
            Bytes.FromHexString("ccdd"),
            Bytes.FromHexString("eeff"),
        ];

        byte[] data = Build(keys, vals);

        // Expected wire format (data: 6 bytes; trailer: 2 offsets · 2 + 3 keys · 2 + 2 keycount + 1 type = 13 bytes; total 19):
        //   data:        aa bb cc dd ee ff
        //   offsets:     02 00 04 00          (Offset_1 = 2, Offset_2 = 4)
        //   keys:        10 00 20 00 30 00    (LE-stored: input 00:10 → 10 00, etc.)
        //   keycount:    02 00                (N − 1 = 2)
        //   indextype:   05
        byte[] expected =
        [
            0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff,
            0x02, 0x00, 0x04, 0x00,
            0x10, 0x00, 0x20, 0x00, 0x30, 0x00,
            0x02, 0x00,
            0x05,
        ];
        Assert.That(data, Is.EqualTo(expected));

        // And every entry round-trips through the dispatcher.
        for (int i = 0; i < keys.Length; i++)
        {
            Assert.That(TryGet(data, keys[i], out byte[] got), Is.True);
            Assert.That(got, Is.EqualTo(vals[i]));
        }
    }

    [Test]
    public void Enumerator_WalksInKeyOrder()
    {
        byte[][] keys =
        [
            [0x00, 0x10],
            [0x12, 0x34],
            [0xab, 0xcd],
            [0xff, 0xfe],
        ];
        byte[][] vals = [[1], [], [2, 3, 4], [5]];
        byte[] data = Build(keys, vals);

        SpanByteReader reader = new(data);
        List<(byte[] Key, byte[] Value)> walked = [];
        Span<byte> keyScratch = stackalloc byte[2];
        using (HsstRefEnumerator<SpanByteReader, NoOpPin> e = new(in reader, new Bound(0, data.Length)))
        {
            while (e.MoveNext())
            {
                ReadOnlySpan<byte> k = e.CopyCurrentLogicalKey(keyScratch);
                Bound vb = e.Current.ValueBound;
                walked.Add((
                    k.ToArray(),
                    data.AsSpan().Slice((int)vb.Offset, (int)vb.Length).ToArray()));
            }
        }

        Assert.That(walked.Count, Is.EqualTo(keys.Length));
        for (int i = 0; i < keys.Length; i++)
        {
            Assert.That(walked[i].Key, Is.EqualTo(keys[i]), $"key #{i}");
            Assert.That(walked[i].Value, Is.EqualTo(vals[i]), $"value #{i}");
        }
    }
}
