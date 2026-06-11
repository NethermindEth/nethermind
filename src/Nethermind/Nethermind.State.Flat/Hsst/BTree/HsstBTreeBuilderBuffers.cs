// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Runtime.CompilerServices;
using Nethermind.Core.Collections;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.Hsst.BTree;

/// <summary>
/// Reusable working buffers for <see cref="HsstBTreeBuilder{TWriter, TReader, TPin}"/> and
/// its inner index/leaf-boundary phases. Declare one in an outer scope and pass it by
/// <c>ref</c> to multiple builder constructions to skip the per-build rent/return of all
/// internal buffers.
///
/// List buffers retain their capacity across builds (cleared by
/// <see cref="ResetForBuild"/>). Array buffers stay rented from <see cref="ArrayPool{T}.Shared"/>
/// and only grow when a subsequent build needs more space than the previous one. Steady
/// state after a few uses is zero rent/return per build.
///
/// <see cref="Dispose"/> releases everything; in the auto-owned constructor path of
/// <see cref="HsstBTreeBuilder{TWriter, TReader, TPin}"/> the builder owns and disposes
/// an internal instance, so behavior is identical to the pre-refactor code at the cost
/// of one struct-sized field.
/// </summary>
public struct HsstBTreeBuilderBuffers(int expectedKeyCount = 16)
{
    // Current/next index-build level node lists. Populated during Add (one Entry-kind
    // descriptor pushed per entry; the trailing pending run is collapsed into a leaf
    // descriptor when a page-local leaf is emitted, or simply sealed in place when a
    // flush decides not to wrap them); then consumed by HsstBTreeBuilder.BuildIndex
    // as the bottom level and flipped between iterations as it walks up to the root.
    // Using NativeMemoryList<T> (class) rather than NativeMemoryListRef<T> (ref
    // struct) keeps the struct itself non-ref so it can live as a field of a class
    // (see HsstBTreeBuilderBuffersContainer) and so HsstBTreeBuilder's borrowed-
    // buffers ref field needs no Unsafe.AsPointer indirection.
    internal NativeMemoryList<HsstIndexNodeInfo> CurrentLevel = new(expectedKeyCount);
    internal NativeMemoryList<HsstIndexNodeInfo> NextLevel = new(64);

    // First-entry full key for every descriptor in <see cref="CurrentLevel"/> /
    // <see cref="NextLevel"/>, in matching order. Flat (descriptorCount * keyLength)
    // layout: the i-th descriptor's first-key occupies bytes
    // [i * keyLength, (i + 1) * keyLength). Populated whenever a descriptor is
    // pushed (per-entry Add, inline leaf, or freshly written intermediate) so that
    // HsstBTreeBuilder.BuildIndex can read every child's first-key directly without
    // reaching back into the already-written data region for a 20-byte address that
    // may straddle a 4 KiB page. Flipped together with the level lists at the end
    // of each Build iteration.
    internal NativeMemoryList<byte> CurrentLevelFirstKeys = new(64);
    internal NativeMemoryList<byte> NextLevelFirstKeys = new(64);

    // ArrayPool-backed scratch — null until first build that uses them.
    internal byte[]? CommonPrefixArr = null;
    internal byte[]? ValueScratch = null;

    // Per-Build scratch for HsstBTreeBuilder.ChooseIntermediateChildCount and
    // HsstBTreeBuilder.WriteIndexNode. Pooled fields (rather than stackalloc'd per call)
    // so a hot caller (e.g. PersistedSnapshotBuilder, which fires many small Builds
    // back-to-back) reuses the rented buffers across calls. Sized lazily by
    // HsstBTreeBuilder; null until the first build that needs them.
    internal byte[]? IndexFirstSepScratch = null;
    internal byte[]? IndexSepBufScratch = null;
    internal int[]? IndexSepLengthsScratch = null;

    // Root node's first-entry full key, populated by HsstBTreeBuilder.BuildIndex at
    // its final return so HsstBTreeBuilder.CopyRootPrefixBytes can supply the
    // trailer's RootPrefix bytes from memory rather than re-reading from the data
    // section.
    // ArrayPool-backed for cross-build reuse; null until the first non-empty build.
    internal byte[]? RootFirstKey = null;

    // Previous entry's full key, used by HsstBTreeBuilder.EmitEntryBookkeeping /
    // MaybeFlushBeforeEntry to compute online LCP across flushes (the pending-range
    // descriptor slice in <see cref="CurrentLevel"/> can shrink to zero on a flush,
    // but the LCP chain must stay intact). ArrayPool-backed and retained across
    // builds: cross-build contamination is impossible because the in-build invariant
    // is "PrevKeyBuf is meaningful only when entryIdx > 0 in the current build", and
    // entryIdx=0's EmitEntryBookkeeping unconditionally writes the entry-0 key before any
    // later add reads it.
    internal byte[]? PrevKeyBuf = null;

    // Running max separator length over the currently-pending entry range (the
    // trailing run of Entry-kind descriptors in <see cref="CurrentLevel"/>).
    // Maintained incrementally by HsstBTreeBuilder.EmitEntryBookkeeping so
    // MaybeFlushBeforeEntry's leaf-fit estimate can read it in O(1) instead of
    // rescanning the pending CommonPrefixArr slice on every Add. Reset to 0 on
    // every full pending flush (MaybeEmitInlineLeaf / FlushPendingAsEntries); recomputed
    // by a bounded rescan in FlushPendingNotOnCurrentPage's partial-trim path.
    internal byte PendingMaxSepLen = 0;

