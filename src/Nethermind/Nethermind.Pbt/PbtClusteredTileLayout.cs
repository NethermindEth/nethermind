// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;

namespace Nethermind.Pbt;

/// <summary>
/// 4-level tiles, every other depth holding its children's blobs so that a single store lookup serves
/// eight trie levels (see <see cref="PbtNodeCluster"/>).
/// </summary>
/// <remarks>
/// The descent radix-partitions each range into sixteen boundary slots per frame, which is what keeps
/// the tile narrow: the bucket sort carries a fixed bounds array per frame for ranges that are usually
/// one or two entries, so a wider tile multiplies that overhead on exactly the sparse frames that
/// dominate. Widening the unit of storage rather than the tile is what the cluster does instead.
/// </remarks>
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

    /// <summary>
    /// Which tiles hold their children, which alternates by tile so that every other level is reached
    /// without a lookup of its own.
    /// </summary>
    /// <remarks>
    /// Absolute rather than relative to the root or to the nearest run, which is what keeps a blob's
    /// placement a function of its depth alone: a run splitting at an intermediate depth would
    /// otherwise flip the parity of everything below it, re-keying a whole subtree. Depth 0 does not
    /// cluster, so the zone roots at depth 4 keep the keys their columns are routed by.
    /// </remarks>
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
