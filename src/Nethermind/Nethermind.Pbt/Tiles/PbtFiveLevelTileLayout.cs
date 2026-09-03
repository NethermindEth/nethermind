// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;

using Nethermind.Pbt;

namespace Nethermind.Pbt.Tiles;

/// <summary>5-level tiles, each its own blob.</summary>
public readonly struct PbtFiveLevelTileLayout : IPbtTileLayout
{
    public static PbtTiling Tiling => PbtTiling.FiveLevel;

    public static int RootDepth => 0;

    public static int LevelsPerGroup => 5;

    public static int BoundarySlots => 1 << LevelsPerGroup;

    public static int PositionCount => 2 * BoundarySlots - 1;

    public static int RootPosition => PositionCount - 1;

    public static int MaxGroupDepth => Stem.LengthInBits - Stem.LengthInBits % LevelsPerGroup;

    public static int PositionMaskWordCount => 1;

    public static int BoundaryMaskWordCount => 1;

    public static int MaxMaskTrailerLength => 2 * sizeof(ulong) + sizeof(uint);

    public static bool HasCompactBoundaryMask => false;

    public static int MaskTrailerLength => MaxMaskTrailerLength;

    public static bool IsGroupDepth(int depth) => depth >= RootDepth && depth % LevelsPerGroup == 0;

    public static int GroupDepthOf(int bit) => bit - bit % LevelsPerGroup;

    public static int SlotOf(in Stem stem, int depth) =>
        BinaryPrimitives.ReadUInt16BigEndian(stem.PaddedBytes[(depth >> 3)..]) >> (16 - LevelsPerGroup - (depth & 7)) & (BoundarySlots - 1);

    public static void WriteMasks(Span<byte> trailer, in NodeGroupBitmasks masks)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(trailer, (ulong)masks.Presence);
        BinaryPrimitives.WriteUInt64LittleEndian(trailer[sizeof(ulong)..], (ulong)masks.Stems);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[(2 * sizeof(ulong))..], (uint)masks.Chains);
    }

    public static NodeGroupBitmasks ReadMasks(ReadOnlySpan<byte> trailer) => new(
        BinaryPrimitives.ReadUInt64LittleEndian(trailer),
        BinaryPrimitives.ReadUInt64LittleEndian(trailer[sizeof(ulong)..]),
        BinaryPrimitives.ReadUInt32LittleEndian(trailer[(2 * sizeof(ulong))..]));
}
