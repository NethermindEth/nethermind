// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using NUnit.Framework;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.State.Flat.Hsst.BTree;
using Nethermind.State.Flat.Hsst.PackedArray;
using Nethermind.State.Flat.Hsst.DenseByteIndex;

namespace Nethermind.State.Flat.Test.Hsst;

/// <summary>
/// End-to-end smoke for the HSST builder/reader/merge path at single-HSST sizes
/// above the 2 GiB single-Span ceiling. Exercises the long-aware code paths
/// (Bound.Length, HSST index offsets, mmap-backed long-offset <see cref="ArenaByteReader"/>)
/// and verifies — on every yielded entry — that the bytes round-trip exactly,
/// not just that the entry count matches.
///
/// Each HSST is written into its own dedicated arena via a page-tracker-disabled
/// <see cref="TempDirArenaManager"/> (pageCacheBytes == 0). With the tracker disabled the
/// <see cref="ArenaByteReader"/> returned by <see cref="ArenaReservation.CreateReader"/>
/// degenerates to a pure long-offset pointer reader — no residency tracking, no madvise —
/// which is exactly what these correctness checks want. Dedicated arenas are sparse-sized
/// to a generous estimate and truncated to the bytes actually written on
/// <see cref="ArenaWriter.Complete"/>, so over-estimating costs no disk.
///
/// Two scaling strategies are used, picked by the index type's structural cap:
/// - Multi-byte-keyed indexes (BTree, PackedArray) hit &gt;2 GiB through entry
///   volume — see <see cref="BTreeEntryCount"/> / <see cref="PackedArrayEntryCount"/>.
/// - Single-byte-keyed indexes (DenseByteIndex) are hard-capped at
///   256 entries by the format, so they hit &gt;2 GiB through value size:
///   <see cref="ByteKeyEntryCount"/> × <see cref="ByteKeyValueSize"/>.
///
/// The BTree builder buffers every entry's separator + metadata in native
/// memory before writing the index region (~16 B per HsstEntry × N), which
/// makes the &gt;2 GiB scale take hours of CPU and several GiB of native heap.
/// PackedArray's per-entry buffer footprint is tiny (sparse checkpoint keys
/// only), so its run time is dominated by I/O. DenseByteIndex
/// each allocate one ~10 MiB scratch buffer that is reused across entries.
/// </summary>
[Explicit("Writes large HSSTs to /tmp; minutes to hours to run at default scale.")]
public class HsstLargeBuildTests
{
    // BTree / PackedArray (multi-byte keys): scale via entry count. Each format
    // needs its own count because their on-disk per-entry size differs — they're
    // tuned so a single HSST clears ~2.4 GiB, well past the int.MaxValue ceiling.
    // The merged HSST (2 × count entries) must keep its entry count under
    // int.MaxValue; both values leave ample headroom.
    //
    // BTree per-entry on disk ≈ 13 B (6 B key + 1 B value + LEB length + index
    // share); 200M ≈ 2.4 GiB. PackedArray uses a fixed 16 B value so it is denser
    // per entry; 150M ≈ 2.4 GiB.
    private static readonly long BTreeEntryCount = 200_000_000L;
    private static readonly long PackedArrayEntryCount = 150_000_000L;
    private const int KeySize = 6;
    private const byte BTreeValueByte = 0xAB;
    private const int PackedValueSize = 16;

    private static long EntryCountFor(IndexType indexType) =>
        indexType == IndexType.BTree ? BTreeEntryCount : PackedArrayEntryCount;

    // DenseByteIndex (1-byte keys): scale via value size.
    // 256 entries × 10 MiB ≈ 2.5 GiB per file — clears the ceiling without
    // multi-GiB scratch buffers (one ByteKeyValueSize buffer is reused).
    private static readonly int ByteKeyEntryCount = 256;
    private static readonly int ByteKeyValueSize = 10 * 1024 * 1024;

    // Generous, sparse-backed upper bound on an N-entry HSST's on-disk footprint. The
    // dedicated arena is SetLength-sized to this (sparse, so free) and Complete trims it to
    // the bytes written, so over-estimating is harmless; under-estimating would leave the
    // mmap shorter than the data and is unsafe, hence the wide margin.
    private static long EstimateBytes(long entryCount) => entryCount * 48L + (1L << 30);

