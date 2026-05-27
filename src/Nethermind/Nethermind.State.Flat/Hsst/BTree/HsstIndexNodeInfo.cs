// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Hsst.BTree;

/// <summary>
/// One node descriptor in the bottom-up B-tree build. Used uniformly for entries, leaves,
/// and intermediate nodes — the on-disk flag byte at <see cref="ChildOffset"/> tells the
/// reader which kind of thing it is sitting on.
/// </summary>
/// <remarks>
/// Lifted out of the generic <see cref="HsstBTreeBuilder{TWriter, TReader, TPin}"/> so that
/// <see cref="HsstBTreeBuilderBuffers"/> — which is not generic in <c>TWriter</c> — can hold
/// preallocated lists of these.
/// </remarks>
internal readonly struct HsstIndexNodeInfo(long childOffset, int firstEntry, int lastEntry, int prefixLen)
{
    /// <summary>Absolute first-byte position of this node (or entry) in the HSST (= the flag byte).</summary>
    public readonly long ChildOffset = childOffset;
    /// <summary>Index (into <c>EntryPositions</c> / <c>PendingKeys</c>) of the first leaf entry under this subtree.</summary>
    public readonly int FirstEntry = firstEntry;
    /// <summary>Index (into <c>EntryPositions</c> / <c>PendingKeys</c>) of the last leaf entry under this subtree.</summary>
    public readonly int LastEntry = lastEntry;
    /// <summary>Common-key-prefix length the BTreeNode planner picked for this node.
    /// Read at the level above when computing each separator length: the parent must extend
    /// its separator i to at least <c>PrefixLen</c> bytes so the child can recover its
    /// prefix bytes from the parent's separator at descent time. <c>0</c> for an entry
    /// descriptor — entries have no header, no <c>CommonKeyPrefix</c>.</summary>
    public readonly int PrefixLen = prefixLen;
}
