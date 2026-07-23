// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;

namespace Nethermind.Pbt;

/// <summary>
/// The shape every trie node group tile shares whatever its width: where a node sits inside one and
/// which levels a format writes down, and the same for a <see cref="StemLeafBlob"/>. Everything here
/// is a function of a position, a slot or a width — never of any bytes — so a producer and a reader
/// can agree on the layout before either has an encoding to hand. What does depend on the tile's
/// width lives in <see cref="IPbtTileLayout"/>.
/// </summary>
/// <remarks>
/// The numbering counts up from slot 0, so a narrower tile's positions are a prefix of a wider one's
/// and the masks below serve both truncated — which is what lets the two tilings share this much.
/// </remarks>
public static class PbtLayout
{
    /// <summary>The widest tile's boundary slots, which the masks here are sized for.</summary>
    internal const int MaxBoundarySlots = 64;

    /// <summary>Bit set at <see cref="TrieNodeGroupBoundarySlotPosition"/>(i) for each boundary slot i.</summary>
    private static readonly UInt128 BoundaryPositionsBitmask = new(0x01b36366c366c6cdUL, 0x8366c6cd86cd8d9bUL);

    /// <summary>Every position of the widest tile, which is what a format that skips them all skips.</summary>
    private static readonly UInt128 AllPositionsBitmask = (UInt128.One << (2 * MaxBoundarySlots - 1)) - 1;

    /// <summary>
    /// Bit set at each position <see cref="PbtGroupFormat.Interleaved"/> stores no internal node at:
    /// the odd group-relative levels, counted up from the widest tile's root.
    /// </summary>
    /// <remarks>
    /// A position's level follows from the boundary slots its subtree covers — halving from the tile
    /// root down to the boundary — so the levels alternate with the width, and
    /// <see cref="TrieNodeGroupStoresInternalAtWidth"/> says the same thing by width where a walk has
    /// one to hand. A narrower tile's root sits at a width this one also holds, so the parities agree
    /// and the mask truncates like the boundary one. Disjoint from
    /// <see cref="BoundaryPositionsBitmask"/>: a boundary slot is a level of its own.
    /// </remarks>
    private static readonly UInt128 InterleavedSkippedPositions = new(0x2a44948914892912UL, 0x5489291229125224UL);

    /// <summary>The widths a tile's levels cover, from its root down to a boundary slot.</summary>
    private const int AllWidthsBitmask = 0b1111111;

    /// <summary>The widths a <see cref="StemLeafBlob"/>'s levels cover: 256 at the root down to 1 at a leaf.</summary>
    private const int StemLeafAllWidthsBitmask = 0b1_1111_1111;

    /// <summary>The widths whose level <paramref name="format"/> stores an internal node at.</summary>
    /// <remarks>
    /// <see cref="PbtGroupFormat.Interleaved"/> keeps every other one and
    /// <see cref="PbtGroupFormat.BoundaryOnly"/> the boundary alone — a boundary entry being the link
    /// to what hangs below it, which nothing recomputes without a lookup of its own. Both masks are
    /// anchored at width 1, so they say the same of a narrower tiling's widths as of a wider one's.
    /// </remarks>
    private static int TrieNodeGroupKeptWidths(PbtGroupFormat format) => format switch
    {
        PbtGroupFormat.Interleaved => 0b1010101,
        PbtGroupFormat.BoundaryOnly or PbtGroupFormat.Every4Depth => 0b0000001,
        _ => AllWidthsBitmask,
    };

    /// <summary>Bit set at each position <paramref name="format"/> stores no internal node at.</summary>
    private static UInt128 TrieNodeGroupSkippedPositions(PbtGroupFormat format) => format switch
    {
        PbtGroupFormat.Interleaved => InterleavedSkippedPositions,
        PbtGroupFormat.BoundaryOnly or PbtGroupFormat.Every4Depth => AllPositionsBitmask & ~BoundaryPositionsBitmask,
        _ => 0,
    };

    /// <summary>The widths whose level <paramref name="format"/> stores an internal node of a <see cref="StemLeafBlob"/> at.</summary>
    /// <remarks>
    /// <see cref="PbtLeafFormat.Interleaved"/> keeps 64, 16 and 4, anchored at the leaves as the
    /// group's is at its boundary, so that the level just above a stored node is always a skipped one;
    /// <see cref="PbtLeafFormat.LeavesOnly"/> keeps none of them, and
    /// <see cref="PbtLeafFormat.Every4Depth"/> the 16-wide level alone. Width 1 is a leaf, which holds a
    /// value rather than a hash and is stored whatever the format, and the 256-wide root is left out
    /// wherever a level is for the same reason a group's is: the stem node holding the blob caches
    /// that hash already. <see cref="StemLeafBlob"/> counts an interleaved or every-4-depth blob's
    /// entries by a fold that unrolls its kept levels, so a change to them belongs there too.
    /// </remarks>
    private static int StemLeafKeptWidths(PbtLeafFormat format) => format switch
    {
        PbtLeafFormat.Interleaved => 0b1010100,
        PbtLeafFormat.LeavesOnly => 0b0000001,
        PbtLeafFormat.Every4Depth => 0b0010000,
        _ => StemLeafAllWidthsBitmask,
    };

    /// <summary>Whether <paramref name="format"/> stores no internal node at <paramref name="position"/>.</summary>
    /// <remarks>A stem node is stored at every position; only an internal node's hash is recomputable.</remarks>
    public static bool TrieNodeGroupIsSkippedPosition(PbtGroupFormat format, int position) =>
        (TrieNodeGroupSkippedPositions(format) & (UInt128.One << position)) != 0;

