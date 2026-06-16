// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.State.Flat.Hsst;
using NUnit.Framework;
using Nethermind.State.Flat.Hsst.DenseByteIndex;
using Nethermind.State.Flat.Hsst.PackedArray;

namespace Nethermind.State.Flat.Test.Hsst;

[TestFixture]
public class HsstDenseByteIndexTests
{
    private static byte[] Build(byte[] tags, byte[][] values)
    {
        Assert.That(tags.Length, Is.EqualTo(values.Length));
        using PooledByteBufferWriter pooled = new(64 * 1024);
        using HsstDenseByteIndexBuilder<PooledByteBufferWriter.Writer> b = new(ref pooled.GetWriter());
        // Tests pass tags in ascending (semantic) order for readability. The builder
        // requires strictly descending insertion, so the helper feeds them tail-first.
        for (int i = tags.Length - 1; i >= 0; i--) b.Add(tags[i], values[i]);
        b.Build();
        return pooled.WrittenSpan.ToArray();
    }

    private static bool TryGet(ReadOnlySpan<byte> data, byte key, out byte[] value) =>
        HsstTestUtil.TryGet(data, key, out value);

    private static bool TryGetFloor(ReadOnlySpan<byte> data, byte key, out byte[] value) =>
        HsstTestUtil.TryGetFloor(data, key, out value);

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

    [TestCase((byte)0x05, (byte)0x05, TestName = "Reject_DuplicateTag")]
    [TestCase((byte)0x05, (byte)0x06, TestName = "Reject_AscendingTag")]
    public void RejectsNonDescendingTag(byte firstTag, byte secondTag)
    {
        bool threw = false;
        using PooledByteBufferWriter p = new(1024);
        using HsstDenseByteIndexBuilder<PooledByteBufferWriter.Writer> b = new(ref p.GetWriter());
        b.Add(firstTag, [0x01]);
        try { b.Add(secondTag, [0x02]); } catch (ArgumentException) { threw = true; }
        Assert.That(threw, Is.True,
            $"Add(0x{secondTag:X2}) after Add(0x{firstTag:X2}) must throw (strictly-descending invariant)");
    }

    [Test]
    public void RejectsMultiByteTagAndEmptyBuild()
    {
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
        // Insertion happens high → low (0x03 → 0x02 → 0x00) so physical layout is
        // [empty][Z][AB] (data section reads high-tag first).
        // Position 0x01 is gap-filled empty → N = 4. valuesTotal = 3 ≤ 255 → OffsetSize = 1.
        byte[] data = Build([0x00, 0x02, 0x03], ["AB"u8.ToArray(), "Z"u8.ToArray(), []]);

        // Layout: [Value_3=0][Value_2=1][Value_0=2][Ends: 4·1][Count:1][OffsetSize:1][IndexType:1]
        //       = 0 + 1 + 2 + 4 + 3 = 10
        Assert.That(data.Length, Is.EqualTo(2 + 1 + 4 + 3));
        Assert.That(data[^1], Is.EqualTo((byte)IndexType.DenseByteIndex));
        Assert.That(data[^2], Is.EqualTo((byte)1)); // OffsetSize
        Assert.That(data[^3], Is.EqualTo((byte)3)); // N - 1

        // Ends indexed by tag value (still ascending): Ends[0]=3, Ends[1]=1 (below-range gap-fill,
        // = Ends[2]), Ends[2]=1, Ends[3]=0 (highest tag was first written, prevEnd = 0).
        ReadOnlySpan<byte> endsSpan = data.AsSpan(data.Length - 3 - 4, 4);
        Assert.That(endsSpan[0], Is.EqualTo((byte)3));
        Assert.That(endsSpan[1], Is.EqualTo((byte)1));
        Assert.That(endsSpan[2], Is.EqualTo((byte)1));
        Assert.That(endsSpan[3], Is.EqualTo((byte)0));

        // Physical layout: empty Value_3 (0 bytes), then Value_2 = 'Z', then Value_0 = "AB".
        Assert.That(data[0], Is.EqualTo((byte)'Z'));
        Assert.That(data[1..3], Is.EqualTo("AB"u8.ToArray()));
    }

