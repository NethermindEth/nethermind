// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;
using Nethermind.State.Flat.Hsst;
using NUnit.Framework;
using Nethermind.State.Flat.Hsst.TwoByteSlot;

namespace Nethermind.State.Flat.Test.Hsst;

/// <summary>
/// Format-specific tests for the keys-first sub-slot builder
/// (<see cref="HsstTwoByteSlotValueBuilder{TWriter}"/>): the u16 / 64 KiB cumulative-cap
/// variant (offsetSize 2) and the u24 variant (offsetSize 3). Tests that exercise the same
/// shape across both widths are parameterised on a <c>bool large</c> discriminator. Generic
/// round-trip / floor / enumeration coverage lives in <see cref="HsstCrossFormatTests"/>.
/// </summary>
[TestFixture]
public class HsstTwoByteSlotValueTests
{
    private static byte[] Build(bool large, byte[][] keys, byte[][] values) =>
        Build(large ? 3 : 2, keys, values);

    /// <summary>
    /// Builds with the offset width chosen automatically from the cumulative payload size,
    /// exactly as production does (see <see cref="HsstTwoByteSlotValueBuilder{TWriter}.FitsInOffsetWidth"/>
    /// callers in the merger / snapshot builder): u16 while it fits the cap, u24 once it overflows.
    /// </summary>
    private static byte[] BuildAuto(byte[][] keys, byte[][] values)
    {
        long totalValueBytes = 0;
        foreach (byte[] v in values) totalValueBytes += v.Length;
        int offsetSize = HsstTwoByteSlotValueBuilder<PooledByteBufferWriter.Writer>.FitsInOffsetWidth(totalValueBytes) ? 2 : 3;
        return Build(offsetSize, keys, values);
    }

    private static byte[] Build(int offsetSize, byte[][] keys, byte[][] values)
    {
        Assert.That(keys.Length, Is.EqualTo(values.Length));
        using PooledByteBufferWriter pooled = new(64 * 1024);
        using (HsstTwoByteSlotValueBuilder<PooledByteBufferWriter.Writer> b = new(ref pooled.GetWriter(), offsetSize))
        {
            for (int i = 0; i < keys.Length; i++) b.Add(keys[i], values[i]);
            b.Build();
        }
        return pooled.WrittenSpan.ToArray();
    }

    private static bool TryGet(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key, out byte[] value) =>
        HsstTestUtil.TryGetTwoByteSlot(data, key, out value);

    [TestCase(false)]
    [TestCase(true)]
    public void Add_NonAscendingKey_Throws(bool large)
    {
        // Duplicate key.
        Assert.Throws<ArgumentException>(() =>
        {
            using PooledByteBufferWriter p = new(1024);
            using HsstTwoByteSlotValueBuilder<PooledByteBufferWriter.Writer> b = new(ref p.GetWriter(), large ? 3 : 2);
            b.Add([0x10, 0x00], [1]);
            b.Add([0x10, 0x00], [2]);
        }, "duplicate key must throw");

        // Strictly-lower key.
        Assert.Throws<ArgumentException>(() =>
        {
            using PooledByteBufferWriter p = new(1024);
            using HsstTwoByteSlotValueBuilder<PooledByteBufferWriter.Writer> b = new(ref p.GetWriter(), large ? 3 : 2);
            b.Add([0x10, 0x00], [1]);
            b.Add([0x09, 0xff], [2]);
        }, "lower key must throw");
    }

