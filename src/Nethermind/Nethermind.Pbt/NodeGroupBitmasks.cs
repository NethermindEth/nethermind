// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NodeKind = Nethermind.Pbt.PbtTrieNodeGroup.NodeKind;
using Nethermind.Pbt.Tiles;

namespace Nethermind.Pbt;

/// <summary>Position masks describe encoded nodes and stems; boundary masks drive descent, and chains exist only at boundaries.</summary>
public readonly record struct NodeGroupBitmasks(UInt128 Presence, UInt128 Stems, ulong Chains);

/// <summary>
/// What a trie node group holds at its boundary: the slots holding a node, those whose node is a
/// stem, and those whose node is a run.
/// </summary>
internal readonly record struct BoundarySlotMasks<TLayout>(
    SlotBitmask<TLayout> Presence, SlotBitmask<TLayout> Stems, SlotBitmask<TLayout> Chains)
    where TLayout : IPbtTileLayout
{
    /// <summary>
    /// The slots rooting a child group's blob — occupied, and holding neither a stem, whose subtree is
    /// its leaf blob, nor a run, whose bytes the group holds itself.
    /// </summary>
    public SlotBitmask<TLayout> ChildSlots => Presence.Except(Stems).Except(Chains);

    /// <summary>
    /// The kind of the node the slots <c>[firstSlot, firstSlot + width)</c> fold to — an unoccupied
    /// range is absent, a lone stem stays a stem, hoisting to its shortest unique prefix higher up,
    /// and anything else roots an internal node.
    /// </summary>
    /// <remarks>
    /// The fold's whole kind algebra, and it needs only the boundary: a node's shape follows from its
    /// own range without walking below it, which is what lets a rebuild emit nodes in encoding order.
    /// </remarks>
    public NodeKind KindOf(int firstSlot, int width)
    {
        int occupied = Presence.CountInRange(firstSlot, width);
        return occupied == 0 ? NodeKind.Absent
            : occupied == 1 && Stems[Presence.FirstInRange(firstSlot, width)] ? NodeKind.Stem
            : NodeKind.Internal;
    }

    /// <summary>What the whole of the shape folds to, <see cref="KindOf"/> over every slot.</summary>
    public NodeKind RootKind => KindOf(0, TLayout.BoundarySlots);
}