    [TestCase(IndexType.BTree)]
    [TestCase(IndexType.PackedArray)]
    public void Hsst_BeyondTwoGiB_RoundTripAndMerge(IndexType indexType)
    {
        using TempDirArenaManager manager = new();
        manager.Initialize([]);

        long count = EntryCountFor(indexType);

        // -------- write --------
        ArenaReservation resA = WriteLargeHsst(manager, indexType, baseKey: 0L, count: count);
        ArenaReservation resB = WriteLargeHsst(manager, indexType, baseKey: count, count: count);
        ArenaReservation? resMerged = null;
        try
        {
            Assert.That(resA.Size, Is.GreaterThan((long)int.MaxValue),
                $"{indexType} HSST A is supposed to exceed the 2 GiB single-Span ceiling");
            Assert.That(resB.Size, Is.GreaterThan((long)int.MaxValue),
                $"{indexType} HSST B is supposed to exceed the 2 GiB single-Span ceiling");

            // -------- iterate each, verifying every key+value --------
            IterateAndVerify(indexType, resA, baseKey: 0L, expectedCount: count);
            IterateAndVerify(indexType, resB, baseKey: count, expectedCount: count);

            // -------- merge --------
            resMerged = MergeTwo(manager, indexType, resA, resB);

            Assert.That(resMerged.Size, Is.GreaterThan((long)int.MaxValue),
                $"merged {indexType} HSST is supposed to also exceed 2 GiB");

            IterateAndVerify(indexType, resMerged, baseKey: 0L, expectedCount: count * 2);
        }
        finally
        {
            resMerged?.Dispose();
            resB.Dispose();
            resA.Dispose();
        }
    }

    [TestCase(IndexType.DenseByteIndex)]
    public void Hsst_BeyondTwoGiB_LargeValues_RoundTrip(IndexType indexType)
    {
        using TempDirArenaManager manager = new();
        manager.Initialize([]);

        ArenaReservation res = WriteLargeValuesHsst(manager, indexType);
        try
        {
            if ((long)ByteKeyValueSize * ByteKeyEntryCount >= int.MaxValue)
                Assert.That(res.Size, Is.GreaterThan((long)int.MaxValue),
                    $"{indexType} HSST is supposed to exceed the 2 GiB single-Span ceiling");

            IterateAndVerifyLargeValues(indexType, res);
        }
        finally
        {
            res.Dispose();
        }
    }

    // ---------------- writers ----------------

    private static ArenaReservation WriteLargeHsst(IArenaManager manager, IndexType indexType, long baseKey, long count)
    {
        using ArenaWriter arenaWriter = manager.CreateWriter(EstimateBytes(count));
        ref ArenaBufferWriter writer = ref arenaWriter.GetWriter();
        switch (indexType)
        {
            case IndexType.BTree:
                {
                    using HsstBTreeBuilderBuffers.Container hsstBuffers = new(checked((int)count));
                    using HsstBTreeBuilder<ArenaBufferWriter> hsst = new(ref writer, ref hsstBuffers.Buffers, KeySize, expectedKeyCount: checked((int)count));
                    Span<byte> keyBuf = stackalloc byte[8];
                    Span<byte> valueBuf = stackalloc byte[1];
                    valueBuf[0] = BTreeValueByte;
                    for (long i = 0; i < count; i++)
                    {
                        BinaryPrimitives.WriteInt64BigEndian(keyBuf, baseKey + i);
                        hsst.Add(keyBuf[(8 - KeySize)..], valueBuf);
                    }
                    hsst.Build();
                    break;
                }
            case IndexType.PackedArray:
                {
                    using HsstPackedArrayBuilder<ArenaBufferWriter> hsst = new(
                        ref writer, keySize: KeySize, valueSize: PackedValueSize,
                        expectedKeyCount: checked((int)count));
                    Span<byte> keyBuf = stackalloc byte[8];
                    Span<byte> valueBuf = stackalloc byte[PackedValueSize];
                    for (long i = 0; i < count; i++)
                    {
                        BinaryPrimitives.WriteInt64BigEndian(keyBuf, baseKey + i);
                        FillPackedValuePattern(baseKey + i, valueBuf);
                        hsst.Add(keyBuf[(8 - KeySize)..], valueBuf);
                    }
                    hsst.Build();
                    break;
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(indexType));
        }
        return arenaWriter.Complete().Reservation;
    }

    private static ArenaReservation WriteLargeValuesHsst(IArenaManager manager, IndexType indexType)
    {
        long estimate = (long)ByteKeyValueSize * ByteKeyEntryCount + (1L << 30);
        using ArenaWriter arenaWriter = manager.CreateWriter(estimate);
        ref ArenaBufferWriter writer = ref arenaWriter.GetWriter();
        byte[] valueBuf = new byte[ByteKeyValueSize];
        switch (indexType)
        {
            case IndexType.DenseByteIndex:
                {
                    using HsstDenseByteIndexBuilder<ArenaBufferWriter> hsst = new(ref writer);
                    // Builder requires strictly descending insertion order.
                    for (int i = ByteKeyEntryCount - 1; i >= 0; i--)
                    {
                        FillLargeValuePattern((byte)i, valueBuf);
                        hsst.Add((byte)i, valueBuf);
                    }
                    hsst.Build();
                    break;
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(indexType));
        }
        return arenaWriter.Complete().Reservation;
    }

