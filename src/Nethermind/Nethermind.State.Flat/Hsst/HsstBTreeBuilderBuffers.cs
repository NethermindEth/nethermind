// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using Nethermind.Core.Collections;

namespace Nethermind.State.Flat.Hsst;

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
public ref struct HsstBTreeBuilderBuffers(int expectedKeyCount = 16)
{
    // Per-key metadata position list — owned by the outer HsstBTreeBuilder phase.
    internal NativeMemoryListRef<long> EntryPositions = new(expectedKeyCount);

    // Every entry's full key bytes, captured by HsstBTreeBuilder.Add /
    // FinishValueWrite. Flat (numEntries * keyLength) layout. Replaces the previous
    // re-read-from-data-section ReadKey path; the index builder indexes into this
    // buffer by the entry's global index. Page-local leaf emission and intermediate
    // construction both source separator/prefix bytes from here.
    internal NativeMemoryListRef<byte> AllKeys = new(64);

    // Current/next index-build level node lists. Populated during Add (entry
    // descriptors pushed for each Add; collapsed into a leaf descriptor when a
    // page-local leaf is emitted); then consumed by HsstIndexBuilder.Build as the
    // bottom level and flipped between iterations as it walks up to the root.
    internal NativeMemoryListRef<HsstIndexNodeInfo> CurrentLevel = new(64);
    internal NativeMemoryListRef<HsstIndexNodeInfo> NextLevel = new(64);

    // ArrayPool-backed scratch — null until first build that uses them.
    internal byte[]? CommonPrefixArr = null;
    internal byte[]? ValueScratch = null;

    /// <summary>
    /// Reset list counts to zero ahead of a new build. Capacity is retained, and
    /// rented arrays stay rented — the next build will reuse them if large enough.
    /// </summary>
    internal void ResetForBuild(int expectedKeyCount)
    {
        EntryPositions.Clear();
        EntryPositions.EnsureCapacity(expectedKeyCount);
        AllKeys.Clear();
        CurrentLevel.Clear();
        NextLevel.Clear();
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
        AllKeys.Dispose();
        CurrentLevel.Dispose();
        NextLevel.Dispose();
        if (CommonPrefixArr is not null) { ArrayPool<byte>.Shared.Return(CommonPrefixArr); CommonPrefixArr = null; }
        if (ValueScratch is not null) { ArrayPool<byte>.Shared.Return(ValueScratch); ValueScratch = null; }
    }
}

/// <summary>
/// Per-node record used by <see cref="HsstIndexBuilder{TWriter, TReader, TPin}"/> while
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
    /// <summary>Index (into <c>EntryPositions</c> / <c>AllKeys</c>) of the first leaf entry under this subtree.</summary>
    public readonly int FirstEntry = firstEntry;
    /// <summary>Index (into <c>EntryPositions</c> / <c>AllKeys</c>) of the last leaf entry under this subtree.</summary>
    public readonly int LastEntry = lastEntry;
    /// <summary>Common-key-prefix length the BSearchIndex planner picked for this node.
    /// Read at the level above when computing each separator length: the parent must extend
    /// its separator i to at least <c>PrefixLen</c> bytes so the child can recover its
    /// prefix bytes from the parent's separator at descent time. <c>0</c> for an entry
    /// descriptor — entries have no header, no <c>CommonKeyPrefix</c>.</summary>
    public readonly int PrefixLen = prefixLen;
}
