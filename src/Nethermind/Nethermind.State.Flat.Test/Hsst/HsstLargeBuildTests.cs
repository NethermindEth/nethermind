// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.MemoryMappedFiles;
using NUnit.Framework;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.Storage;

namespace Nethermind.State.Flat.Test.Hsst;

/// <summary>
/// End-to-end smoke for the HSST builder/reader/merge path at single-HSST sizes
/// above the 2 GiB single-Span ceiling. Exercises the long-aware code paths
/// (Bound.Length, HSST index offsets, mmap-backed long-offset MmapByteReader)
/// and verifies — on every yielded entry — that the bytes round-trip exactly,
/// not just that the entry count matches.
///
/// Two scaling strategies are used, picked by the index type's structural cap:
/// - Multi-byte-keyed indexes (BTree, PackedArray) hit &gt;2 GiB through entry
///   volume — see <see cref="EntryCountPerHsst"/> (~150M).
/// - Single-byte-keyed indexes (ByteTagMap, DenseByteIndex) are hard-capped at
///   256 entries by the format, so they hit &gt;2 GiB through value size:
///   <see cref="ByteKeyEntryCount"/> × <see cref="ByteKeyValueSize"/>.
///
/// The BTree builder buffers every entry's separator + metadata in native
/// memory before writing the index region (~16 B per HsstEntry × N), which
/// makes the &gt;2 GiB scale take hours of CPU and several GiB of native heap.
/// PackedArray's per-entry buffer footprint is tiny (sparse checkpoint keys
/// only), so its run time is dominated by I/O. ByteTagMap / DenseByteIndex
/// each allocate one ~10 MiB scratch buffer that is reused across entries.
/// </summary>
[Explicit("Writes large HSSTs to /tmp; minutes to hours to run at default scale.")]
public class HsstLargeBuildTests
{
    // BTree / PackedArray (multi-byte keys): scale via entry count.
    // 6 B key + value bytes ≈ entry size; chosen so the *merged* HSST stays
    // under int.MaxValue separator-buffer count for BTree.
    private static readonly long EntryCountPerHsst = 150_000_000L;
    private const int KeySize = 6;
    private const byte BTreeValueByte = 0xAB;
    // PackedArray uses a fixed-size value; 16 B × 150M ≈ 2.4 GiB so a single
    // HSST clears the ceiling even with the leaner index footprint.
    private const int PackedValueSize = 16;

    // ByteTagMap / DenseByteIndex (1-byte keys): scale via value size.
    // 256 entries × 10 MiB ≈ 2.5 GiB per file — clears the ceiling without
    // multi-GiB scratch buffers (one ByteKeyValueSize buffer is reused).
    private static readonly int ByteKeyEntryCount = 256;
    private static readonly int ByteKeyValueSize = 10 * 1024 * 1024;

