// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.Hsst.BTree;

/// <summary>
/// Reusable working buffers for <see cref="HsstBTreeBuilder{TWriter, TReader, TPin}"/> and
/// its inner index/leaf-boundary phases. Declare one in an outer scope and pass it by
/// <c>ref</c> to multiple builder constructions to skip the per-build rent/return of all
/// internal buffers.
///
/// Every buffer is a <see cref="NativeMemoryList{T}"/> that grows and retains its capacity
/// across builds (cleared/refilled per build); steady state after a few uses is zero allocation
/// per build. In the auto-owned constructor path of
/// <see cref="HsstBTreeBuilder{TWriter, TReader, TPin}"/> the builder owns and disposes an
/// internal instance.
/// </summary>
public struct HsstBTreeBuilderBuffers(int expectedKeyCount = 16)
{
    // Current/next index-build level node lists. Populated during Add (one Entry-kind
    // descriptor per entry; the trailing pending run becomes a leaf descriptor on inline-leaf
    // emission, or is sealed in place when a flush declines to wrap it), then consumed by
    // BuildIndex as the bottom level and flipped each iteration as it walks up to the root.
    // NativeMemoryList<T> (class) rather than NativeMemoryListRef<T> (ref struct) keeps this
    // struct non-ref so it can be a field of a class (see Container) and the builder's borrowed
    // ref field needs no Unsafe.AsPointer indirection.
    internal NativeMemoryList<HsstIndexNodeInfo> CurrentLevel = new(expectedKeyCount);
    internal NativeMemoryList<HsstIndexNodeInfo> NextLevel = new(64);

    // First-entry full key for every descriptor in CurrentLevel / NextLevel, in matching
    // order. Flat (descriptorCount * keyLength) layout: descriptor i's first-key occupies
    // [i * keyLength, (i + 1) * keyLength). Populated on every descriptor push so BuildIndex
    // can read each child's first-key without reaching back into the data region for an
    // address that may straddle a 4 KiB page. Flipped with the level lists each iteration.
    internal NativeMemoryList<byte> CurrentLevelFirstKeys = new(64);
    internal NativeMemoryList<byte> NextLevelFirstKeys = new(64);

    // Per-entry common-prefix length against the prior entry's key. Appended once per entry
    // by HsstBTreeBuilder.EmitEntryBookkeeping (Count == entry count) and read back by the
    // index-build phase at child.FirstEntry. Cleared at build start by ResetForBuild.
    internal NativeMemoryList<byte> CommonPrefixArr = new(expectedKeyCount);

    // Per-node scratch for child-offset value bytes, written by HsstBTreeBuilder.WriteIndexNode.
    internal NativeMemoryList<byte> ValueScratch = new(64);

    // Per-Build scratch for HsstBTreeBuilder.WriteIndexNode's per-child separator lengths.
    // Refilled (Clear + Add) per call so a hot caller (e.g. PersistedSnapshotBuilder, firing many
    // small Builds back-to-back) reuses the buffer across calls.
    internal NativeMemoryList<int> IndexSepLengthsScratch = new(64);

    // Root node's first-entry full key, populated by HsstBTreeBuilder.BuildIndex at its final
    // return so HsstBTreeBuilder.CopyRootPrefixBytes can supply the trailer's RootPrefix bytes
    // from memory rather than re-reading from the data section.
    internal NativeMemoryList<byte> RootFirstKey = new(64);

    // Previous entry's full key, used by HsstBTreeBuilder.EmitEntryBookkeeping /
    // MaybeFlushBeforeEntry to compute online LCP across flushes (the pending-range
    // descriptor slice in <see cref="CurrentLevel"/> can shrink to zero on a flush, but the
    // LCP chain must stay intact). Refilled (Clear + AddRange) at the end of each entry's
    // bookkeeping; meaningful only when entryIdx > 0, and entry 0 writes it before any read.
    internal NativeMemoryList<byte> PrevKeyBuf = new(64);

