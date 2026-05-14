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
public class HsstTwoByteSlotValueLargeTests
{
    private static byte[] Build(byte[][] keys, byte[][] values)
    {
        Assert.That(keys.Length, Is.EqualTo(values.Length));
        using PooledByteBufferWriter pooled = new(64 * 1024);
        using HsstTwoByteSlotValueLargeBuilder<PooledByteBufferWriter.Writer> b = new(ref pooled.GetWriter());
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
    [TestCase(256)]
    [TestCase(4096)]
    public void RoundTrip_HitsAndMisses(int n)
    {
        // n unique ascending 2-byte keys; 32-byte values to push past the u16 cap
        // at higher N. With n=4096 the payload is ~128 KiB > ushort.MaxValue, so the
        // test forces the u24 path.
        byte[][] keys = new byte[n][];
        byte[][] vals = new byte[n][];
        int stride = Math.Max(1, 65536 / Math.Max(1, n));
        for (int i = 0; i < n; i++)
        {
            ushort k = (ushort)(i * stride);
            keys[i] = [(byte)(k >> 8), (byte)(k & 0xff)];
            int len = (i % 11 == 0) ? 0 : 32;
            vals[i] = new byte[len];
            for (int j = 0; j < len; j++) vals[i][j] = (byte)((i * 17 + j * 13) & 0xff);
        }

        byte[] data = Build(keys, vals);

        Assert.That(data[^1], Is.EqualTo((byte)IndexType.TwoByteSlotValueLarge));
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(data.Length - 3)), Is.EqualTo((ushort)(n - 1)));

        for (int i = 0; i < n; i++)
        {
            Assert.That(TryGet(data, keys[i], out byte[] got), Is.True, $"missing key #{i}");
            Assert.That(got, Is.EqualTo(vals[i]));
        }

        byte[] missing = [0xab, 0xcd];
        bool present = false;
        for (int i = 0; i < n; i++) if (keys[i].AsSpan().SequenceEqual(missing)) { present = true; break; }
        if (!present)
            Assert.That(TryGet(data, missing, out _), Is.False);
    }

    [Test]
    public void RoundTrip_PayloadExceedsU16Cap()
    {
        // Confirm the format handles payloads beyond TwoByteSlotValue's 64 KiB cap.
        // 3000 entries × 32 bytes = 96 KiB > 65,535, so this would overflow u16.
        const int n = 3000;
        byte[][] keys = new byte[n][];
        byte[][] vals = new byte[n][];
        for (int i = 0; i < n; i++)
        {
            ushort k = (ushort)i;
            keys[i] = [(byte)(k >> 8), (byte)(k & 0xff)];
            vals[i] = new byte[32];
            for (int j = 0; j < 32; j++) vals[i][j] = (byte)((i * 7 + j) & 0xff);
        }

        byte[] data = Build(keys, vals);
        Assert.That(data[^1], Is.EqualTo((byte)IndexType.TwoByteSlotValueLarge));
        // Spot-check a few keys including ones whose data offset is > 65,535.
        Assert.That(TryGet(data, keys[0], out byte[] g0), Is.True);
        Assert.That(g0, Is.EqualTo(vals[0]));
        int midIdx = n / 2;
        Assert.That(TryGet(data, keys[midIdx], out byte[] gm), Is.True);
        Assert.That(gm, Is.EqualTo(vals[midIdx]));
        Assert.That(TryGet(data, keys[n - 1], out byte[] gl), Is.True);
        Assert.That(gl, Is.EqualTo(vals[n - 1]));
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
    public void Floor_BetweenKeys_ReturnsPredecessor()
    {
        byte[][] keys = [[0x10, 0x00], [0x20, 0x00], [0x30, 0x00]];
        byte[][] vals = [[1, 1], [2, 2], [3, 3]];
        byte[] data = Build(keys, vals);

        Assert.That(TryGetFloor(data, [0x05, 0x00], out _), Is.False);
        Assert.That(TryGetFloor(data, [0x25, 0x00], out byte[] g1), Is.True);
        Assert.That(g1, Is.EqualTo(new byte[] { 2, 2 }));
        Assert.That(TryGetFloor(data, [0xff, 0xff], out byte[] g2), Is.True);
        Assert.That(g2, Is.EqualTo(new byte[] { 3, 3 }));
    }

    [Test]
    public void Add_NonAscendingKey_Throws()
    {
        bool dup = false, lower = false;
        using (PooledByteBufferWriter p = new(1024))
        {
            using HsstTwoByteSlotValueLargeBuilder<PooledByteBufferWriter.Writer> b = new(ref p.GetWriter());
            b.Add([0x10, 0x00], [1]);
            try { b.Add([0x10, 0x00], [2]); } catch (ArgumentException) { dup = true; }
        }
        using (PooledByteBufferWriter p = new(1024))
        {
            using HsstTwoByteSlotValueLargeBuilder<PooledByteBufferWriter.Writer> b = new(ref p.GetWriter());
            b.Add([0x10, 0x00], [1]);
            try { b.Add([0x09, 0xff], [2]); } catch (ArgumentException) { lower = true; }
        }
        Assert.That(dup, Is.True);
        Assert.That(lower, Is.True);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(3)]
    public void Add_WrongKeyLength_Throws(int len)
    {
        bool threw = false;
        using PooledByteBufferWriter pooled = new(1024);
        using HsstTwoByteSlotValueLargeBuilder<PooledByteBufferWriter.Writer> b = new(ref pooled.GetWriter());
        byte[] key = new byte[len];
        try { b.Add(key, [1]); } catch (ArgumentException) { threw = true; }
        Assert.That(threw, Is.True, $"{len}-byte key must throw");
    }

    [Test]
    public void Build_EmptyMap_Throws()
    {
        bool threw = false;
        using PooledByteBufferWriter pooled = new(1024);
        using HsstTwoByteSlotValueLargeBuilder<PooledByteBufferWriter.Writer> b = new(ref pooled.GetWriter());
        try { b.Build(); } catch (InvalidOperationException) { threw = true; }
        Assert.That(threw, Is.True, "Build on empty map must throw");
    }

    [Test]
    public void FitsInOffsetWidth_BoundaryAndOverflow()
    {
        Assert.That(HsstTwoByteSlotValueLargeBuilder<PooledByteBufferWriter.Writer>.FitsInOffsetWidth(0), Is.True);
        Assert.That(HsstTwoByteSlotValueLargeBuilder<PooledByteBufferWriter.Writer>.FitsInOffsetWidth((1 << 24) - 1), Is.True);
        Assert.That(HsstTwoByteSlotValueLargeBuilder<PooledByteBufferWriter.Writer>.FitsInOffsetWidth(1 << 24), Is.False);
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

        // Expected wire format:
        //   data:        aa bb cc dd ee ff           (6)
        //   offsets:     02 00 00 04 00 00           (2·3 = 6 bytes for Offset_1, Offset_2)
        //   keys:        10 00 20 00 30 00           (LE-stored: 3·2 = 6)
        //   keycount:    02 00                       (2)
        //   indextype:   06                          (1)
        //                Total: 21 bytes
        byte[] expected =
        [
            0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff,
            0x02, 0x00, 0x00, 0x04, 0x00, 0x00,
            0x10, 0x00, 0x20, 0x00, 0x30, 0x00,
            0x02, 0x00,
            0x06,
        ];
        Assert.That(data, Is.EqualTo(expected));

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