    /// <summary>
    /// Reset list counts to zero ahead of a new build. Capacity is retained, and
    /// rented arrays stay rented — the next build will reuse them if large enough.
    /// </summary>
    internal void ResetForBuild(int expectedKeyCount)
    {
        CurrentLevel.Clear();
        CurrentLevel.EnsureCapacity(expectedKeyCount);
        NextLevel.Clear();
        CurrentLevelFirstKeys.Clear();
        NextLevelFirstKeys.Clear();
        PendingMaxSepLen = 0;
    }

    /// <summary>Ensure <see cref="CommonPrefixArr"/> can hold the per-entry LCP for <paramref name="entryCount"/> entries.</summary>
    internal void EnsureCommonPrefixCapacity(int entryCount) => EnsureSize(ref CommonPrefixArr, entryCount);

    /// <summary>Ensure <see cref="PrevKeyBuf"/> can hold one <paramref name="keyLength"/>-byte key.</summary>
    internal void EnsurePrevKeyCapacity(int keyLength) => EnsureSize(ref PrevKeyBuf, keyLength);

    /// <summary>Ensure <see cref="ValueScratch"/> holds at least <paramref name="byteCount"/> bytes.</summary>
    internal void EnsureValueScratchCapacity(int byteCount) => EnsureSize(ref ValueScratch, byteCount);

    /// <summary>Ensure <see cref="RootFirstKey"/> holds the <paramref name="byteCount"/>-byte root first-key.</summary>
    internal void EnsureRootFirstKeyCapacity(int byteCount) => EnsureSize(ref RootFirstKey, byteCount);

    /// <summary>Ensure <see cref="IndexSepLengthsScratch"/> can hold <paramref name="count"/> separator lengths.</summary>
    internal void EnsureIndexSepLengthsCapacity(int count) => EnsureSize(ref IndexSepLengthsScratch, count);

    /// <summary>Ensure <see cref="IndexFirstSepScratch"/> holds the <paramref name="byteCount"/>-byte first separator.</summary>
    internal void EnsureIndexFirstSepCapacity(int byteCount) => EnsureSize(ref IndexFirstSepScratch, byteCount);

    /// <summary>Ensure <see cref="IndexSepBufScratch"/> holds a <paramref name="byteCount"/>-byte separator.</summary>
    internal void EnsureIndexSepBufCapacity(int byteCount) => EnsureSize(ref IndexSepBufScratch, byteCount);

    /// <summary>
    /// Ensure <paramref name="slot"/> holds an array of at least <paramref name="minSize"/>
    /// elements: keeps the existing array when already large enough, otherwise returns the
    /// old one to the pool (if any) and rents a fresh one.
    /// </summary>
    private static void EnsureSize<T>(ref T[]? slot, int minSize)
    {
        if (slot is null || slot.Length < minSize)
        {
            if (slot is not null) ArrayPool<T>.Shared.Return(slot);
            slot = ArrayPool<T>.Shared.Rent(minSize);
        }
    }

    /// <summary>
    /// Record entry <paramref name="entryIdx"/>'s common-prefix length in
    /// <see cref="CommonPrefixArr"/>. <see cref="CommonPrefixArr"/> is primed at build start and
    /// grows monotonically, so the hot path is a bounds check + direct write; the out-of-line
    /// grow rents a larger pool array, preserving the bytes already written for entries
    /// <c>0..entryIdx</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void AddCommonPrefix(int entryIdx, byte commonPrefixLength)
    {
        byte[] cpArr = CommonPrefixArr!;
        if ((uint)entryIdx >= (uint)cpArr.Length)
            cpArr = GrowCommonPrefixArr(entryIdx + 1);
        cpArr[entryIdx] = commonPrefixLength;
    }

    /// <summary>Cold-path rent-and-copy for <see cref="CommonPrefixArr"/>, kept out-of-line so <see cref="AddCommonPrefix"/> inlines.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private byte[] GrowCommonPrefixArr(int needed)
    {
        byte[]? oldArr = CommonPrefixArr;
        byte[] newArr = ArrayPool<byte>.Shared.Rent(needed);
        if (oldArr is not null)
        {
            Array.Copy(oldArr, newArr, oldArr.Length);
            ArrayPool<byte>.Shared.Return(oldArr);
        }
        CommonPrefixArr = newArr;
        return newArr;
    }

    public void Dispose()
    {
        CurrentLevel.Dispose();
        NextLevel.Dispose();
        CurrentLevelFirstKeys.Dispose();
        NextLevelFirstKeys.Dispose();
        if (CommonPrefixArr is not null) { ArrayPool<byte>.Shared.Return(CommonPrefixArr); CommonPrefixArr = null; }
        if (ValueScratch is not null) { ArrayPool<byte>.Shared.Return(ValueScratch); ValueScratch = null; }
        if (RootFirstKey is not null) { ArrayPool<byte>.Shared.Return(RootFirstKey); RootFirstKey = null; }
        if (PrevKeyBuf is not null) { ArrayPool<byte>.Shared.Return(PrevKeyBuf); PrevKeyBuf = null; }
        if (IndexFirstSepScratch is not null) { ArrayPool<byte>.Shared.Return(IndexFirstSepScratch); IndexFirstSepScratch = null; }
        if (IndexSepBufScratch is not null) { ArrayPool<byte>.Shared.Return(IndexSepBufScratch); IndexSepBufScratch = null; }
        if (IndexSepLengthsScratch is not null) { ArrayPool<int>.Shared.Return(IndexSepLengthsScratch); IndexSepLengthsScratch = null; }
    }
}

