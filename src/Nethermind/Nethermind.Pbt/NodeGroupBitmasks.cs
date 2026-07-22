// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using NodeKind = Nethermind.Pbt.PbtTrieNodeGroup.NodeKind;

namespace Nethermind.Pbt;

/// <summary>
/// What a <see cref="PbtTrieNodeGroup"/> holds, as the three bitmaps that say it: where a node sits,
/// which of those nodes are stems, and which boundary slots hold a run.
/// </summary>
/// <remarks>
/// Read at either of the two granularities a group is described by, whichever the producer means.
/// The encoding's own arithmetic — <see cref="PbtTrieNodeGroup.EncodedLength"/>,
/// <see cref="PbtTrieNodeGroup.Decode"/>, <see cref="PbtTrieNodeGroup.SubtreeBitmaps"/> — indexes the
/// 31 post-order positions, which is what pins every entry's offset and the whole blob's length;
/// <see cref="PbtTrieNodeGroup.BoundaryShape"/> gathers those same bitmaps down to the 16 boundary
/// slots, which is what the descent resolves and the fold is driven by. <see cref="Chains"/> is
/// always the latter: a run hangs from a boundary slot and nowhere else.
/// </remarks>
public readonly record struct NodeGroupBitmasks(uint Presence, uint Stems, uint Chains)
{
    /// <summary>
    /// Of a boundary shape: the slots rooting a child group's blob — occupied, and holding neither a
    /// stem, whose subtree is its leaf blob, nor a run, whose bytes the group holds itself.
    /// </summary>
    public uint ChildSlots => Presence & ~Stems & ~Chains;

    /// <summary>
    /// Of a boundary shape: the kind of the node the slots <paramref name="rangeBitmask"/> covers fold
    /// to — an unoccupied range is absent, a lone stem stays a stem, hoisting to its shortest unique
    /// prefix higher up, and anything else roots an internal node.
    /// </summary>
    /// <remarks>
    /// The fold's whole kind algebra, and it needs only the boundary: a node's shape follows from its
    /// own range without walking below it, which is what lets a rebuild emit nodes in encoding order.
    /// </remarks>
    public NodeKind KindOf(uint rangeBitmask)
    {
        uint occupied = Presence & rangeBitmask;
        return occupied == 0 ? NodeKind.Absent
            : BitOperations.PopCount(occupied) == 1 && (Stems & occupied) != 0 ? NodeKind.Stem
            : NodeKind.Internal;
    }

    /// <summary>Of a boundary shape: what the whole of it folds to, <see cref="KindOf"/> over every slot.</summary>
    public NodeKind RootKind => KindOf(uint.MaxValue);
}