    // Running max separator length over the currently-pending entry range (the
    // trailing run of Entry-kind descriptors in <see cref="CurrentLevel"/>).
    // Maintained incrementally by HsstBTreeBuilder.EmitEntryBookkeeping so
    // MaybeFlushBeforeEntry's leaf-fit estimate can read it in O(1) instead of
    // rescanning the pending CommonPrefixArr slice on every Add. Reset to 0 on
    // every full pending flush (MaybeEmitInlineLeaf / FlushPendingAsEntries); recomputed
    // by a bounded rescan in FinalizePendingNotOnCurrentPage's partial-trim path.
    internal byte PendingMaxSepLen = 0;

    /// <summary>
    /// Reset list counts to zero ahead of a new build. Capacity is retained for reuse.
    /// </summary>
    internal void ResetForBuild(int expectedKeyCount)
    {
        CurrentLevel.Clear();
        CurrentLevel.EnsureCapacity(expectedKeyCount);
        NextLevel.Clear();
        CurrentLevelFirstKeys.Clear();
        NextLevelFirstKeys.Clear();
        CommonPrefixArr.Clear();
        PrevKeyBuf.Clear();
        PendingMaxSepLen = 0;
    }

    public void Dispose()
    {
        CurrentLevel.Dispose();
        NextLevel.Dispose();
        CurrentLevelFirstKeys.Dispose();
        NextLevelFirstKeys.Dispose();
        CommonPrefixArr.Dispose();
        ValueScratch.Dispose();
        IndexSepLengthsScratch.Dispose();
        RootFirstKey.Dispose();
        PrevKeyBuf.Dispose();
    }

    /// <summary>
    /// Reference-type (heap) container for an <see cref="HsstBTreeBuilderBuffers"/>, letting it be
    /// held in a non-ref field and reused across many builds. Used by the persisted-snapshot
    /// builder/merger and <see cref="HsstBTreeMerger"/> to amortise per-build buffer rentals.
    /// </summary>
    internal sealed class Container(int expectedKeyCount = 16) : IDisposable
    {
        private HsstBTreeBuilderBuffers _buffers = new(expectedKeyCount);

        /// <summary>The contained buffers, returned by <c>ref</c> into the field.</summary>
        public ref HsstBTreeBuilderBuffers Buffers => ref _buffers;

        public void Dispose() => _buffers.Dispose();
    }
}

/// <summary>
/// One node descriptor in the bottom-up B-tree build. Used uniformly for entries, leaves,
/// and intermediate nodes — the on-disk flag byte at <see cref="ChildOffset"/> tells the
/// reader which kind of thing it is sitting on.
/// </summary>
/// <remarks>
/// Lives here (rather than inside the generic <see cref="HsstBTreeBuilder{TWriter, TReader, TPin}"/>)
/// so the non-generic <see cref="HsstBTreeBuilderBuffers"/> can hold preallocated lists of these.
/// </remarks>
internal readonly struct HsstIndexNodeInfo(long childOffset, int firstEntry, int lastEntry, int prefixLen)
{
    /// <summary>Absolute first-byte position of this node (or entry) in the HSST (= the flag byte).</summary>
    public readonly long ChildOffset = childOffset;
    /// <summary>Global, build-wide entry index of the first leaf entry under this subtree.
    /// Used by the index-build phase to look up per-entry common-prefix length in
    /// <see cref="HsstBTreeBuilderBuffers.CommonPrefixArr"/>.</summary>
    public readonly int FirstEntry = firstEntry;
    /// <summary>Global, build-wide entry index of the last leaf entry under this subtree.
    /// Used by the index-build phase to look up per-entry common-prefix length in
    /// <see cref="HsstBTreeBuilderBuffers.CommonPrefixArr"/>.</summary>
    public readonly int LastEntry = lastEntry;
    /// <summary>Common-key-prefix length the BTreeNode planner picked for this node.
    /// Read at the level above when computing each separator length: the parent must extend
    /// its separator i to at least <c>PrefixLen</c> bytes so the child can recover its
    /// prefix bytes from the parent's separator at descent time. <c>0</c> for an entry
    /// descriptor — entries have no header, no <c>CommonKeyPrefix</c>.</summary>
    public readonly int PrefixLen = prefixLen;
}
