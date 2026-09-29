// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Persistence;

/// <summary>Persisted node-group key.</summary>
/// <remarks>
/// The group path bytes, then a trailer byte: 0 when the path fills its last byte, 1 when it ends on a nibble. Keys
/// carry no padding, so a subtree is still one contiguous key range, but the trailer is compared against the next path
/// byte of longer keys. A byte-aligned group's 0 never exceeds that byte, and on a tie the shorter key wins, so it sorts
/// at the front of its subtree; a nibble group's 1 sorts it after the descendants whose next byte is zero, inside its
/// subtree's range rather than at its front.
/// </remarks>
internal static class PbtNodeGroupKey
{
    private const int TrailerLength = 1;
    private const byte ByteAlignedTrailer = 0;
    private const byte NibbleAlignedTrailer = 1;
    internal const int MaxLength = PbtStorageTreeKey.MaxLength + TrailerLength;

    // Top groups are keyed shorter than the zone and address hash, so the account/code width covers every zone.
    private static int PathLength(PbtColumns column) =>
        column == PbtColumns.StorageNodeGroups ? PbtStorageTreeKey.MaxLength : PbtTreeKey.MaxLength;

    internal static ReadOnlySpan<byte> Encode<TPath>(PbtColumns column, TPath groupKey, Span<byte> destination)
        where TPath : struct, IPbtNodePath<TPath>
    {
        if (PbtBitPrefix.ByteCount(groupKey.BitDepth) > PathLength(column)) throw new ArgumentOutOfRangeException(nameof(groupKey));
        Span<byte> key = destination[..(PbtBitPrefix.ByteCount(groupKey.BitDepth) + TrailerLength)];
        PbtNodePathOperations.CopyTo(groupKey, key);
        key[^1] = (groupKey.BitDepth & 7) == 0 ? ByteAlignedTrailer : NibbleAlignedTrailer;
        return key;
    }

    internal static PbtStorageNodePath Decode(ReadOnlySpan<byte> key)
    {
        // The root group lives under its own metadata key, never in a node-group column.
        if (key.Length < 1 + TrailerLength || key.Length - TrailerLength > PbtStorageTreeKey.MaxLength)
            throw new InvalidDataException("Invalid persisted PBT node-group key length.");
        int bitsInLastByte = key[^1] switch
        {
            ByteAlignedTrailer => 8,
            NibbleAlignedTrailer => PbtFourLevelGroupGeometry.LevelsPerGroup,
            _ => throw new InvalidDataException("Invalid persisted PBT node-group key trailer."),
        };
        int depth = (key.Length - TrailerLength - 1) * 8 + bitsInLastByte;
        if (!PbtFourLevelGroupGeometry.IsGroupDepth(depth))
            throw new InvalidDataException("A persisted PBT node-group key depth must be a four-level boundary.");
        try { return new PbtStorageNodePath(key[..^TrailerLength], depth); }
        catch (ArgumentException exception) { throw new InvalidDataException("Invalid persisted PBT node-group key padding.", exception); }
    }
}
