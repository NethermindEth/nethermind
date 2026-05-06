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
/// End-to-end smoke for the BTree-indexed HSST builder/reader/merge path
/// using the long-aware code paths (Bound.Length, HSST index offsets,
/// mmap-backed long-offset MmapByteReader).
///
/// The per-HSST builder cap on the on-disk format has been lifted, so this
/// test scales to a single HSST &gt;2 GiB by bumping
/// <see cref="EntryCountPerHsst"/> to ~300 million. The builder buffers
/// every entry's separator + metadata in native memory before writing the
/// index region (~16 B per HsstEntry × N), which makes the >2 GiB scale
/// take hours of CPU and ~5 GiB of native heap. Practical >2 GiB testing
/// requires a streaming builder that doesn't retain entry metadata across
/// the full input.
/// </summary>
[Explicit("Writes large HSSTs to /tmp; minutes to run at default scale.")]
public class HsstLargeBuildTests
{
    // 6 B key + 1 B value + 2 B LEB128 lengths ≈ 9 B/entry data, plus index.
    // 1M entries → ~10 MB per HSST: validates pipeline end to end. Bump to
    // ~300_000_000 to actually push a single HSST past 2 GiB (slow — see
    // class summary).
    // Cap is set so that the *merged* HSST's separator buffer (≈ 6 bytes per entry
    // for sequential 6-byte keys, summed across both sources) stays under
    // int.MaxValue — _separatorBuffer count is still int.
    private static readonly long EntryCountPerHsst = 150_000_000L;
    private const int KeySize = 6;
    private const byte ValueByte = 0xAB;

    [Test]
    public unsafe void BTree_Hsst_BeyondTwoGiB_RoundTripAndMerge()
    {
        string tmp = Path.GetTempPath();
        string pathA = Path.Combine(tmp, $"hsst-large-a-{Guid.NewGuid():N}.bin");
        string pathB = Path.Combine(tmp, $"hsst-large-b-{Guid.NewGuid():N}.bin");
        string pathMerged = Path.Combine(tmp, $"hsst-large-m-{Guid.NewGuid():N}.bin");

        try
        {
            // -------- write --------
            WriteLargeHsst(pathA, baseKey: 0L, count: EntryCountPerHsst);
            WriteLargeHsst(pathB, baseKey: EntryCountPerHsst, count: EntryCountPerHsst);

            long sizeA = new FileInfo(pathA).Length;
            long sizeB = new FileInfo(pathB).Length;
            // Skip the >2 GiB assertion when running with a smoke-sized entry count.
            if (EntryCountPerHsst >= 150_000_000L)
            {
                Assert.That(sizeA, Is.GreaterThan((long)int.MaxValue),
                    "HSST A is supposed to exceed the 2 GiB single-Span ceiling");
                Assert.That(sizeB, Is.GreaterThan((long)int.MaxValue),
                    "HSST B is supposed to exceed the 2 GiB single-Span ceiling");
            }

            // -------- iterate each --------
            Assert.That(IterateAndCount(pathA), Is.EqualTo(EntryCountPerHsst));
            Assert.That(IterateAndCount(pathB), Is.EqualTo(EntryCountPerHsst));

            // -------- merge --------
            MergeTwo(pathA, pathB, pathMerged);

            long sizeMerged = new FileInfo(pathMerged).Length;
            if (EntryCountPerHsst >= 150_000_000L)
                Assert.That(sizeMerged, Is.GreaterThan((long)int.MaxValue),
                    "merged HSST is supposed to also exceed 2 GiB");

            Assert.That(IterateAndCount(pathMerged), Is.EqualTo(EntryCountPerHsst * 2));
        }
        finally
        {
            TryDelete(pathA);
            TryDelete(pathB);
            TryDelete(pathMerged);
        }
    }

    private static void WriteLargeHsst(string path, long baseKey, long count)
    {
        using FileStream fs = new(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1);
        StreamBufferWriter writer = new(fs);
        try
        {
            using HsstBuilder<StreamBufferWriter> hsst = new(ref writer, expectedKeyCount: checked((int)count));
            Span<byte> keyBuf = stackalloc byte[8];
            Span<byte> valueBuf = stackalloc byte[1];
            valueBuf[0] = ValueByte;
            for (long i = 0; i < count; i++)
            {
                BinaryPrimitives.WriteInt64BigEndian(keyBuf, baseKey + i);
                hsst.Add(keyBuf[(8 - KeySize)..], valueBuf);
            }
            hsst.Build();
            writer.Flush();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static unsafe long IterateAndCount(string path)
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
            using HsstEnumerator<MmapByteReader, NoOpPin> e = new(in reader, new Bound(0, size));
            long count = 0;
            while (e.MoveNext()) count++;
            return count;
        }
        finally
        {
            accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    private static unsafe void MergeTwo(string pathA, string pathB, string pathOut)
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

            using HsstMergeEnumerator<MmapByteReader, NoOpPin> eA = new(in rA, new Bound(0, sizeA));
            using HsstMergeEnumerator<MmapByteReader, NoOpPin> eB = new(in rB, new Bound(0, sizeB));
            bool moreA = eA.MoveNext(in rA);
            bool moreB = eB.MoveNext(in rB);

            using FileStream outFs = new(pathOut, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1);
            StreamBufferWriter writer = new(outFs);
            try
            {
                using HsstBuilder<StreamBufferWriter> outHsst = new(ref writer, expectedKeyCount: checked((int)(EntryCountPerHsst * 2)));

                while (moreA || moreB)
                {
                    int cmp;
                    if (!moreA) cmp = 1;
                    else if (!moreB) cmp = -1;
                    else
                    {
                        Bound kA = eA.CurrentKey;
                        Bound kB = eB.CurrentKey;
                        using NoOpPin pA = rA.PinBuffer(kA.Offset, kA.Length);
                        using NoOpPin pB = rB.PinBuffer(kB.Offset, kB.Length);
                        cmp = pA.Buffer.SequenceCompareTo(pB.Buffer);
                    }

                    if (cmp <= 0)
                    {
                        Bound kb = eA.CurrentKey;
                        Bound vb = eA.CurrentValue;
                        using NoOpPin keyPin = rA.PinBuffer(kb.Offset, kb.Length);
                        using NoOpPin valPin = rA.PinBuffer(vb.Offset, vb.Length);
                        outHsst.Add(keyPin.Buffer, valPin.Buffer);
                        moreA = eA.MoveNext(in rA);
                        // Disjoint key spaces: cmp == 0 won't happen in this test, but guard anyway.
                        if (cmp == 0) moreB = eB.MoveNext(in rB);
                    }
                    else
                    {
                        Bound kb = eB.CurrentKey;
                        Bound vb = eB.CurrentValue;
                        using NoOpPin keyPin = rB.PinBuffer(kb.Offset, kb.Length);
                        using NoOpPin valPin = rB.PinBuffer(vb.Offset, vb.Length);
                        outHsst.Add(keyPin.Buffer, valPin.Buffer);
                        moreB = eB.MoveNext(in rB);
                    }
                }

                outHsst.Build();
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

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort cleanup */ }
    }
}
