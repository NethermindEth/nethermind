// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;

namespace Nethermind.Pbt;

/// <summary>
/// The shape of the PBT's storage: how many trie levels a group covers, where a node sits inside one,
/// which depths hold their children's blobs and which levels a format writes down. Everything here is
/// a function of a depth, a position, a slot or a width — never of any bytes — so a producer and a
/// reader can agree on the layout before either has an encoding to hand.
/// </summary>
public static class PbtLayout
{
    /// <summary>Trie levels covered by one group: a group rooted at depth d has its boundary slots at depth d + 4.</summary>
    public const int TrieNodeGroupLevelsPerGroup = 4;

    public const int TrieNodeGroupBoundarySlots = 1 << TrieNodeGroupLevelsPerGroup;
    public const int TrieNodeGroupPositionCount = 2 * TrieNodeGroupBoundarySlots - 1;
    public const int TrieNodeGroupRootPosition = 2 * TrieNodeGroupBoundarySlots - 2;

    /// <summary>The deepest group root depth; that group's boundary is the 248-bit stem level, where every node is a stem.</summary>
    public const int TrieNodeGroupMaxGroupDepth = Stem.LengthInBits - TrieNodeGroupLevelsPerGroup;

    /// <summary>Bit set at <see cref="TrieNodeGroupBoundarySlotPosition"/>(i) for each boundary slot i.</summary>
    private const uint BoundaryPositionsBitmask = 0x06CD8D9Bu;

    /// <summary>
    /// Bit set at each position <see cref="PbtGroupFormat.Interleaved"/> stores no internal node at: the odd
    /// group-relative levels, 14 and 29 (level 1) and 2, 5, 9, 12, 17, 20, 24 and 27 (level 3).
    /// </summary>
    /// <remarks>
    /// A position's level follows from the boundary slots its subtree covers — 16 at the root, then 8,
    /// 4, 2 and 1 at the boundary — so the levels alternate with the width halving, and
    /// <see cref="TrieNodeGroupStoresInternalAtWidth"/> says the same thing by width where a walk has one to hand.
    /// Disjoint from <see cref="BoundaryPositionsBitmask"/>: a boundary slot is a level of its own.
    /// </remarks>
    private const uint SkippedPositionsBitmask = 0x29125224u;

    /// <summary>The widths whose level <see cref="PbtGroupFormat.Interleaved"/> stores an internal node at: 16, 4 and 1.</summary>
    private const int KeptWidthsBitmask = 0b10101;

    /// <summary>The widths whose level <see cref="PbtLeafFormat.Interleaved"/> stores an internal node at: 64, 16 and 4.</summary>
    /// <remarks>
    /// Anchored at the leaves, as the group's is at its boundary, so that the level just above a stored
    /// node is always a skipped one. Width 1 is a leaf, which holds a value rather than a hash and is
    /// stored whatever the format, and the 256-wide root is left out for the same reason a group's is:
    /// the stem node holding the blob caches that hash already. <see cref="StemLeafBlob"/> counts a
    /// blob's entries by a fold that unrolls these three levels, so a change here belongs there too.
    /// </remarks>
    private const int StemLeafKeptWidthsBitmask = 0b1010100;

    /// <summary>
    /// Whether the group at <paramref name="depth"/> holds its children's blobs inside its own, which
    /// alternates by group so that every other level is reached without a lookup of its own.
    /// </summary>
    /// <remarks>
    /// Absolute rather than relative to the root or to the nearest run, which is what keeps a blob's
    /// placement a function of its depth alone: a run splitting at an intermediate depth would
    /// otherwise flip the parity of everything below it, re-keying a whole subtree. Depth 0 does not
    /// cluster, so the zone roots at depth 4 keep the keys their columns are routed by.
    /// </remarks>
    public static bool IsClusteringDepth(int depth) => (depth & TrieNodeGroupLevelsPerGroup) != 0;