    /// <summary>
    /// IByteBufferWriter that tracks position as <see cref="long"/> but only retains
    /// bytes the caller actually writes via <see cref="GetSpan"/>+<see cref="Advance"/>.
    /// "Skip" Advances (count larger than the scratch tail) bump <see cref="Written"/>
    /// without growing the scratch — used by the &gt;4 GiB DenseByteIndex test below to
    /// fast-forward through fake value bodies without allocating multi-GiB buffers.
    /// </summary>
    private struct LongAdvanceOnlyWriter(byte[] scratch) : IByteBufferWriter
    {
        private readonly byte[] _scratch = scratch;
        private int _scratchCursor;
        private long _written;

        public Span<byte> GetSpan(int sizeHint)
        {
            if (sizeHint > _scratch.Length - _scratchCursor)
                throw new InvalidOperationException(
                    $"LongAdvanceOnlyWriter scratch exhausted: need {sizeHint}, have {_scratch.Length - _scratchCursor}");
            return _scratch.AsSpan(_scratchCursor);
        }

        public void Advance(int count)
        {
            _written += count;
            // Only move the scratch cursor when the advance fits; treats large
            // advances as "skipped value bytes" that don't need to be retained.
            if (count <= _scratch.Length - _scratchCursor)
                _scratchCursor += count;
        }

        public readonly long Written => _written;
        public readonly long FirstOffset => 0;
        public readonly ReadOnlySpan<byte> ScratchTrailer => _scratch.AsSpan(0, _scratchCursor);
    }

    [Test]
    public void OffsetSize6_AboveUInt32Max_TrailerEncodesCumulativeEndsAsU48LE()
    {
        // Three entries each with a value of int.MaxValue bytes (≈2.147 GiB). Cumulative
        // ends: ~2.15 GiB, ~4.29 GiB, ~6.44 GiB. The last end exceeds uint.MaxValue, so
        // ChooseOffsetSize must select 6 (u48 LE) — exercising the >4 GiB DenseByteIndex
        // format that the long-finality compactor relies on.
        //
        // Insertion is high-tag → low-tag: tag 2 first (Ends[2] = step), then tag 1
        // (Ends[1] = 2·step), then tag 0 (Ends[0] = 3·step).
        byte[] scratch = new byte[4096];
        LongAdvanceOnlyWriter writer = new(scratch);
        long step = int.MaxValue; // 2_147_483_647
        long[] expectedEnds = [step * 3, step * 2, step];

        using (HsstDenseByteIndexBuilder<LongAdvanceOnlyWriter> b = new(ref writer))
        {
            for (int tag = 2; tag >= 0; tag--)
            {
                b.BeginValueWrite();
                writer.Advance(int.MaxValue);
                b.FinishValueWrite((byte)tag);
            }
            b.Build();
        }

        ReadOnlySpan<byte> trailer = writer.ScratchTrailer;
        // 3 ends × 6 bytes + 3-byte trailer = 21 bytes total in scratch.
        Assert.That(trailer.Length, Is.EqualTo(3 * 6 + 3));

        Assert.That(trailer[^1], Is.EqualTo((byte)IndexType.DenseByteIndex));
        Assert.That(trailer[^2], Is.EqualTo((byte)6), "OffsetSize must be 6 once cumulative ends exceed uint.MaxValue");
        Assert.That(trailer[^3], Is.EqualTo((byte)2), "Count = N - 1 with N = highestTag + 1 = 3");

        // Decode the three u48 LE end offsets and check exact values.
        Span<byte> u64 = stackalloc byte[8];
        for (int i = 0; i < 3; i++)
        {
            u64.Clear();
            trailer.Slice(i * 6, 6).CopyTo(u64);
            long end = (long)BinaryPrimitives.ReadUInt64LittleEndian(u64);
            Assert.That(end, Is.EqualTo(expectedEnds[i]), $"end[{i}] u48 LE mismatch");
        }
        Assert.That(writer.Written, Is.EqualTo(3L * int.MaxValue + 3 * 6 + 3),
            "writer position must reflect 3 fake values + ends section + trailer");
    }

