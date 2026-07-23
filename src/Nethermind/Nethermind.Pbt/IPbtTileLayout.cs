// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt;

/// <summary>Which tiling of the stem trie a database is written in; the shape a whole tree shares.</summary>
/// <remarks>
/// Unlike <see cref="PbtGroupFormat"/>, which two blobs of one tree may differ in, this fixes the
/// keys the tree is stored under: a tree cannot hold blobs of both. It is stamped on the database and
/// checked on the way in.
/// </remarks>
public enum PbtTiling : byte
{
    /// <summary>4-level tiles, every other depth holding its children's blobs (<see cref="PbtClusteredTileLayout"/>).</summary>
    ClusteredFourLevel = 0,

    /// <summary>6-level tiles, each its own blob (<see cref="PbtSixLevelTileLayout"/>).</summary>
    SixLevel = 1,
}

/// <summary>What a trie node group's encoding is written in: its tiling, and which levels it stores.</summary>
public readonly record struct PbtTrieFormat(PbtTiling Tiling, PbtGroupFormat GroupFormat);

/// <summary>
/// The shape of one tiling of the stem trie: how many levels a tile covers, how a stem picks the slot
/// it descends into, and how wide the bitmaps pinning its entries are.
/// </summary>
/// <remarks>
/// Implemented by empty structs and consumed as a type parameter, so every tiling folds to the
/// constants it was written with rather than reading them back through a field. What does not depend
/// on the tile's width — the post-order numbering, the levels a format skips — is shared in
/// <see cref="PbtLayout"/>.
/// </remarks>
public interface IPbtTileLayout
{
    static abstract PbtTiling Tiling { get; }

    /// <summary>Trie levels covered by one tile: a tile rooted at depth d has its boundary slots at depth d + this.</summary>
    static abstract int LevelsPerGroup { get; }

    static abstract int BoundarySlots { get; }

    static abstract int PositionCount { get; }

    static abstract int RootPosition { get; }

    /// <summary>The deepest tile root depth, which is the deepest key this tiling ever writes.</summary>
    static abstract int MaxGroupDepth { get; }

    /// <summary>Whether the tile at <paramref name="depth"/> holds its children's blobs inside its own.</summary>
    static abstract bool IsClusteringDepth(int depth);

    /// <summary>The depth of the tile holding trie level <paramref name="bit"/>: <paramref name="bit"/> rounded down to a tile boundary.</summary>
    static abstract int GroupDepthOf(int bit);

    /// <summary>The boundary slot <paramref name="stem"/> descends into at <paramref name="depth"/>: its <see cref="LevelsPerGroup"/> path bits there.</summary>
    static abstract int SlotOf(in Stem stem, int depth);

    /// <summary>Gathers the boundary bits of <paramref name="positionsBitmask"/> down into slot order.</summary>
    static abstract ulong BoundaryBitmask(UInt128 positionsBitmask);

    /// <summary>The bytes the bitmaps take in a group's trailer.</summary>
    static abstract int MaskTrailerLength { get; }

    static abstract void WriteMasks(Span<byte> trailer, in NodeGroupBitmasks masks);

    static abstract NodeGroupBitmasks ReadMasks(ReadOnlySpan<byte> trailer);
}