    /// <summary>Whether <paramref name="format"/> stores no internal node at <paramref name="position"/>.</summary>
    /// <remarks>A stem node is stored at every position; only an internal node's hash is recomputable.</remarks>
    public static bool TrieNodeGroupIsSkippedPosition(PbtGroupFormat format, int position) =>
        format == PbtGroupFormat.Interleaved && (SkippedPositionsBitmask & (1u << position)) != 0;

    /// <summary>
    /// Whether <paramref name="presenceBitmask"/> holds an internal node at a level <paramref name="format"/>
    /// skips, which is what a group in that format must never encode.
    /// </summary>
    internal static bool TrieNodeGroupHoldsSkippedInternal(PbtGroupFormat format, uint presenceBitmask, uint stemsBitmask) =>
        format == PbtGroupFormat.Interleaved && (presenceBitmask & SkippedPositionsBitmask & ~stemsBitmask) != 0;

    /// <summary>
    /// Whether <paramref name="format"/> stores an internal node at the position whose subtree covers
    /// <paramref name="width"/> boundary slots.
    /// </summary>
    public static bool TrieNodeGroupStoresInternalAtWidth(PbtGroupFormat format, int width) =>
        format == PbtGroupFormat.EveryLevel || (width & KeptWidthsBitmask) != 0;

    /// <summary>
    /// Whether <paramref name="format"/> stores an internal node at the level of a
    /// <see cref="StemLeafBlob"/> covering <paramref name="width"/> leaves.
    /// </summary>
    /// <remarks>Says nothing about whether that node branches, which is the other half of what a blob stores.</remarks>
    public static bool StemLeafStoresInternalAtWidth(PbtLeafFormat format, int width) =>
        format != PbtLeafFormat.Interleaved || (width & StemLeafKeptWidthsBitmask) != 0;

    /// <summary>The post-order position of boundary slot <paramref name="slot"/>.</summary>
    public static int TrieNodeGroupBoundarySlotPosition(int slot) => 2 * slot - BitOperations.PopCount((uint)slot);

    public static bool TrieNodeGroupIsBoundaryPosition(int position) => (BoundaryPositionsBitmask & (1u << position)) != 0;

    public static int TrieNodeGroupBoundarySlot(int position) => BitOperations.PopCount(BoundaryPositionsBitmask & ((1u << position) - 1));

    /// <summary>The lowest position of the subtree rooted at <paramref name="position"/> covering <paramref name="width"/> boundary slots.</summary>
    internal static int TrieNodeGroupFirstSubtreePosition(int position, int width) => position - 2 * width + 2;

    /// <summary>The positions of the subtree rooted at <paramref name="position"/> covering <paramref name="width"/> boundary slots.</summary>
    internal static uint TrieNodeGroupSubtreeBitmask(int position, int width) =>
        ((1u << (position + 1)) - 1) & ~((1u << TrieNodeGroupFirstSubtreePosition(position, width)) - 1);

    /// <summary>Gathers the sixteen <see cref="BoundaryPositionsBitmask"/> bits of <paramref name="positionsBitmask"/> down into slot order.</summary>
    /// <remarks>
    /// A software PEXT of the constant mask, whose bits fall in four groups of four — positions
    /// <c>o + {0, 1, 3, 4}</c> for <c>o</c> in 0, 7, 15 and 22 — so each group compacts with one shift
    /// and the four pack together. Branch-free and ISA-independent, unlike <c>Bmi2.ParallelBitExtract</c>.
    /// </remarks>
    internal static uint TrieNodeGroupBoundaryBitmask(uint positionsBitmask) =>
        CompactGroup(positionsBitmask)
        | (CompactGroup(positionsBitmask >> 7) << 4)
        | (CompactGroup(positionsBitmask >> 15) << 8)
        | (CompactGroup(positionsBitmask >> 22) << 12);

    /// <summary>Compacts one group's bits — at 0, 1, 3 and 4 — down into the low four.</summary>
    private static uint CompactGroup(uint groupBitmask) => (groupBitmask & 0b0011u) | ((groupBitmask >> 1) & 0b1100u);
}
