// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;

namespace Nethermind.Pbt;

/// <summary>
/// 6-level tiles, each its own blob: one store lookup serves six trie levels and no blob holds
/// another.
/// </summary>
/// <remarks>
/// The middle of the trade <see cref="PbtClusteredTileLayout"/> takes the other way. A lookup buys half
/// again as many levels for one blob rather than two, at the cost of a 64-way bucket sort per descent
/// frame instead of a 16-way one, and of storing the internal nodes along every root-to-slot path of a
/// wider tile.
/// <para>
/// 248 is not a multiple of six, so the deepest tile — rooted at <see cref="MaxGroupDepth"/> — reaches
/// four levels past the last stem bit. <see cref="Stem"/> is backed by a zero-padded 32 bytes, so
/// those bits read as zero and only four of that tile's slots can be occupied; two stems in one of
/// them would agree on all 248 bits, which is a duplicate the descent rejects. So each occupied slot
/// holds a single stem, which the fold hoists without hashing a level for it, and no node is ever
/// built below the stem level.
/// </para>
/// </remarks>
public readonly struct PbtSixLevelTileLayout : IPbtTileLayout
{
    public static PbtTiling Tiling => PbtTiling.SixLevel;

    public static int LevelsPerGroup => 6;

    public static int BoundarySlots => 1 << 6;

    public static int PositionCount => 2 * BoundarySlots - 1;

    public static int RootPosition => 2 * BoundarySlots - 2;

    /// <inheritdoc cref="PbtSixLevelTileLayout" path="/remarks"/>
    public static int MaxGroupDepth => Stem.LengthInBits - Stem.LengthInBits % LevelsPerGroup;

    public static int PositionMaskWordCount => 2;

    public static int BoundaryMaskWordCount => 1;

    public static int MaxMaskTrailerLength => 2 * 16 + sizeof(ulong);

    public static bool HasCompactBoundaryMask => false;

    public static int MaskTrailerLength => MaxMaskTrailerLength;

    public static bool IsClusteringDepth(int depth) => false;

    public static int GroupDepthOf(int bit) => bit - bit % LevelsPerGroup;

    /// <summary>
    /// The six path bits at <paramref name="depth"/>, read as a window over the stem's zero-padded
    /// bytes: a slot straddles two of them, and the deepest tile's window reaches past the stem.
    /// </summary>
    public static int SlotOf(in Stem stem, int depth) =>
        BinaryPrimitives.ReadUInt16BigEndian(stem.PaddedBytes[(depth >> 3)..]) >> (16 - LevelsPerGroup - (depth & 7)) & (BoundarySlots - 1);

    public static void WriteMasks(Span<byte> trailer, in NodeGroupBitmasks masks)
    {
        BinaryPrimitives.WriteUInt128LittleEndian(trailer, masks.Presence);
        BinaryPrimitives.WriteUInt128LittleEndian(trailer[16..], masks.Stems);
        BinaryPrimitives.WriteUInt64LittleEndian(trailer[32..], masks.Chains);
    }

    public static NodeGroupBitmasks ReadMasks(ReadOnlySpan<byte> trailer) => new(
        BinaryPrimitives.ReadUInt128LittleEndian(trailer),
        BinaryPrimitives.ReadUInt128LittleEndian(trailer[16..]),
        BinaryPrimitives.ReadUInt64LittleEndian(trailer[32..]));
}
