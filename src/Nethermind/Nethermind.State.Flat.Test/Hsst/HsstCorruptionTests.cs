// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.State.Flat.Hsst;
using NUnit.Framework;
using Nethermind.State.Flat.Hsst.BTree;
using Nethermind.State.Flat.Hsst.PackedArray;
using Nethermind.State.Flat.Hsst.DenseByteIndex;
using Nethermind.State.Flat.Hsst.TwoByteSlot;

namespace Nethermind.State.Flat.Test.Hsst;

/// <summary>
/// Exercises the readers' top-level corruption detection: every entry point must reject a
/// truncated, mis-typed, or internally-inconsistent on-disk blob by returning false (or, for
/// the byte-source bounds checks, throwing) rather than reading out of bounds or crashing.
/// </summary>
[TestFixture]
public class HsstCorruptionTests
{
    private static bool TrySeek(byte[] data, Bound bound, ReadOnlySpan<byte> key)
    {
        SpanByteReader r = new(data);
        using HsstReader<SpanByteReader, NoOpPin> hr = new(in r, bound);
        return hr.TrySeek(key, out _);
    }

    private static bool TrySeekTwoByteSlot(byte[] data, Bound bound, ReadOnlySpan<byte> key)
    {
        SpanByteReader r = new(data);
        using HsstReader<SpanByteReader, NoOpPin> hr = new(in r, bound);
        return hr.TrySeekTwoByteSlot(key, out _);
    }

