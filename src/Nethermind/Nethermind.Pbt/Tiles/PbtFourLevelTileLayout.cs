// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;

using Nethermind.Pbt;

namespace Nethermind.Pbt.Tiles;

/// <summary>4-level tiles, each its own blob.</summary>
public readonly struct PbtFourLevelTileLayout : IPbtTileLayout
{
    public static PbtTiling Tiling => PbtTiling.FourLevel;

    public static int RootDepth => 0;

    public static int LevelsPerGroup => 4;

    public static int BoundarySlots => 1 << 4;

    public static int PositionCount => 2 * BoundarySlots - 1;

    public static int RootPosition => 2 * BoundarySlots - 2;

    public static int MaxGroupDepth => Stem.LengthInBits - LevelsPerGroup;

    public static int PositionMaskWordCount => 1;

    public static int BoundaryMaskWordCount => 1;

    public static int MaxMaskTrailerLength => sizeof(uint) + sizeof(uint) + sizeof(ushort);

    public static bool HasCompactBoundaryMask => false;

    public static int MaskTrailerLength => MaxMaskTrailerLength;

    public static bool IsGroupDepth(int depth) => depth >= RootDepth && depth % LevelsPerGroup == 0;

    public static int GroupDepthOf(int bit) => bit & ~(LevelsPerGroup - 1);

    public static int SlotOf(in Stem stem, int depth) =>
        (depth & 4) == 0 ? stem.Bytes[depth >> 3] >> 4 : stem.Bytes[depth >> 3] & 0xF;

    public static void WriteMasks(Span<byte> trailer, in NodeGroupBitmasks masks)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(trailer, (uint)masks.Presence);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[sizeof(uint)..], (uint)masks.Stems);
        BinaryPrimitives.WriteUInt16LittleEndian(trailer[(2 * sizeof(uint))..], (ushort)masks.Chains);
    }

    public static NodeGroupBitmasks ReadMasks(ReadOnlySpan<byte> trailer) => new(
        BinaryPrimitives.ReadUInt32LittleEndian(trailer),
        BinaryPrimitives.ReadUInt32LittleEndian(trailer[sizeof(uint)..]),
        BinaryPrimitives.ReadUInt16LittleEndian(trailer[(2 * sizeof(uint))..]));
}
