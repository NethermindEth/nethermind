// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using NodeKind = Nethermind.Pbt.PbtTrieNodeGroup.NodeKind;

namespace Nethermind.Pbt;

/// <summary>
/// What a trie node group holds by position, as the two bitmaps that say it: where a node sits, and
/// which of those nodes are stems.
/// </summary>
/// <remarks>
/// The encoding's own arithmetic — <c>EncodedLength</c>, <c>Decode</c>, <c>SubtreeBitmaps</c> —
/// indexes the tile's post-order positions, which is what pins every entry's offset and the whole
/// blob's length. The descent works at the coarser <see cref="BoundarySlotMasks"/> granularity, which
/// is what it resolves and the fold is driven by. <see cref="Chains"/> is always the latter: a run
/// hangs from a boundary slot and nowhere else.
/// <para>
/// This scalar value remains the representation for four- and six-level groups. Wider groups use
/// caller-owned word spans through <see cref="PbtTrieNodeGroup{TLayout}.CopyBoundaryShape"/> and
/// <see cref="GroupRebuild{TLayout}"/>.
/// </para>
/// </remarks>
public readonly record struct NodeGroupBitmasks(UInt128 Presence, UInt128 Stems, ulong Chains);

/// <summary>
/// What a trie node group holds at its boundary: bit <c>i</c> is set where boundary slot <c>i</c>
/// holds a node, where that node is a stem, and where it is a run.
/// </summary>
public readonly record struct BoundarySlotMasks(ulong Presence, ulong Stems, ulong Chains)
{
    /// <summary>
    /// The slots rooting a child group's blob — occupied, and holding neither a stem, whose subtree is
    /// its leaf blob, nor a run, whose bytes the group holds itself.
    /// </summary>
    public ulong ChildSlots => Presence & ~Stems & ~Chains;

    /// <summary>
    /// The kind of the node the slots <paramref name="rangeBitmask"/> covers fold to — an unoccupied
    /// range is absent, a lone stem stays a stem, hoisting to its shortest unique prefix higher up,
    /// and anything else roots an internal node.
    /// </summary>
    /// <remarks>
    /// The fold's whole kind algebra, and it needs only the boundary: a node's shape follows from its
    /// own range without walking below it, which is what lets a rebuild emit nodes in encoding order.
    /// </remarks>
    public NodeKind KindOf(ulong rangeBitmask)
    {
        ulong occupied = Presence & rangeBitmask;
        return occupied == 0 ? NodeKind.Absent
            : BitOperations.PopCount(occupied) == 1 && (Stems & occupied) != 0 ? NodeKind.Stem
            : NodeKind.Internal;
    }

    /// <summary>What the whole of the shape folds to, <see cref="KindOf"/> over every slot.</summary>
    public NodeKind RootKind => KindOf(ulong.MaxValue);
}
