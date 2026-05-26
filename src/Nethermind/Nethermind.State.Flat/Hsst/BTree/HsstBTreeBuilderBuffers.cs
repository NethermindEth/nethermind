// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
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
    // Per-key metadata position list — owned by the outer HsstBTreeBuilder phase.
    // Using NativeMemoryList<T> (class) rather than NativeMemoryListRef<T> (ref
    // struct) keeps the struct itself non-ref so it can live as a field of a class
    // (see HsstBTreeBuilderBuffersContainer) and so HsstBTreeBuilder's borrowed-
    // buffers ref field needs no Unsafe.AsPointer indirection.
    internal NativeMemoryList<long> EntryPositions = new(expectedKeyCount);

    // Full keys for the entries that are still pending — i.e. not yet folded into
    // an inline page-local leaf. Flat (pendingCount * keyLength) layout. Cleared
    // on every <see cref="HsstBTreeBuilder{TWriter,TReader,TPin}"/>.EmitInlineLeaf
    // (after the leaf has been written). Peak size is bounded by one 4 KiB page-
    // worth of entries (a few hundred entries × keyLength, low KB) — once flushed,
    // the leftmost-entry key the index builder still needs for intermediate
    // construction is preserved in <see cref="CurrentLevelFirstKeys"/>.
    internal NativeMemoryList<byte> PendingKeys = new(64);

    // Current/next index-build level node lists. Populated during Add (entry
    // descriptors pushed for each Add; collapsed into a leaf descriptor when a
    // page-local leaf is emitted); then consumed by HsstBTreeBuilder.BuildIndex as
    // the bottom level and flipped between iterations as it walks up to the root.
    internal NativeMemoryList<HsstIndexNodeInfo> CurrentLevel = new(64);
    internal NativeMemoryList<HsstIndexNodeInfo> NextLevel = new(64);

    // First-entry full key for every descriptor in <see cref="CurrentLevel"/> /
    // <see cref="NextLevel"/>, in matching order. Flat (descriptorCount * keyLength)
    // layout: the i-th descriptor's first-key occupies bytes
    // [i * keyLength, (i + 1) * keyLength). Populated whenever a descriptor is
    // pushed (inline leaf, direct-flush entry, or freshly written intermediate)
    // so that HsstBTreeBuilder.BuildIndex can read every child's first-key directly
    // without reaching back into the already-written data region for a 20-byte
    // address that may straddle a 4 KiB page. Flipped together with the level
    // lists at the end of each Build iteration.
    internal NativeMemoryList<byte> CurrentLevelFirstKeys = new(64);
    internal NativeMemoryList<byte> NextLevelFirstKeys = new(64);

    // ArrayPool-backed scratch — null until first build that uses them.
    internal byte[]? CommonPrefixArr = null;
    internal byte[]? ValueScratch = null;

    // Per-Build scratch for HsstBTreeBuilder.ChooseIntermediateChildCount and
    // HsstBTreeBuilder.WriteIndexNode. Previously stackalloc'd per call (255 bytes
    // each for firstSep / sepBuf, plus variable-sized int[] / byte[] for sepLengths
    // / keyBuf). Promoted to pooled fields so a hot caller (e.g.
    // PersistedSnapshotBuilder, which fires many small Builds back-to-back) reuses
    // the rented buffers across calls. Sized lazily by HsstBTreeBuilder; null until
    // the first build that needs them.
    internal byte[]? IndexFirstSepScratch = null;
    internal byte[]? IndexSepBufScratch = null;
    internal byte[]? IndexKeyBufScratch = null;
    internal int[]? IndexSepLengthsScratch = null;

    // Root node's first-entry full key, populated by HsstBTreeBuilder.BuildIndex at
    // its final return so HsstBTreeBuilder.CopyRootPrefixBytes can supply the
    // trailer's RootPrefix bytes from memory rather than re-reading from the data
    // section.
    // ArrayPool-backed for cross-build reuse; null until the first non-empty build.
    internal byte[]? RootFirstKey = null;

    // Previous entry's full key, used by HsstBTreeBuilder.OnEntryAdded /
    // MaybeFlushBeforeEntry to compute online LCP. Independent of
    // <see cref="PendingKeys"/> (which only holds keys for the in-flight pending
    // set and is cleared on each leaf emission), so the LCP chain stays intact
    // across flushes. ArrayPool-backed and retained across builds: cross-build
    // contamination is impossible because the in-build invariant is "PrevKeyBuf
    // is meaningful only when entryIdx > 0 in the current build", and entryIdx=0's
    // OnEntryAdded unconditionally writes the entry-0 key before any later add
    // reads it.
    internal byte[]? PrevKeyBuf = null;

    // Running max separator length over the currently-pending entry range
    // [_pendingFirstEntryIdx, EntryPositions.Count). Maintained incrementally by
    // HsstBTreeBuilder.OnEntryAdded so MaybeFlushBeforeEntry's leaf-fit estimate
    // can read it in O(1) instead of rescanning the pending CommonPrefixArr slice
    // on every Add. Reset to 0 on every full pending flush
    // (EmitInlineLeaf / FlushPendingAsEntries); recomputed by a bounded rescan in
    // FlushPendingNotOnCurrentPage's partial-flush path.
    internal byte PendingMaxSepLen = 0;

    /// <summary>
    /// Reset list counts to zero ahead of a new build. Capacity is retained, and
    /// rented arrays stay rented — the next build will reuse them if large enough.
    /// </summary>
    internal void ResetForBuild(int expectedKeyCount)
    {
        EntryPositions.Clear();
        EntryPositions.EnsureCapacity(expectedKeyCount);
        PendingKeys.Clear();
        CurrentLevel.Clear();
        NextLevel.Clear();
        CurrentLevelFirstKeys.Clear();
        NextLevelFirstKeys.Clear();
        PendingMaxSepLen = 0;
    }

    /// <summary>
    /// Ensure <paramref name="slot"/> holds an array of at least <paramref name="minSize"/>
    /// elements. Returns the existing array when already large enough; otherwise returns
    /// the old one to the pool (if any) and rents a fresh one.
    /// </summary>
    internal static void EnsureSize<T>(ref T[]? slot, int minSize)
    {
        if (slot is null || slot.Length < minSize)
        {
            if (slot is not null) ArrayPool<T>.Shared.Return(slot);
            slot = ArrayPool<T>.Shared.Rent(minSize);
        }
    }

    public void Dispose()
    {
        EntryPositions.Dispose();
        PendingKeys.Dispose();
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
        if (IndexKeyBufScratch is not null) { ArrayPool<byte>.Shared.Return(IndexKeyBufScratch); IndexKeyBufScratch = null; }
        if (IndexSepLengthsScratch is not null) { ArrayPool<int>.Shared.Return(IndexSepLengthsScratch); IndexSepLengthsScratch = null; }
    }
}

