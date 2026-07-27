// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;

namespace Nethermind.Pbt;

/// <summary>
/// 4-level tiles, every other depth holding its children's blobs so that a single store lookup serves
/// eight trie levels (see <see cref="PbtNodeCluster"/>).
/// </summary>
/// <remarks>Four-level tiles keep sparse-frame bucket bounds small while clustered storage amortizes lookups.</remarks>
public readonly struct PbtClusteredTileLayout : IPbtTileLayout
{
    public static PbtTiling Tiling => PbtTiling.ClusteredFourLevel;

    public static int LevelsPerGroup => 4;

    public static int BoundarySlots => 1 << 4;

    public static int PositionCount => 2 * BoundarySlots - 1;

    public static int RootPosition => 2 * BoundarySlots - 2;

    /// <summary>The deepest tile root depth; that tile's boundary is the 248-bit stem level, where every node is a stem.</summary>
    public static int MaxGroupDepth => Stem.LengthInBits - LevelsPerGroup;

    public static int PositionMaskWordCount => 1;

    public static int BoundaryMaskWordCount => 1;

    public static int MaxMaskTrailerLength => sizeof(uint) + sizeof(uint) + sizeof(ushort);

    public static bool HasCompactBoundaryMask => false;

    public static int MaskTrailerLength => MaxMaskTrailerLength;

    /// <summary>Whether the tile holds child blobs.</summary>
    /// <remarks>Absolute depth prevents a splitting run from re-keying descendants.</remarks>
    public static bool IsClusteringDepth(int depth) => (depth & LevelsPerGroup) != 0;

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