    /// <summary>
    /// Stub <see cref="IHsstByteReader{TPin}"/> whose logical <see cref="Length"/> exceeds
    /// <see cref="int.MaxValue"/> but only physically backs a small <c>trailer</c> at the tail.
    /// The DenseByteIndex reader only ever touches bytes in the trailer (IndexType byte,
    /// Count+OffsetSize, and the Ends array immediately before them), so we don't need to
    /// allocate the multi-GiB value region the trailer claims exists. Any read outside the
    /// trailer is treated as a test bug and fails the call.
    /// </summary>
    private readonly ref struct TrailerOnlyLongReader : IHsstByteReader<NoOpPin>
    {
        private readonly long _length;
        private readonly long _trailerStart;
        private readonly ReadOnlySpan<byte> _trailer;

        public TrailerOnlyLongReader(long length, ReadOnlySpan<byte> trailer)
        {
            _length = length;
            _trailerStart = length - trailer.Length;
            _trailer = trailer;
        }

        public long Length => _length;

        public bool TryRead(long offset, scoped Span<byte> output)
        {
            if (offset < _trailerStart || offset + output.Length > _length) return false;
            int srcOff = (int)(offset - _trailerStart);
            _trailer.Slice(srcOff, output.Length).CopyTo(output);
            return true;
        }

        public NoOpPin PinBuffer(Bound bound)
        {
            long offset = bound.Offset;
            long size = bound.Length;
            if (offset < _trailerStart || offset + size > _length)
                throw new InvalidOperationException(
                    $"TrailerOnlyLongReader: read outside trailer [{_trailerStart}, {_length}) at offset {offset} size {size}");
            int srcOff = (int)(offset - _trailerStart);
            return new NoOpPin(_trailer.Slice(srcOff, (int)size));
        }

        public void Prefetch(long offset) { }
    }

    /// <summary>
    /// Regression for the long-finality bug where the DenseByteIndex reader's
    /// <c>valueLen &gt; int.MaxValue → false</c> guard refused to resolve a column whose
    /// single value exceeded 2 GiB. The bug silently made the outer <c>TrySeek(0x01)</c> on
    /// the compacted snapshot's <c>AccountColumn</c> return false once the column crossed
    /// the 2 GiB mark, losing every account/slot/storage/self-destruct entry. <see cref="Bound"/>
    /// is long-typed; the producer (HsstPackedArrayLayout.ChooseOffsetSize → 6-byte u48 ends) already
    /// supports up to 256 TiB, so the reader must too.
    /// </summary>
    [Test]
    public void TrySeek_ResolvesColumnAbove2GiB_Regression()
    {
        // Build a 2-entry DenseByteIndex via the no-alloc writer:
        //   tag 0x01 → value of 1024 bytes (small, written first under the descending contract)
        //   tag 0x00 → value of 2_500_000_000 bytes (> int.MaxValue, triggers the bug)
        // Tag 0x00's prevEnd = Ends[1] = 1024 (small); tag 0x01's prevEnd = 0 (highest tag).
        const long BigValueSize = 2_500_000_000L;
        const int SmallValueSize = 1024;
        byte[] scratch = new byte[64];
        LongAdvanceOnlyWriter writer = new(scratch);

        using (HsstDenseByteIndexBuilder<LongAdvanceOnlyWriter> b = new(ref writer))
        {
            b.BeginValueWrite();
            writer.Advance(SmallValueSize);
            b.FinishValueWrite(0x01);

            b.BeginValueWrite();
            // Advance is int-typed; cover BigValueSize in two hops.
            writer.Advance(int.MaxValue);
            writer.Advance(checked((int)(BigValueSize - int.MaxValue)));
            b.FinishValueWrite(0x00);

            b.Build();
        }

        // Total writer position = both values + trailer (ends + 3-byte tail). Cumulative ends
        // are above uint.MaxValue, so OffsetSize must be 6.
        ReadOnlySpan<byte> trailer = writer.ScratchTrailer;
        Assert.That(trailer[^1], Is.EqualTo((byte)IndexType.DenseByteIndex));
        // Cumulative ends are ~2.5 GiB which fits in 4 bytes (uint.MaxValue ≈ 4.29 GiB) —
        // OffsetSize stays at 4 here; the regression is independent of stride width.
        Assert.That(trailer[^2], Is.EqualTo((byte)4));
        Assert.That(trailer[^3], Is.EqualTo((byte)1), "Count = N - 1 with N = 2");

        long total = writer.Written;
        TrailerOnlyLongReader reader = new(total, trailer);

        // tag 0x01 was written first → physically at offset 0, length 1024.
        using (HsstReader<TrailerOnlyLongReader, NoOpPin> r = new(in reader))
        {
            Assert.That(r.TrySeek([0x01], out Bound b1), Is.True);
            Assert.That(b1.Offset, Is.EqualTo(0L));
            Assert.That(b1.Length, Is.EqualTo((long)SmallValueSize));
        }

        // tag 0x00 occupies [SmallValueSize, SmallValueSize + BigValueSize); its Length > int.MaxValue.
        using (HsstReader<TrailerOnlyLongReader, NoOpPin> r = new(in reader))
        {
            Assert.That(r.TrySeek([0x00], out Bound b0), Is.True,
                "TrySeek(0x00) must succeed for a column whose value exceeds int.MaxValue");
            Assert.That(b0.Offset, Is.EqualTo((long)SmallValueSize));
            Assert.That(b0.Length, Is.EqualTo(BigValueSize));
        }
    }

