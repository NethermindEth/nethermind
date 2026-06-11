// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.State.Flat.Hsst;
using NUnit.Framework;
using Nethermind.State.Flat.Hsst.BTree;
using Nethermind.State.Flat.Hsst.TwoByteSlot;

namespace Nethermind.State.Flat.Test.Hsst;

[TestFixture]
public class HsstBTreeKeyFirstTests
{
    // Inner sub-slots are keys-first TwoByteSlotValue blobs — front-dispatched on byte 0.
    private static bool TryGet(ReadOnlySpan<byte> data, scoped ReadOnlySpan<byte> key, out byte[] value) =>
        HsstTestUtil.TryGetTwoByteSlot(data, key, out value);

    [Test]
    public void IndexType_Byte_Is_BTreeKeyFirst_At_Tail()
    {
        byte[] data = HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer> b) =>
        {
            b.Add("key"u8, "value"u8);
        }, keyFirst: true);

        Assert.That(data[^1], Is.EqualTo((byte)IndexType.BTreeKeyFirst));
    }

    [Test]
    public void BeginValueWrite_Throws_InKeyFirstMode()
    {
        using PooledByteBufferWriter pooled = new(1024);
        using HsstBTreeBuilderBuffersContainer buffers = new(expectedKeyCount: 4);
        HsstBTreeBuilder<PooledByteBufferWriter.Writer> builder = new(
            ref pooled.GetWriter(), ref buffers.Buffers, keyLength: 4, expectedKeyCount: 4, keyFirst: true);
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

        byte[] outerBytes = HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer> outer) =>
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

            // Inner blob leads with the keys-first sub-slot type byte at byte 0.
            Assert.That(innerBytes[0], Is.EqualTo((byte)IndexType.TwoByteSlotValue));

            for (int i = 0; i < innerKeysPer[o].Length; i++)
            {
                Assert.That(TryGet(innerBytes, innerKeysPer[o][i], out byte[] got), Is.True, $"outer {o} inner {i} missing");
                Assert.That(got, Is.EqualTo(innerValsPer[o][i]), $"outer {o} inner {i} value mismatch");
            }
        }
    }
}