    private static byte[] BuildBTree() =>
        HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer> b) =>
        {
            b.Add([0x00, 0x01, 0x02, 0x03], "v0"u8);
            b.Add([0x00, 0x01, 0x02, 0x04], "v1"u8);
        });

    private static byte[] BuildPackedArray()
    {
        using PooledByteBufferWriter p = new(4096);
        HsstPackedArrayBuilder<PooledByteBufferWriter.Writer> b = new(ref p.GetWriter(), keySize: 4, valueSize: 4, expectedKeyCount: 2);
        try
        {
            b.Add([0, 0, 0, 1], [0, 0, 0, 10]);
            b.Add([0, 0, 0, 2], [0, 0, 0, 20]);
            b.Build();
            return p.WrittenSpan.ToArray();
        }
        finally { b.Dispose(); }
    }

    private static byte[] BuildDense()
    {
        using PooledByteBufferWriter p = new(4096);
        using HsstDenseByteIndexBuilder<PooledByteBufferWriter.Writer> b = new(ref p.GetWriter());
        b.Add((byte)0x02, new byte[] { 0xBB, 0xCC }); // descending insertion
        b.Add((byte)0x00, new byte[] { 0xAA });
        b.Build();
        return p.WrittenSpan.ToArray();
    }

    private static byte[] BuildTwoByteSlot()
    {
        using PooledByteBufferWriter p = new(4096);
        ref PooledByteBufferWriter.Writer w = ref p.GetWriter();
        using HsstTwoByteSlotValueBuilder<PooledByteBufferWriter.Writer> b = new(ref w);
        b.Add([0x00, 0x01], [0xAA]);
        b.Add([0x00, 0x02], [0xBB]);
        b.Build();
        return p.WrittenSpan.ToArray();
    }

    private static readonly byte[] OneByteKey = [0x00];
    private static readonly byte[] TwoByteKey = [0x00, 0x01];

    // The top-level dispatch (last IndexType byte) rejects a bound too short to even hold the
    // trailer, a bound that runs past the byte source, and an unknown/illegal IndexType byte.
    [Test]
    public void TopLevelDispatch_RejectsTruncated_Oversized_UnknownType()
    {
        byte[] data = BuildBTree();

        // Bound shorter than the 2-byte minimum the dispatcher needs.
        Assert.That(TrySeek(data, new Bound(0, 0), OneByteKey), Is.False);
        Assert.That(TrySeek(data, new Bound(0, 1), OneByteKey), Is.False);

        // Bound claims more bytes than the source has: the trailing IndexType read fails.
        Assert.That(TrySeek(data, new Bound(0, data.Length + 8), OneByteKey), Is.False);

        // A valid-but-illegal-at-top-level IndexType byte (TwoByteSlotValue is nested-only)
        // and a wholly unknown byte both fall through the switch to a false result.
        byte[] nestedAtTop = new byte[20];
        nestedAtTop[^1] = (byte)IndexType.TwoByteSlotValue;
        Assert.That(TrySeek(nestedAtTop, new Bound(0, nestedAtTop.Length), OneByteKey), Is.False);
        byte[] unknownType = new byte[20];
        unknownType[^1] = 0xEE;
        Assert.That(TrySeek(unknownType, new Bound(0, unknownType.Length), OneByteKey), Is.False);
    }

    // The keys-first two-byte-slot dispatch (leading IndexType byte at byte 0) rejects the same
    // corruption classes, plus a non-two-byte-slot leading byte.
    [Test]
    public void TwoByteSlotDispatch_RejectsTruncated_Oversized_UnknownType()
    {
        byte[] tbs = BuildTwoByteSlot();

        Assert.That(TrySeekTwoByteSlot(tbs, new Bound(0, 1), TwoByteKey), Is.False);
        // Bound whose offset starts past the source: the leading-byte read fails.
        Assert.That(TrySeekTwoByteSlot(tbs, new Bound(tbs.Length, 5), TwoByteKey), Is.False);
        // Leading byte names a non-two-byte-slot type.
        byte[] notTbs = new byte[20];
        notTbs[0] = (byte)IndexType.BTree;
        Assert.That(TrySeekTwoByteSlot(notTbs, new Bound(0, notTbs.Length), TwoByteKey), Is.False);
    }

    // Each format's TryReadLayout rejects a blob shorter than its minimal trailer, reached via
    // the real dispatch path (correct trailing/leading IndexType byte, but too few bytes).
    [Test]
    public void FormatLayout_RejectsBelowMinimumLength()
    {
        // DenseByteIndex trailer is >= 3 bytes.
        byte[] denseTooShort = [0x00, (byte)IndexType.DenseByteIndex];
        Assert.That(TrySeek(denseTooShort, new Bound(0, denseTooShort.Length), OneByteKey), Is.False);

        // PackedArray needs >= 3 bytes.
        byte[] packedTooShort = [0x00, (byte)IndexType.PackedArray];
        Assert.That(TrySeek(packedTooShort, new Bound(0, packedTooShort.Length), OneByteKey), Is.False);

        // BTree needs trailer (5) + root header (12) = 17 bytes.
        byte[] btreeTooShort = new byte[6];
        btreeTooShort[^1] = (byte)IndexType.BTree;
        Assert.That(TrySeek(btreeTooShort, new Bound(0, btreeTooShort.Length), OneByteKey), Is.False);

        // TwoByteSlotValue needs >= 5 bytes (dispatched on the leading byte).
        byte[] tbsTooShort = [(byte)IndexType.TwoByteSlotValue, 0x00];
        Assert.That(TrySeekTwoByteSlot(tbsTooShort, new Bound(0, tbsTooShort.Length), TwoByteKey), Is.False);
    }

    // A well-formed DenseByteIndex blob whose trailer fields are corrupted must be rejected:
    // an OffsetSize outside {1,2,4,6}, and a Count whose implied trailer exceeds the blob.
    [Test]
    public void DenseByteIndex_RejectsCorruptTrailerFields()
    {
        byte[] valid = BuildDense();
        Assert.That(valid[^1], Is.EqualTo((byte)IndexType.DenseByteIndex));

        // Wrong key length (single-byte index requires a 1-byte key) — rejected before lookup.
        Assert.That(TrySeek(valid, new Bound(0, valid.Length), new byte[] { 0x00, 0x00 }), Is.False);

        // Invalid OffsetSize byte (3 is not a supported width).
        byte[] badOffset = (byte[])valid.Clone();
        badOffset[^2] = 3;
        Assert.That(TrySeek(badOffset, new Bound(0, badOffset.Length), OneByteKey), Is.False);

        // Count byte (N-1) inflated so the implied Ends trailer overruns the blob.
        byte[] badCount = (byte[])valid.Clone();
        badCount[^3] = 0xFF;
        Assert.That(TrySeek(badCount, new Bound(0, badCount.Length), OneByteKey), Is.False);
    }

    // A well-formed PackedArray blob whose metadata-length byte points before the blob start
    // must be rejected by the layout reader.
    [Test]
    public void PackedArray_RejectsMetadataLengthBeforeStart()
    {
        byte[] valid = BuildPackedArray();
        Assert.That(valid[^1], Is.EqualTo((byte)IndexType.PackedArray));

        // The second-to-last byte is the metadata length; an oversized value places the
        // metadata start before the blob, which TryReadLayout rejects.
        byte[] badMeta = (byte[])valid.Clone();
        badMeta[^2] = 0xFF;
        Assert.That(TrySeek(badMeta, new Bound(0, badMeta.Length), new byte[] { 0, 0, 0, 1 }), Is.False);
    }

    [Test]
    public void TwoByteSlot_RejectsWrongKeyLength()
    {
        byte[] tbs = BuildTwoByteSlot();
        Assert.That(TrySeekTwoByteSlot(tbs, new Bound(0, tbs.Length), OneByteKey), Is.False);
    }

    // SpanByteReader is the untrusted-byte source; its own bounds checks must hold: an
    // out-of-range TryRead returns false, and an out-of-range pin throws.
    [Test]
    public void SpanByteReader_BoundsChecks()
    {
        byte[] data = new byte[8];
        SpanByteReader r = new(data);

        Span<byte> one = stackalloc byte[1];
        Assert.That(r.TryRead(data.Length, one), Is.False, "read at end-of-buffer must fail");
        Assert.That(r.TryRead(data.Length - 1, one), Is.True, "last-byte read must succeed");

        // SpanByteReader is a ref struct, so the throwing call can't be wrapped in a lambda.
        bool threw = false;
        try { r.PinBuffer(new Bound(0, data.Length + 1)); } catch (ArgumentOutOfRangeException) { threw = true; }
        Assert.That(threw, Is.True, "out-of-range pin must throw");
    }
}
