// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.State.Flat.Hsst;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class HsstBTreeKeyFirstTests
{
    private static bool TryGet(ReadOnlySpan<byte> data, scoped ReadOnlySpan<byte> key, out byte[] value)
    {
        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        if (!r.TrySeek(key, out _)) { value = []; return false; }
        Bound b = r.GetBound();
        value = b.Length == 0 ? [] : data.Slice((int)b.Offset, (int)b.Length).ToArray();
        return true;
    }

    [Test]
    public void IndexType_Byte_Is_BTreeKeyFirst_At_Tail()
    {
        byte[] data = HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> b) =>
        {
            b.Add("key"u8, "value"u8);
        }, keyFirst: true);

        Assert.That(data[^1], Is.EqualTo((byte)IndexType.BTreeKeyFirst));
    }

    [Test]
    public void BeginValueWrite_Throws_InKeyFirstMode()
    {
        using PooledByteBufferWriter pooled = new(1024);
        HsstBTreeBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder = new(
            ref pooled.GetWriter(), keyLength: 4, options: null, expectedKeyCount: 4, keyFirst: true);
        try
        {
            bool threw = false;
            try { _ = builder.BeginValueWrite(); } catch (InvalidOperationException) { threw = true; }
            Assert.That(threw, Is.True, "BeginValueWrite must reject in key-first mode");
        }
        finally
        {
            builder.Dispose();
        }
    }

    [Test]
    public void Nested_KeyFirstBTree_Over_KeysFirstSubSlot_RoundTrips()
    {
        // Outer: 4-byte key BTree (key-first).
        // Inner: 2-byte key TwoByteSlotValue (keys-first), wrapped as the outer's value.
        byte[][] outerKeys = [
            [0xaa, 0xbb, 0xcc, 0x01],
            [0xaa, 0xbb, 0xcc, 0x02],
            [0xaa, 0xbb, 0xcc, 0x03],
        ];
        byte[][][] innerKeysPer = [
            [[0x00, 0x10], [0x00, 0x20]],
            [[0x00, 0x10], [0x00, 0x30]],
            [[0x00, 0x20]],
        ];
        byte[][][] innerValsPer = [
            [[1, 2, 3], [4, 5]],
            [[6], [7, 8, 9, 10]],
            [[11, 12, 13, 14, 15]],
        ];

        byte[] outerBytes = HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> outer) =>
        {
            using PooledByteBufferWriter staging = new(4096);
            for (int o = 0; o < outerKeys.Length; o++)
            {
                staging.Reset();
                ref PooledByteBufferWriter.Writer w = ref staging.GetWriter();
                using HsstTwoByteSlotValueBuilder<PooledByteBufferWriter.Writer> inner = new(ref w);
                for (int i = 0; i < innerKeysPer[o].Length; i++) inner.Add(innerKeysPer[o][i], innerValsPer[o][i]);
                inner.Build();
                outer.Add(outerKeys[o], staging.WrittenSpan);
            }
        }, keyFirst: true);

        Assert.That(outerBytes[^1], Is.EqualTo((byte)IndexType.BTreeKeyFirst));

        // For each outer key, descend into the inner sub-slot and verify each entry.
        for (int o = 0; o < outerKeys.Length; o++)
        {
            SpanByteReader rdr = new(outerBytes);
            using HsstReader<SpanByteReader, NoOpPin> r = new(in rdr);
            Assert.That(r.TrySeek(outerKeys[o], out _), Is.True, $"outer {o} missing");
            Bound innerBound = r.GetBound();
            ReadOnlySpan<byte> innerBytes = outerBytes.AsSpan((int)innerBound.Offset, (int)innerBound.Length);

            // Inner trailer must be the keys-first sub-slot type.
            Assert.That(innerBytes[^1], Is.EqualTo((byte)IndexType.TwoByteSlotValue));

            for (int i = 0; i < innerKeysPer[o].Length; i++)
            {
                Assert.That(TryGet(innerBytes, innerKeysPer[o][i], out byte[] got), Is.True, $"outer {o} inner {i} missing");
                Assert.That(got, Is.EqualTo(innerValsPer[o][i]), $"outer {o} inner {i} value mismatch");
            }
        }
    }
}
