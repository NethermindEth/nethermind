// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt;

/// <summary>8-level tiles, each its own blob: one byte of a stem selects each tile's boundary slot.</summary>
public readonly struct PbtEightLevelTileLayout : IPbtTileLayout
{
    public static PbtTiling Tiling => PbtTiling.EightLevel;

    public static int LevelsPerGroup => 8;

    public static int BoundarySlots => 1 << LevelsPerGroup;

    public static int PositionCount => 2 * BoundarySlots - 1;

    public static int RootPosition => PositionCount - 1;

    public static int MaxGroupDepth => Stem.LengthInBits - LevelsPerGroup;

    public static int PositionMaskWordCount => (PositionCount + 63) / 64;

    public static int BoundaryMaskWordCount => BoundarySlots / 64;

    public static int MaxMaskTrailerLength => 2 * PositionMaskWordCount * sizeof(ulong) + CompactBitmap256.MaxEncodedLength;

    public static bool HasCompactBoundaryMask => true;

    public static int MaskTrailerLength => MaxMaskTrailerLength;

    public static bool IsClusteringDepth(int depth) => false;

    public static int GroupDepthOf(int bit) => bit & ~(LevelsPerGroup - 1);

    public static int SlotOf(in Stem stem, int depth) => stem.PaddedBytes[depth >> 3];

    public static void WriteMasks(Span<byte> trailer, in NodeGroupBitmasks masks) =>
        throw new NotSupportedException("Eight-level masks require the span-based group encoder");

    public static NodeGroupBitmasks ReadMasks(ReadOnlySpan<byte> trailer) =>
        throw new NotSupportedException("Eight-level masks require the span-based group decoder");
}