    /// <summary>
    /// Whether <paramref name="presenceBitmask"/> holds an internal node at a level <paramref name="format"/>
    /// skips, which is what a group in that format must never encode.
    /// </summary>
    internal static bool TrieNodeGroupHoldsSkippedInternal(PbtGroupFormat format, UInt128 presenceBitmask, UInt128 stemsBitmask) =>
        (presenceBitmask & TrieNodeGroupSkippedPositions(format) & ~stemsBitmask) != 0;

    /// <summary>
    /// Whether <paramref name="format"/> stores an internal node at the position whose subtree covers
    /// <paramref name="width"/> boundary slots.
    /// </summary>
    public static bool TrieNodeGroupStoresInternalAtWidth(PbtGroupFormat format, int width) =>
        (width & TrieNodeGroupKeptWidths(format)) != 0;

    /// <summary>
    /// Whether <paramref name="format"/> stores an internal node at the level of a
    /// <see cref="StemLeafBlob"/> covering <paramref name="width"/> leaves.
    /// </summary>
    /// <remarks>Says nothing about whether that node branches, which is the other half of what a blob stores.</remarks>
    public static bool StemLeafStoresInternalAtWidth(PbtLeafFormat format, int width) =>
        (width & StemLeafKeptWidths(format)) != 0;

    /// <summary>The bits of the boundary slots <c>[firstSlot, firstSlot + width)</c>.</summary>
    /// <remarks>
    /// Built by shifting the all-ones word down rather than up: a tile as wide as the word would
    /// shift its one bit clean out, which C# would instead wrap back to bit zero.
    /// </remarks>
    internal static ulong SlotRange(int firstSlot, int width) => (ulong.MaxValue >> (MaxBoundarySlots - width)) << firstSlot;

    /// <summary>The bits of the boundary slots below <paramref name="slot"/>, which may be every one of them.</summary>
    /// <remarks>
    /// <see cref="TrieNodeGroupBoundarySlot"/> counts as high as the tile is wide — every slot sits
    /// below its root position — and a shift by the whole word wraps back to no shift at all rather
    /// than to zero.
    /// </remarks>
    internal static ulong SlotsBelow(int slot) => slot == MaxBoundarySlots ? ulong.MaxValue : (1UL << slot) - 1;

    /// <summary>The post-order position of boundary slot <paramref name="slot"/>.</summary>
    public static int TrieNodeGroupBoundarySlotPosition(int slot) => 2 * slot - BitOperations.PopCount((uint)slot);

    public static bool TrieNodeGroupIsBoundaryPosition(int position) => (BoundaryPositionsBitmask & (UInt128.One << position)) != 0;

    public static int TrieNodeGroupBoundarySlot(int position) => PopCount(BoundaryPositionsBitmask & ((UInt128.One << position) - 1));

    /// <summary>The lowest position of the subtree rooted at <paramref name="position"/> covering <paramref name="width"/> boundary slots.</summary>
    internal static int TrieNodeGroupFirstSubtreePosition(int position, int width) => position - 2 * width + 2;

    /// <summary>The positions of the subtree rooted at <paramref name="position"/> covering <paramref name="width"/> boundary slots.</summary>
    internal static UInt128 TrieNodeGroupSubtreeBitmask(int position, int width) =>
        ((UInt128.One << (position + 1)) - 1) & ~((UInt128.One << TrieNodeGroupFirstSubtreePosition(position, width)) - 1);

    public static int PopCount(UInt128 bitmask) =>
        BitOperations.PopCount((ulong)bitmask) + BitOperations.PopCount((ulong)(bitmask >> 64));

    internal static int TrailingZeroCount(UInt128 bitmask) =>
        (ulong)bitmask != 0 ? BitOperations.TrailingZeroCount((ulong)bitmask) : 64 + BitOperations.TrailingZeroCount((ulong)(bitmask >> 64));

    /// <summary>The highest set position of <paramref name="bitmask"/>, which must not be zero.</summary>
    internal static int Log2(UInt128 bitmask) =>
        (ulong)(bitmask >> 64) != 0 ? 64 + BitOperations.Log2((ulong)(bitmask >> 64)) : BitOperations.Log2((ulong)bitmask);

    /// <summary>
    /// Gathers the <see cref="BoundaryPositionsBitmask"/> bits of <paramref name="positionsBitmask"/>
    /// down into slot order, over the <paramref name="slotGroups"/> groups of four slots a tile holds.
    /// </summary>
    /// <remarks>
    /// A software PEXT of the constant mask, whose bits fall in groups of four — positions
    /// <c>o + {0, 1, 3, 4}</c> for <c>o = 8g - popcount(g)</c> — so each group compacts with one shift
    /// and they pack together. Branch-free and ISA-independent, unlike <c>Bmi2.ParallelBitExtract</c>,
    /// and with <paramref name="slotGroups"/> a constant of the caller's tiling the loop costs only the
    /// groups that tiling has.
    /// </remarks>
    internal static ulong GatherBoundary(UInt128 positionsBitmask, int slotGroups)
    {
        ulong gathered = 0;
        for (int group = 0; group < slotGroups; group++)
        {
            int offset = 8 * group - BitOperations.PopCount((uint)group);
            gathered |= (ulong)CompactGroup((uint)(positionsBitmask >> offset)) << (4 * group);
        }

        return gathered;
    }

    /// <summary>Compacts one group's bits — at 0, 1, 3 and 4 — down into the low four.</summary>
    private static uint CompactGroup(uint groupBitmask) => (groupBitmask & 0b0011u) | ((groupBitmask >> 1) & 0b1100u);
}