    // ---------------- iterators ----------------

    private static void IterateAndVerify(IndexType indexType, ArenaReservation reservation, long baseKey, long expectedCount)
    {
        ArenaByteReader reader = reservation.CreateReader();
        long size = reservation.Size;
        using HsstEnumerator<ArenaByteReader, NoOpPin> e = new(in reader, new Bound(0, size));
        Span<byte> expectedKey = stackalloc byte[8];
        Span<byte> expectedValue = stackalloc byte[PackedValueSize];
        Span<byte> keyBuf = stackalloc byte[KeySize];
        long i = 0;
        while (e.MoveNext(in reader))
        {
            ReadOnlySpan<byte> kSpan = e.CopyCurrentLogicalKey(in reader, keyBuf);
            Bound vb = e.CurrentValue;
            using NoOpPin vp = reader.PinBuffer(vb.Offset, vb.Length);

            BinaryPrimitives.WriteInt64BigEndian(expectedKey, baseKey + i);
            if (!kSpan.SequenceEqual(expectedKey[(8 - KeySize)..]))
                Assert.Fail($"key mismatch at entry {i} (baseKey {baseKey})");

            switch (indexType)
            {
                case IndexType.BTree:
                    if (vb.Length != 1 || vp.Buffer[0] != BTreeValueByte)
                        Assert.Fail($"value mismatch at entry {i}: len {vb.Length}, byte 0x{(vb.Length > 0 ? vp.Buffer[0] : 0):X2}");
                    break;
                case IndexType.PackedArray:
                    FillPackedValuePattern(baseKey + i, expectedValue);
                    if (!vp.Buffer.SequenceEqual(expectedValue))
                        Assert.Fail($"value mismatch at entry {i}");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(indexType));
            }
            i++;
        }
        Assert.That(i, Is.EqualTo(expectedCount));
    }

    private static void IterateAndVerifyLargeValues(IndexType indexType, ArenaReservation reservation)
    {
        ArenaByteReader reader = reservation.CreateReader();

        switch (indexType)
        {
            case IndexType.DenseByteIndex:
                {
                    // DenseByteIndex has no HsstEnumerator support — it's point-lookup only.
                    // Verify every tag 0..ByteKeyEntryCount-1 round-trips via HsstReader.TrySeek.
                    Span<byte> keyBuf = stackalloc byte[1];
                    for (int i = 0; i < ByteKeyEntryCount; i++)
                    {
                        // Match HsstDenseByteIndexTests' pattern: a fresh reader per lookup.
                        using HsstReader<ArenaByteReader, NoOpPin> r = new(in reader);
                        keyBuf[0] = (byte)i;
                        Assert.That(r.TrySeek(keyBuf, out _), Is.True, $"DenseByteIndex missing tag {i}");
                        Bound vb = r.GetBound();
                        using NoOpPin vp = reader.PinBuffer(vb.Offset, vb.Length);
                        Assert.That(vb.Length, Is.EqualTo(ByteKeyValueSize), $"DenseByteIndex value length at tag {i}");
                        if (!LargeValueMatches((byte)i, vp.Buffer))
                            Assert.Fail($"DenseByteIndex value byte mismatch at tag {i}");
                    }
                    break;
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(indexType));
        }
    }

    // ---------------- merge ----------------

    private static ArenaReservation MergeTwo(IArenaManager manager, IndexType indexType, ArenaReservation resA, ArenaReservation resB)
    {
        ArenaByteReader rA = resA.CreateReader();
        ArenaByteReader rB = resB.CreateReader();

        using HsstEnumerator<ArenaByteReader, NoOpPin> eA = new(in rA, new Bound(0, resA.Size));
        using HsstEnumerator<ArenaByteReader, NoOpPin> eB = new(in rB, new Bound(0, resB.Size));
        bool moreA = eA.MoveNext(in rA);
        bool moreB = eB.MoveNext(in rB);

        long merged = EntryCountFor(indexType) * 2;
        using ArenaWriter arenaWriter = manager.CreateWriter(EstimateBytes(merged));
        ref ArenaBufferWriter writer = ref arenaWriter.GetWriter();
        int mergedCount = checked((int)merged);
        switch (indexType)
        {
            case IndexType.BTree:
                {
                    using HsstBTreeBuilderBuffers.Container outHsstBuffers = new(mergedCount);
                    using HsstBTreeBuilder<ArenaBufferWriter> outHsst = new(ref writer, ref outHsstBuffers.Buffers, KeySize, expectedKeyCount: mergedCount);
                    Span<byte> keyBufA = stackalloc byte[KeySize];
                    Span<byte> keyBufB = stackalloc byte[KeySize];
                    while (moreA || moreB)
                    {
                        int cmp = ComparePins(in rA, in rB, in eA, in eB, moreA, moreB);
                        if (cmp <= 0)
                        {
                            ReadOnlySpan<byte> key = eA.CopyCurrentLogicalKey(in rA, keyBufA);
                            Bound vb = eA.CurrentValue;
                            using NoOpPin valPin = rA.PinBuffer(vb.Offset, vb.Length);
                            outHsst.Add(key, valPin.Buffer);
                            moreA = eA.MoveNext(in rA);
                            if (cmp == 0) moreB = eB.MoveNext(in rB);
                        }
                        else
                        {
                            ReadOnlySpan<byte> key = eB.CopyCurrentLogicalKey(in rB, keyBufB);
                            Bound vb = eB.CurrentValue;
                            using NoOpPin valPin = rB.PinBuffer(vb.Offset, vb.Length);
                            outHsst.Add(key, valPin.Buffer);
                            moreB = eB.MoveNext(in rB);
                        }
                    }
                    outHsst.Build();
                    break;
                }
            case IndexType.PackedArray:
                {
                    using HsstPackedArrayBuilder<ArenaBufferWriter> outHsst = new(
                        ref writer, keySize: KeySize, valueSize: PackedValueSize, expectedKeyCount: mergedCount);
                    Span<byte> keyBufA = stackalloc byte[KeySize];
                    Span<byte> keyBufB = stackalloc byte[KeySize];
                    while (moreA || moreB)
                    {
                        int cmp = ComparePins(in rA, in rB, in eA, in eB, moreA, moreB);
                        if (cmp <= 0)
                        {
                            ReadOnlySpan<byte> key = eA.CopyCurrentLogicalKey(in rA, keyBufA);
                            Bound vb = eA.CurrentValue;
                            using NoOpPin valPin = rA.PinBuffer(vb.Offset, vb.Length);
                            outHsst.Add(key, valPin.Buffer);
                            moreA = eA.MoveNext(in rA);
                            if (cmp == 0) moreB = eB.MoveNext(in rB);
                        }
                        else
                        {
                            ReadOnlySpan<byte> key = eB.CopyCurrentLogicalKey(in rB, keyBufB);
                            Bound vb = eB.CurrentValue;
                            using NoOpPin valPin = rB.PinBuffer(vb.Offset, vb.Length);
                            outHsst.Add(key, valPin.Buffer);
                            moreB = eB.MoveNext(in rB);
                        }
                    }
                    outHsst.Build();
                    break;
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(indexType));
        }
        return arenaWriter.Complete().Reservation;
    }

    private static int ComparePins(
        scoped in ArenaByteReader rA, scoped in ArenaByteReader rB,
        scoped in HsstEnumerator<ArenaByteReader, NoOpPin> eA,
        scoped in HsstEnumerator<ArenaByteReader, NoOpPin> eB,
        bool moreA, bool moreB)
    {
        if (!moreA) return 1;
        if (!moreB) return -1;
        Span<byte> bufA = stackalloc byte[KeySize];
        Span<byte> bufB = stackalloc byte[KeySize];
        ReadOnlySpan<byte> kA = eA.CopyCurrentLogicalKey(in rA, bufA);
        ReadOnlySpan<byte> kB = eB.CopyCurrentLogicalKey(in rB, bufB);
        return kA.SequenceCompareTo(kB);
    }

    // ---------------- value patterns ----------------

    /// <summary>
    /// Deterministic per-entry value for the PackedArray case. Byte j of the value
    /// for entry index <paramref name="entryIdx"/> is <c>(byte)((entryIdx + j * 31) ^ 0x5A)</c>;
    /// the verifier re-derives the same span and compares with SequenceEqual.
    /// </summary>
    private static void FillPackedValuePattern(long entryIdx, Span<byte> dest)
    {
        for (int j = 0; j < dest.Length; j++)
            dest[j] = (byte)((entryIdx + j * 31) ^ 0x5A);
    }

    private static void FillLargeValuePattern(byte tag, Span<byte> dest)
    {
        for (int j = 0; j < dest.Length; j++)
            dest[j] = (byte)((tag + j) & 0xFF);
    }

    private static bool LargeValueMatches(byte tag, ReadOnlySpan<byte> actual)
    {
        if (actual.Length != ByteKeyValueSize) return false;
        for (int j = 0; j < actual.Length; j++)
            if (actual[j] != (byte)((tag + j) & 0xFF)) return false;
        return true;
    }
}