    [TestCase(50, 1)]     // 4 entries × 50 = 200 ≤ 255
    [TestCase(300, 2)]    // 4 entries × 300 = 1200 > 255 → OffsetSize 2
    [TestCase(20_000, 4)] // 4 entries × 20000 = 80000 > 65535 → OffsetSize 4
    public void OffsetSize_GrowsWithValuesTotal_AndRoundTripsCorrectly(int valLen, int expectedOffsetSize)
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

    /// <summary>
    /// Helper: exact-match single-tag resolution via the per-address fast path
    /// (<see cref="HsstDenseByteIndexReader.TryResolveSingleTag{TReader,TPin}"/>).
    /// </summary>
    private static bool TryResolveSingleTag(ReadOnlySpan<byte> data, byte tag, out byte[] value)
    {
        SpanByteReader reader = new(data);
        bool ok = HsstDenseByteIndexReader.TryResolveSingleTag<SpanByteReader, NoOpPin>(
            in reader, new Bound(0, data.Length), tag, out Bound b);
        if (!ok) { value = []; return false; }
        value = b.Length == 0 ? [] : data.Slice((int)b.Offset, (int)b.Length).ToArray();
        return true;
    }

    [TestCase(50, 1)]     // OffsetSize 1 (cumulative ≤ 255)
    [TestCase(300, 2)]    // OffsetSize 2 (≤ 65535)
    [TestCase(20_000, 4)] // OffsetSize 4 (> 65535)
    public void TryResolveSingleTag_RoundTripsAllOffsetSizeRegimes(int valLen, int expectedOffsetSize)
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
        Assert.That(data[^2], Is.EqualTo((byte)expectedOffsetSize));