    [TestCase(IndexType.BTree)]
    [TestCase(IndexType.PackedArray)]
    public unsafe void Hsst_BeyondTwoGiB_RoundTripAndMerge(IndexType indexType)
    {
        string tmp = Path.GetTempPath();
        string pathA = Path.Combine(tmp, $"hsst-large-a-{Guid.NewGuid():N}.bin");
        string pathB = Path.Combine(tmp, $"hsst-large-b-{Guid.NewGuid():N}.bin");
        string pathMerged = Path.Combine(tmp, $"hsst-large-m-{Guid.NewGuid():N}.bin");

        try
        {
            // -------- write --------
            WriteLargeHsst(indexType, pathA, baseKey: 0L, count: EntryCountPerHsst);
            WriteLargeHsst(indexType, pathB, baseKey: EntryCountPerHsst, count: EntryCountPerHsst);

            long sizeA = new FileInfo(pathA).Length;
            long sizeB = new FileInfo(pathB).Length;
            // Skip the >2 GiB assertion when running with a smoke-sized entry count.
            if (EntryCountPerHsst >= 150_000_000L)
            {
                Assert.That(sizeA, Is.GreaterThan((long)int.MaxValue),
                    $"{indexType} HSST A is supposed to exceed the 2 GiB single-Span ceiling");
                Assert.That(sizeB, Is.GreaterThan((long)int.MaxValue),
                    $"{indexType} HSST B is supposed to exceed the 2 GiB single-Span ceiling");
            }

            // -------- iterate each, verifying every key+value --------
            IterateAndVerify(indexType, pathA, baseKey: 0L, expectedCount: EntryCountPerHsst);
            IterateAndVerify(indexType, pathB, baseKey: EntryCountPerHsst, expectedCount: EntryCountPerHsst);

            // -------- merge --------
            MergeTwo(indexType, pathA, pathB, pathMerged);

            long sizeMerged = new FileInfo(pathMerged).Length;
            if (EntryCountPerHsst >= 150_000_000L)
                Assert.That(sizeMerged, Is.GreaterThan((long)int.MaxValue),
                    $"merged {indexType} HSST is supposed to also exceed 2 GiB");

            IterateAndVerify(indexType, pathMerged, baseKey: 0L, expectedCount: EntryCountPerHsst * 2);
        }
        finally
        {
            TryDelete(pathA);
            TryDelete(pathB);
            TryDelete(pathMerged);
        }
    }

    [TestCase(IndexType.ByteTagMap)]
    [TestCase(IndexType.DenseByteIndex)]
    public unsafe void Hsst_BeyondTwoGiB_LargeValues_RoundTrip(IndexType indexType)
    {
        string tmp = Path.GetTempPath();
        string path = Path.Combine(tmp, $"hsst-large-v-{Guid.NewGuid():N}.bin");

        try
        {
            WriteLargeValuesHsst(indexType, path);

            long size = new FileInfo(path).Length;
            if ((long)ByteKeyValueSize * ByteKeyEntryCount >= int.MaxValue)
                Assert.That(size, Is.GreaterThan((long)int.MaxValue),
                    $"{indexType} HSST is supposed to exceed the 2 GiB single-Span ceiling");

            IterateAndVerifyLargeValues(indexType, path);
        }
        finally
        {
            TryDelete(path);
        }
    }

    // ---------------- writers ----------------

