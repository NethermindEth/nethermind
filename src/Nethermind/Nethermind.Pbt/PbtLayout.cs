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
    /// <summary>The widest tile's boundary slots.</summary>
    internal const int MaxBoundarySlots = 256;

    /// <summary>The widths a tile's levels cover, from its root down to a boundary slot.</summary>
    private const int AllWidthsBitmask = 0b1_1111_1111;

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
        PbtGroupFormat.Interleaved => 0b1_0101_0101,
        PbtGroupFormat.BoundaryOnly or PbtGroupFormat.Every4Depth => 0b0000001,
        _ => AllWidthsBitmask,
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
        !TrieNodeGroupIsBoundaryPosition(position)
        && !TrieNodeGroupStoresInternalAtWidth(format, TrieNodeGroupPositionWidth(position));

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

    /// <summary>The post-order position of boundary slot <paramref name="slot"/>.</summary>
    public static int TrieNodeGroupBoundarySlotPosition(int slot) => 2 * slot - BitOperations.PopCount((uint)slot);

    public static bool TrieNodeGroupIsBoundaryPosition(int position)
    {
        int slot = TrieNodeGroupBoundarySlot(position);
        return slot < MaxBoundarySlots && TrieNodeGroupBoundarySlotPosition(slot) == position;
    }

    /// <summary>The number of boundary positions strictly below <paramref name="position"/>.</summary>
    public static int TrieNodeGroupBoundarySlot(int position)
    {
        int low = 0;
        int high = MaxBoundarySlots;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (TrieNodeGroupBoundarySlotPosition(middle) < position) low = middle + 1;
            else high = middle;
        }

        return low;
    }

    /// <summary>The lowest position of the subtree rooted at <paramref name="position"/> covering <paramref name="width"/> boundary slots.</summary>
    internal static int TrieNodeGroupFirstSubtreePosition(int position, int width) => position - 2 * width + 2;

    private static int TrieNodeGroupPositionWidth(int position)
    {
        for (int width = 2; width <= MaxBoundarySlots; width *= 2)
        {
            int firstPosition = TrieNodeGroupFirstSubtreePosition(position, width);
            int firstSlot = TrieNodeGroupBoundarySlot(firstPosition);
            if (firstSlot % width == 0 && TrieNodeGroupBoundarySlotPosition(firstSlot) == firstPosition) return width;
        }

        return 1;
    }

    internal static ulong GatherBoundary(UInt128 positions, int boundarySlots)
    {
        ulong boundary = 0;
        for (int slot = 0; slot < boundarySlots; slot++)
            if ((positions & (UInt128.One << TrieNodeGroupBoundarySlotPosition(slot))) != 0) boundary |= 1UL << slot;
        return boundary;
    }

    public static int PopCount(UInt128 bitmask) =>
        BitOperations.PopCount((ulong)bitmask) + BitOperations.PopCount((ulong)(bitmask >> 64));

    internal static int TrailingZeroCount(UInt128 bitmask) =>
        (ulong)bitmask != 0 ? BitOperations.TrailingZeroCount((ulong)bitmask) : 64 + BitOperations.TrailingZeroCount((ulong)(bitmask >> 64));

    /// <summary>The highest set position of <paramref name="bitmask"/>, which must not be zero.</summary>
    internal static int Log2(UInt128 bitmask) =>
        (ulong)(bitmask >> 64) != 0 ? 64 + BitOperations.Log2((ulong)(bitmask >> 64)) : BitOperations.Log2((ulong)bitmask);
}