        // Round-trip filled positions via the single-tag fast path.
        for (int i = 0; i < 4; i++)
        {
            Assert.That(TryResolveSingleTag(data, tags[i], out byte[] got), Is.True);
            Assert.That(got, Is.EqualTo(vals[i]));
        }
        // Gap positions return true with empty value (matches general TrySeek semantics).
        foreach (byte gap in new byte[] { 0x01, 0x03, 0x05 })
        {
            Assert.That(TryResolveSingleTag(data, gap, out byte[] g), Is.True);
            Assert.That(g.Length, Is.EqualTo(0));
        }
        // Above-range tag 0x07 misses (Count - 1 == 0x06).
        Assert.That(TryResolveSingleTag(data, 0x07, out _), Is.False);
        Assert.That(TryResolveSingleTag(data, 0xFF, out _), Is.False);
    }

    /// <summary>
    /// Stub <see cref="IHsstByteReader{TPin}"/> whose logical length is huge but only the trailing
    /// trailer bytes are physically backed. The
    /// <see cref="HsstDenseByteIndexReader.TryResolveSingleTag{TReader, TPin}"/> fast path pins
    /// a 32-byte speculative window at the end of the bound — that window straddles the (fake)
    /// value region and the real trailer. Callers pre-build a <c>specStage</c> buffer containing
    /// zeros for the fake-value bytes and the real trailer bytes at its tail; the stub returns
    /// that stage for the speculative pin so the resolver sees correctly-positioned trailer
    /// bytes at its window end.
    /// </summary>
    private readonly ref struct PaddedTrailerLongReader : IHsstByteReader<NoOpPin>
    {
        private readonly long _length;
        private readonly long _trailerStart;
        private readonly ReadOnlySpan<byte> _trailer;
        private readonly ReadOnlySpan<byte> _specStage;

        public PaddedTrailerLongReader(long length, ReadOnlySpan<byte> trailer, ReadOnlySpan<byte> specStage)
        {
            _length = length;
            _trailerStart = length - trailer.Length;
            _trailer = trailer;
            _specStage = specStage;
        }

        public long Length => _length;

        public bool TryRead(long offset, scoped Span<byte> output)
        {
            if (offset + output.Length > _length) return false;
            for (int i = 0; i < output.Length; i++)
            {
                long abs = offset + i;
                output[i] = abs >= _trailerStart
                    ? _trailer[(int)(abs - _trailerStart)]
                    : (byte)0;
            }
            return true;
        }

        public NoOpPin PinBuffer(Bound bound)
        {
            long offset = bound.Offset;
            long size = bound.Length;
            if (offset + size > _length)
                throw new InvalidOperationException($"out of bounds at {offset} size {size}");
            if (offset >= _trailerStart)
                return new NoOpPin(_trailer.Slice((int)(offset - _trailerStart), (int)size));
            // Straddling pin: speculative tail window. Expected to be end-anchored
            // (offset + size == _length) and bounded by the pre-built stage.
            if (offset + size != _length)
                throw new InvalidOperationException("non-end-anchored straddling pin not supported");
            if (size > _specStage.Length)
                throw new InvalidOperationException($"spec stage too small: need {size}, have {_specStage.Length}");
            return new NoOpPin(_specStage[..(int)size]);
        }

        public void Prefetch(long offset) { }
    }

    [Test]
    public void TryResolveSingleTag_HandlesOffsetSize6_AboveUInt32Max()
    {
        // OffsetSize 6 is exercised by the same trailer-only stub pattern as the existing
        // regression test, since real OffsetSize-6 data won't fit in memory. Build a 2-entry
        // DenseByteIndex whose cumulative ends straddle the 4-byte boundary, forcing
        // OffsetSize = 6 (the only way to express ends ≥ 4 GiB).
        const long BigValueSize = 5_000_000_000L; // > uint.MaxValue, requires OffsetSize 6
        const int SmallValueSize = 1024;
        byte[] scratch = new byte[64];
        LongAdvanceOnlyWriter writer = new(scratch);

        using (HsstDenseByteIndexBuilder<LongAdvanceOnlyWriter> b = new(ref writer))
        {
            b.BeginValueWrite();
            writer.Advance(SmallValueSize);
            b.FinishValueWrite(0x01);

            b.BeginValueWrite();
            // Advance is int-typed; cover BigValueSize via repeated int.MaxValue hops + tail.
            long remaining = BigValueSize;
            while (remaining > int.MaxValue)
            {
                writer.Advance(int.MaxValue);
                remaining -= int.MaxValue;
            }
            writer.Advance((int)remaining);
            b.FinishValueWrite(0x00);

            b.Build();
        }

        ReadOnlySpan<byte> trailer = writer.ScratchTrailer;
        Assert.That(trailer[^1], Is.EqualTo((byte)IndexType.DenseByteIndex));
        Assert.That(trailer[^2], Is.EqualTo((byte)6), "Cumulative ends > uint.MaxValue must select OffsetSize 6");

        long total = writer.Written;
        // Pre-build the speculative-window stage: zeros for the fake value-region prefix,
        // real trailer bytes at the tail. The resolver's speculative pin (size = min(32,
        // bound.Length)) lands here when winStart < trailerStart.
        byte[] specStage = new byte[32];
        trailer.CopyTo(specStage.AsSpan(specStage.Length - trailer.Length));
        PaddedTrailerLongReader reader = new(total, trailer, specStage);

        // tag 0x01 written first → physically at offset 0, length 1024.
        Assert.That(HsstDenseByteIndexReader.TryResolveSingleTag<PaddedTrailerLongReader, NoOpPin>(
            in reader, new Bound(0, total), 0x01, out Bound b1), Is.True);
        Assert.That(b1.Offset, Is.EqualTo(0L));
        Assert.That(b1.Length, Is.EqualTo((long)SmallValueSize));

        // tag 0x00 occupies [SmallValueSize, SmallValueSize + BigValueSize); Length > int.MaxValue.
        Assert.That(HsstDenseByteIndexReader.TryResolveSingleTag<PaddedTrailerLongReader, NoOpPin>(
            in reader, new Bound(0, total), 0x00, out Bound b0), Is.True);
        Assert.That(b0.Offset, Is.EqualTo((long)SmallValueSize));
        Assert.That(b0.Length, Is.EqualTo(BigValueSize));
    }

    [Test]
    public void TryResolveSingleTag_FallsBackToColdRepin_WhenTrailerExceedsSpecWindow()
    {
        // Build a DenseByteIndex with 256 tags (max addressable) at OffsetSize 2:
        // trailer = 3 + 256·2 = 515 bytes, well past the 32-byte speculative window.
        // The cold-path re-pin must still resolve every tag correctly.
        byte[] tags = new byte[256];
        byte[][] vals = new byte[256][];
        for (int i = 0; i < 256; i++)
        {
            tags[i] = (byte)i;
            // Drive cumulative ends past 255 so OffsetSize must be 2.
            int len = (i % 3 == 0) ? 0 : ((i * 7) % 13 + 1);
            vals[i] = new byte[len];
            for (int k = 0; k < len; k++) vals[i][k] = (byte)((i * 17 + k) & 0xff);
        }

        byte[] data = Build(tags, vals);
        Assert.That(data[^2], Is.EqualTo((byte)2), "Cumulative ends > 255 must select OffsetSize 2");
        // Trailer = 3 + 256*2 = 515 → forces the cold re-pin path in TryResolveSingleTag.
        int trailerSize = 3 + 256 * 2;
        Assert.That(trailerSize, Is.GreaterThan(32));

        for (int i = 0; i < 256; i++)
        {
            Assert.That(TryResolveSingleTag(data, (byte)i, out byte[] got), Is.True, $"tag 0x{i:X2}");
            Assert.That(got, Is.EqualTo(vals[i]), $"value mismatch at tag 0x{i:X2}");
        }
    }

    [Test]
    public void TryResolveSingleTag_RejectsTruncatedBound_WrongIndexType_InvalidOffsetSize()
    {
        byte[] valid = Build([0x00, 0x02], [[0xAA, 0xBB], [0xCC]]);
        SpanByteReader reader = new(valid);

        // Bound < 3: cannot hold the minimal trailer.
        Assert.That(HsstDenseByteIndexReader.TryResolveSingleTag<SpanByteReader, NoOpPin>(
            in reader, new Bound(0, 2), 0x00, out _), Is.False);

        // Wrong IndexType byte: synthesise a trailer that ends with a non-DenseByteIndex sentinel.
        byte[] wrongType = (byte[])valid.Clone();
        wrongType[^1] = (byte)IndexType.BTree;
        SpanByteReader wrongTypeReader = new(wrongType);
        Assert.That(HsstDenseByteIndexReader.TryResolveSingleTag<SpanByteReader, NoOpPin>(
            in wrongTypeReader, new Bound(0, wrongType.Length), 0x00, out _), Is.False);

        // Invalid OffsetSize: 0 isn't in {1,2,4,6}.
        byte[] badOff = (byte[])valid.Clone();
        badOff[^2] = 0;
        SpanByteReader badOffReader = new(badOff);
        Assert.That(HsstDenseByteIndexReader.TryResolveSingleTag<SpanByteReader, NoOpPin>(
            in badOffReader, new Bound(0, badOff.Length), 0x00, out _), Is.False);
    }
}