    [TestCase(false, 0)]
    [TestCase(false, 1)]
    [TestCase(false, 3)]
    [TestCase(true, 0)]
    [TestCase(true, 1)]
    [TestCase(true, 3)]
    public void Add_WrongKeyLength_Throws(bool large, int len)
    {
        byte[] key = new byte[len];
        Assert.Throws<ArgumentException>(() =>
        {
            using PooledByteBufferWriter pooled = new(1024);
            using HsstTwoByteSlotValueBuilder<PooledByteBufferWriter.Writer> b = new(ref pooled.GetWriter(), large ? 3 : 2);
            b.Add(key, [1]);
        }, $"{len}-byte key must throw");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TrySeek_WrongKeyLength_ReturnsFalse(bool large)
    {
        byte[][] keys = [[0x10, 0x00]];
        byte[][] vals = [[1]];
        byte[] data = Build(large, keys, vals);

        Assert.That(TryGet(data, [0x10], out _), Is.False);
        Assert.That(TryGet(data, [0x10, 0x00, 0x00], out _), Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Build_EmptyMap_Throws(bool large) =>
        Assert.Throws<InvalidOperationException>(() =>
        {
            using PooledByteBufferWriter pooled = new(1024);
            using HsstTwoByteSlotValueBuilder<PooledByteBufferWriter.Writer> b = new(ref pooled.GetWriter(), large ? 3 : 2);
            b.Build();
        }, "Build on empty map must throw");

    [Test]
    public void FitsInOffsetWidth_BoundaryAndOverflow_U16()
    {
        Assert.That(HsstTwoByteSlotValueBuilder<PooledByteBufferWriter.Writer>.FitsInOffsetWidth(0), Is.True);
        Assert.That(HsstTwoByteSlotValueBuilder<PooledByteBufferWriter.Writer>.FitsInOffsetWidth(ushort.MaxValue), Is.True);
        Assert.That(HsstTwoByteSlotValueBuilder<PooledByteBufferWriter.Writer>.FitsInOffsetWidth(ushort.MaxValue + 1), Is.False);
    }

    [Test]
    public void DataOverflow_AddThrows_WhenCumulativeCrossesU16()
    {
        // Push the cumulative payload past ushort.MaxValue — Add itself rejects (the
        // u16 builder needs every offset to fit u16, so the trip-wire fires the moment
        // a new entry would push the running total above the cap rather than waiting
        // for Build).
        Assert.Throws<InvalidOperationException>(() =>
        {
            using PooledByteBufferWriter pooled = new(128 * 1024);
            using HsstTwoByteSlotValueBuilder<PooledByteBufferWriter.Writer> b = new(ref pooled.GetWriter());
            b.Add([0x00, 0x01], new byte[30000]);
            b.Add([0x00, 0x02], new byte[30000]);
            b.Add([0x00, 0x03], new byte[5600]);
        }, "Add must throw once cumulative crosses ushort.MaxValue");

        Assert.Throws<InvalidOperationException>(() =>
        {
            using PooledByteBufferWriter pooled = new(128 * 1024);
            using HsstTwoByteSlotValueBuilder<PooledByteBufferWriter.Writer> b = new(ref pooled.GetWriter());
            b.Add([0x00, 0x01], new byte[ushort.MaxValue + 1]);
        }, "Add must throw on a single value > ushort.MaxValue");
    }

    [Test]
    public void RoundTrip_PayloadExceedsU16Cap_RequiresU24()
    {
        // 3000 × 32 = 96 KiB > ushort.MaxValue: this is the regime that forces the u24
        // builder's wider offsets. Let the offset width be chosen automatically (as
        // production does) and assert it promotes to the large variant. Spot-check entries
        // at the start, middle, and end — including ones whose data offset is > 65,535 — to
        // ensure the u24 offset path resolves correctly.
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

        byte[] data = BuildAuto(keys, vals);
        Assert.That(data[0], Is.EqualTo((byte)IndexType.TwoByteSlotValueLarge));

        foreach (int idx in new[] { 0, n / 2, n - 1 })
        {
            Assert.That(TryGet(data, keys[idx], out byte[] got), Is.True, $"missing key #{idx}");
            Assert.That(got, Is.EqualTo(vals[idx]));
        }
    }

    [Test]
    public void WireFormat_KeysFirst_PinsBytes_U16()
    {
        // Three entries, 2-byte values. Validate every byte of the keys-first layout:
        // leading IndexType byte + header (KeyCount) + keys + offsets + values.
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

        byte[] data = Build(large: false, keys, vals);

        // Expected wire format (total 19 bytes):
        //   indextype:   05
        //   keycount:    02 00                (N − 1 = 2)
        //   keys:        10 00 20 00 30 00    (LE-stored: input 00:10 → 10 00, etc.)
        //   offsets:     02 00 04 00          (Offset_1 = 2, Offset_2 = 4, relative to values start)
        //   values:      aa bb cc dd ee ff
        byte[] expected =
        [
            0x05,
            0x02, 0x00,
            0x10, 0x00, 0x20, 0x00, 0x30, 0x00,
            0x02, 0x00, 0x04, 0x00,
            0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff,
        ];
        Assert.That(data, Is.EqualTo(expected));

        for (int i = 0; i < keys.Length; i++)
        {
            Assert.That(TryGet(data, keys[i], out byte[] got), Is.True);
            Assert.That(got, Is.EqualTo(vals[i]));
        }
    }

    [Test]
    public void WireFormat_KeysFirst_PinsBytes_U24()
    {
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

        byte[] data = Build(large: true, keys, vals);

        // Expected wire format (total 21 bytes):
        //   indextype:   06                          (1)
        //   keycount:    02 00                       (N − 1 = 2)
        //   keys:        10 00 20 00 30 00           (LE-stored, 3·2)
        //   offsets:     02 00 00 04 00 00           (2·3 = 6, Offset_1 = 2 u24 LE, Offset_2 = 4 u24 LE)
        //   values:      aa bb cc dd ee ff           (6)
        byte[] expected =
        [
            0x06,
            0x02, 0x00,
            0x10, 0x00, 0x20, 0x00, 0x30, 0x00,
            0x02, 0x00, 0x00, 0x04, 0x00, 0x00,
            0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff,
        ];
        Assert.That(data, Is.EqualTo(expected));

        for (int i = 0; i < keys.Length; i++)
        {
            Assert.That(TryGet(data, keys[i], out byte[] got), Is.True);
            Assert.That(got, Is.EqualTo(vals[i]));
        }
    }
}