/// <summary>
/// Per-node record used by <see cref="HsstBTreeBuilder{TWriter, TReader, TPin}"/> while
/// it walks the index region bottom-up. Lifted out of the generic builder so that
/// <see cref="HsstBTreeBuilderBuffers"/> — which is not generic in <c>TWriter</c> — can
/// hold preallocated lists of these.
/// </summary>
/// <summary>
/// One node descriptor in the bottom-up B-tree build. Used uniformly for entries, leaves,
/// and intermediate nodes — the on-disk flag byte at <see cref="ChildOffset"/> tells the
/// reader which kind of thing it is sitting on.
/// </summary>
internal readonly struct HsstIndexNodeInfo(long childOffset, int firstEntry, int lastEntry, int prefixLen)
{
    /// <summary>Absolute first-byte position of this node (or entry) in the HSST (= the flag byte).</summary>
    public readonly long ChildOffset = childOffset;
    /// <summary>Index (into <c>EntryPositions</c> / <c>PendingKeys</c>) of the first leaf entry under this subtree.</summary>
    public readonly int FirstEntry = firstEntry;
    /// <summary>Index (into <c>EntryPositions</c> / <c>PendingKeys</c>) of the last leaf entry under this subtree.</summary>
    public readonly int LastEntry = lastEntry;
    /// <summary>Common-key-prefix length the BSearchIndex planner picked for this node.
    /// Read at the level above when computing each separator length: the parent must extend
    /// its separator i to at least <c>PrefixLen</c> bytes so the child can recover its
    /// prefix bytes from the parent's separator at descent time. <c>0</c> for an entry
    /// descriptor — entries have no header, no <c>CommonKeyPrefix</c>.</summary>
    public readonly int PrefixLen = prefixLen;
}
