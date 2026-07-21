// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
}
