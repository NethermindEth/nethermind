// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Pbt;

namespace Nethermind.Pbt.Tiles;

/// <summary>
/// The shape of one tiling of the stem trie: how many levels a tile covers, how a stem picks the slot
/// it descends into, and how wide the bitmaps pinning its entries are.
/// </summary>
/// <remarks>Empty generic layout structs provide compile-time constants; width-independent logic is in <see cref="PbtLayout"/>.</remarks>
public interface IPbtTileLayout
{
    static abstract PbtTiling Tiling { get; }

    /// <summary>The depth at which this layout's tile grid starts.</summary>
    static abstract int RootDepth { get; }

    /// <summary>Trie levels covered by one tile: a tile rooted at depth d has its boundary slots at depth d + this.</summary>
    static abstract int LevelsPerGroup { get; }

    static abstract int BoundarySlots { get; }

    static abstract int PositionCount { get; }

    static abstract int RootPosition { get; }

    /// <summary>The deepest tile root depth, which is the deepest key this tiling ever writes.</summary>
    static abstract int MaxGroupDepth { get; }

    /// <summary>Whether <paramref name="depth"/> is a tile root depth in this layout's grid.</summary>
    static abstract bool IsGroupDepth(int depth);

    /// <summary>The depth of the tile holding trie level <paramref name="bit"/>: <paramref name="bit"/> rounded down to a tile boundary.</summary>
    static abstract int GroupDepthOf(int bit);

    /// <summary>The boundary slot <paramref name="stem"/> descends into at <paramref name="depth"/>: its <see cref="LevelsPerGroup"/> path bits there.</summary>
    static abstract int SlotOf(in Stem stem, int depth);

    /// <summary>The number of 64-bit words needed for the group's position masks.</summary>
    static abstract int PositionMaskWordCount { get; }

    /// <summary>The number of 64-bit words needed for the group's boundary masks.</summary>
    static abstract int BoundaryMaskWordCount { get; }

    /// <summary>The greatest number of bytes the masks can take in a group's trailer.</summary>
    static abstract int MaxMaskTrailerLength { get; }

    /// <summary>Whether the boundary chain mask uses <see cref="CompactBitmap256"/> and therefore has a data-dependent length.</summary>
    static abstract bool HasCompactBoundaryMask { get; }

    /// <summary>The encoded mask-trailer length in bytes.</summary>
    static abstract int MaskTrailerLength { get; }

    /// <summary>Encodes masks into <paramref name="trailer"/>.</summary>
    static abstract void WriteMasks(Span<byte> trailer, in NodeGroupBitmasks masks);

    /// <summary>Decodes masks from <paramref name="trailer"/>.</summary>
    static abstract NodeGroupBitmasks ReadMasks(ReadOnlySpan<byte> trailer);
}