    private static void WriteLargeHsst(IndexType indexType, string path, long baseKey, long count)
    {
        // Open a separate read-side mmap so the index builder can read back the
        // freshly-flushed data section through the writer's OpenReader.
        using FileStream fs = new(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite, bufferSize: 1);
        ArenaBufferWriter writer = new(fs, firstOffset: 0, (relOffset, size) => OpenFileView(fs, relOffset, size));
        try
        {
            switch (indexType)
            {
                case IndexType.BTree:
                    {
                        using HsstBTreeBuilder<ArenaBufferWriter, ArenaBufferReader, NoOpPin> hsst = new(ref writer, KeySize, expectedKeyCount: checked((int)count));
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
            writer.Flush();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static void WriteLargeValuesHsst(IndexType indexType, string path)
    {
        using FileStream fs = new(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite, bufferSize: 1);
        ArenaBufferWriter writer = new(fs, firstOffset: 0, (relOffset, size) => OpenFileView(fs, relOffset, size));
        byte[] valueBuf = new byte[ByteKeyValueSize];
        try
        {
            switch (indexType)
            {
                case IndexType.ByteTagMap:
                    {
                        using HsstByteTagMapBuilder<ArenaBufferWriter> hsst = new(ref writer);
                        for (int i = 0; i < ByteKeyEntryCount; i++)
                        {
                            FillLargeValuePattern((byte)i, valueBuf);
                            hsst.Add((byte)i, valueBuf);
                        }
                        hsst.Build();
                        break;
                    }
                case IndexType.DenseByteIndex:
                    {
                        using HsstDenseByteIndexBuilder<ArenaBufferWriter> hsst = new(ref writer);
                        for (int i = 0; i < ByteKeyEntryCount; i++)
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
            writer.Flush();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>
    /// Per-test view source for <see cref="ArenaBufferWriter.OpenReader"/>. Mmaps
    /// the same file the writer is appending to and returns a fresh accessor over
    /// the requested range. Mirrors <see cref="ArenaFile.OpenWholeView"/>'s
    /// disposal behaviour (release pointer + dispose accessor).
    /// </summary>
    private static unsafe IArenaWholeView OpenFileView(FileStream fs, long offset, long size)
    {
        MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(
            fs, mapName: null, capacity: 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
        MemoryMappedViewAccessor accessor = mmf.CreateViewAccessor(offset, size, MemoryMappedFileAccess.Read);
        byte* ptr = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        return new TestFileView(mmf, accessor, ptr + accessor.PointerOffset, size);
    }

    private sealed unsafe class TestFileView(MemoryMappedFile mmf, MemoryMappedViewAccessor accessor, byte* dataPtr, long size) : IArenaWholeView
    {
        public byte* DataPtr => dataPtr;
        public long Size => size;
        public ReadOnlySpan<byte> GetSpan() => new(dataPtr, checked((int)size));
        public void Dispose()
        {
            accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            accessor.Dispose();
            mmf.Dispose();
        }
    }

    // ---------------- iterators ----------------

    private static unsafe void IterateAndVerify(IndexType indexType, string path, long baseKey, long expectedCount)
    {
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long size = fs.Length;
        using MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(
            fs, mapName: null, capacity: 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
        using MemoryMappedViewAccessor accessor = mmf.CreateViewAccessor(0, size, MemoryMappedFileAccess.Read);
        byte* ptr = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        try
        {
            byte* dataPtr = ptr + accessor.PointerOffset;
            MmapByteReader reader = new(dataPtr, size);
            using HsstRefEnumerator<MmapByteReader, NoOpPin> e = new(in reader, new Bound(0, size));
            Span<byte> expectedKey = stackalloc byte[8];
            Span<byte> expectedValue = stackalloc byte[PackedValueSize];
            Span<byte> keyBuf = stackalloc byte[KeySize];
            long i = 0;
            while (e.MoveNext())
            {
                ReadOnlySpan<byte> kSpan = e.CopyCurrentLogicalKey(keyBuf);
                Bound vb = e.Current.ValueBound;
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
        finally
        {
            accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    private static unsafe void IterateAndVerifyLargeValues(IndexType indexType, string path)
    {
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long size = fs.Length;
        using MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(
            fs, mapName: null, capacity: 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
        using MemoryMappedViewAccessor accessor = mmf.CreateViewAccessor(0, size, MemoryMappedFileAccess.Read);
        byte* ptr = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        try
        {
            byte* dataPtr = ptr + accessor.PointerOffset;
            MmapByteReader reader = new(dataPtr, size);

            switch (indexType)
            {
                case IndexType.ByteTagMap:
                    {
                        using HsstRefEnumerator<MmapByteReader, NoOpPin> e = new(in reader, new Bound(0, size));
                        Span<byte> tagBuf = stackalloc byte[1];
                        int i = 0;
                        while (e.MoveNext())
                        {
                            ReadOnlySpan<byte> kSpan = e.CopyCurrentLogicalKey(tagBuf);
                            Bound vb = e.Current.ValueBound;
                            using NoOpPin vp = reader.PinBuffer(vb.Offset, vb.Length);

                            Assert.That(kSpan.Length, Is.EqualTo(1), $"{indexType} key length at entry {i}");
                            Assert.That(kSpan[0], Is.EqualTo((byte)i), $"{indexType} tag at entry {i}");
                            Assert.That(vb.Length, Is.EqualTo(ByteKeyValueSize), $"{indexType} value length at entry {i}");
                            if (!LargeValueMatches((byte)i, vp.Buffer))
                                Assert.Fail($"{indexType} value byte mismatch at entry {i}");
                            i++;
                        }
                        Assert.That(i, Is.EqualTo(ByteKeyEntryCount));
                        break;
                    }
                case IndexType.DenseByteIndex:
                    {
                        // DenseByteIndex has no HsstRefEnumerator support — it's point-lookup only.
                        // Verify every tag 0..ByteKeyEntryCount-1 round-trips via HsstReader.TrySeek.
                        Span<byte> keyBuf = stackalloc byte[1];
                        for (int i = 0; i < ByteKeyEntryCount; i++)
                        {
                            // Match HsstDenseByteIndexTests' pattern: a fresh reader per lookup.
                            using HsstReader<MmapByteReader, NoOpPin> r = new(in reader);
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
        finally
        {
            accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    // ---------------- merge ----------------

    private static unsafe void MergeTwo(IndexType indexType, string pathA, string pathB, string pathOut)
    {
        using FileStream fsA = new(pathA, FileMode.Open, FileAccess.Read, FileShare.Read);
        using FileStream fsB = new(pathB, FileMode.Open, FileAccess.Read, FileShare.Read);
        long sizeA = fsA.Length;
        long sizeB = fsB.Length;

        using MemoryMappedFile mmfA = MemoryMappedFile.CreateFromFile(
            fsA, mapName: null, capacity: 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
        using MemoryMappedFile mmfB = MemoryMappedFile.CreateFromFile(
            fsB, mapName: null, capacity: 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
        using MemoryMappedViewAccessor accA = mmfA.CreateViewAccessor(0, sizeA, MemoryMappedFileAccess.Read);
        using MemoryMappedViewAccessor accB = mmfB.CreateViewAccessor(0, sizeB, MemoryMappedFileAccess.Read);
        byte* ptrA = null, ptrB = null;
        accA.SafeMemoryMappedViewHandle.AcquirePointer(ref ptrA);
        accB.SafeMemoryMappedViewHandle.AcquirePointer(ref ptrB);
        try
        {
            byte* dataA = ptrA + accA.PointerOffset;
            byte* dataB = ptrB + accB.PointerOffset;
            MmapByteReader rA = new(dataA, sizeA);
            MmapByteReader rB = new(dataB, sizeB);

            using HsstEnumerator<MmapByteReader, NoOpPin> eA = new(in rA, new Bound(0, sizeA));
            using HsstEnumerator<MmapByteReader, NoOpPin> eB = new(in rB, new Bound(0, sizeB));
            bool moreA = eA.MoveNext(in rA);
            bool moreB = eB.MoveNext(in rB);

            using FileStream outFs = new(pathOut, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite, bufferSize: 1);
            ArenaBufferWriter writer = new(outFs, firstOffset: 0, (relOffset, size) => OpenFileView(outFs, relOffset, size));
            try
            {
                int merged = checked((int)(EntryCountPerHsst * 2));
                switch (indexType)
                {
                    case IndexType.BTree:
                        {
                            using HsstBTreeBuilder<ArenaBufferWriter, ArenaBufferReader, NoOpPin> outHsst = new(ref writer, KeySize, expectedKeyCount: merged);
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
                                ref writer, keySize: KeySize, valueSize: PackedValueSize, expectedKeyCount: merged);
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
                writer.Flush();
            }
            finally
            {
                writer.Dispose();
            }
        }
        finally
        {
            accA.SafeMemoryMappedViewHandle.ReleasePointer();
            accB.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    private static int ComparePins(
        scoped in MmapByteReader rA, scoped in MmapByteReader rB,
        scoped in HsstEnumerator<MmapByteReader, NoOpPin> eA,
        scoped in HsstEnumerator<MmapByteReader, NoOpPin> eB,
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

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort cleanup */ }
    }
}
