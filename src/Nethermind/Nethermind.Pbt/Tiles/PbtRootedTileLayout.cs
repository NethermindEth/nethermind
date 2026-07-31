// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Pbt;

namespace Nethermind.Pbt.Tiles;

/// <summary>A compile-time tile-grid root depth.</summary>
public interface IPbtTileRoot
{
    static abstract int Depth { get; }
}

/// <summary>The account and code partition root depth.</summary>
public readonly struct PbtDepth4 : IPbtTileRoot
{
    public static int Depth => PbtKeyDerivation.ZoneBits;
}

/// <summary>The storage partition root depth.</summary>
public readonly struct PbtDepth1 : IPbtTileRoot
{
    public static int Depth => 1;
}

/// <summary>Places <typeparamref name="TLayout"/>'s tile grid at <typeparamref name="TRoot"/>.</summary>
public readonly struct PbtRootedTileLayout<TLayout, TRoot> : IPbtTileLayout
    where TLayout : IPbtTileLayout
    where TRoot : IPbtTileRoot
{
    public static PbtTiling Tiling => TLayout.Tiling;
    public static int RootDepth => TRoot.Depth;
    public static int LevelsPerGroup => TLayout.LevelsPerGroup;
    public static int BoundarySlots => TLayout.BoundarySlots;
    public static int PositionCount => TLayout.PositionCount;
    public static int RootPosition => TLayout.RootPosition;
    public static int MaxGroupDepth => RootDepth + (Stem.LengthInBits - RootDepth - 1) / LevelsPerGroup * LevelsPerGroup;
    public static int PositionMaskWordCount => TLayout.PositionMaskWordCount;
    public static int BoundaryMaskWordCount => TLayout.BoundaryMaskWordCount;
    public static int MaxMaskTrailerLength => TLayout.MaxMaskTrailerLength;
    public static bool HasCompactBoundaryMask => TLayout.HasCompactBoundaryMask;
    public static int MaskTrailerLength => TLayout.MaskTrailerLength;
    public static bool IsGroupDepth(int depth) => depth >= RootDepth && TLayout.IsGroupDepth(depth - RootDepth);
    public static int GroupDepthOf(int bit) => RootDepth + (bit - RootDepth) / LevelsPerGroup * LevelsPerGroup;

    public static int SlotOf(in Stem stem, int depth)
    {
        int slot = 0;
        for (int bit = 0; bit < LevelsPerGroup; bit++)
        {
            int stemBit = depth + bit;
            slot = slot << 1 | (stemBit < Stem.LengthInBits ? stem.GetBit(stemBit) : 0);
        }

        return slot;
    }

    public static void WriteMasks(Span<byte> trailer, in NodeGroupBitmasks masks) => TLayout.WriteMasks(trailer, masks);
    public static NodeGroupBitmasks ReadMasks(ReadOnlySpan<byte> trailer) => TLayout.ReadMasks(trailer);
}
