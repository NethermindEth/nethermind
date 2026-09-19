// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Persistence;

/// <summary>Persisted node-group key, in the <see cref="PbtNodeGroupKeyLayout"/> the database was created with.</summary>
/// <remarks>
/// <see cref="PbtNodeGroupKeyLayout.Padded"/> is the group path zero-padded to the column's full-key length, then its
/// nibble count. Unlike <see cref="IPbtNodePath{TSelf}.Encode"/>, which sorts by depth first, this sorts a group
/// immediately before its descendants, so a subtree is stored contiguously. The nibble count breaks the tie between a
/// group and a descendant whose extra bits are all zero.
/// <para/>
/// <see cref="PbtNodeGroupKeyLayout.Variable"/> is the group path bytes, then a trailer byte: 0 when the path fills its
/// last byte, 1 when it ends on a nibble. Keys carry no padding, so a subtree is still one contiguous key range, but the
/// trailer is compared against the next path byte of longer keys. A byte-aligned group's 0 never exceeds that byte, and
/// on a tie the shorter key wins, so it sorts at the front of its subtree; a nibble group's 1 sorts it after the
/// descendants whose next byte is zero, inside its subtree's range rather than at its front.
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

    internal static ReadOnlySpan<byte> Encode<TPath>(PbtNodeGroupKeyLayout layout, PbtColumns column, TPath groupKey, Span<byte> destination)
        where TPath : struct, IPbtNodePath<TPath> => layout switch
        {
            PbtNodeGroupKeyLayout.Padded => EncodePadded(column, groupKey, destination),
            PbtNodeGroupKeyLayout.Variable => EncodeVariable(column, groupKey, destination),
            _ => throw new ArgumentOutOfRangeException(nameof(layout)),
        };

    internal static PbtStorageNodePath Decode(PbtNodeGroupKeyLayout layout, ReadOnlySpan<byte> key) => layout switch
    {
        PbtNodeGroupKeyLayout.Padded => DecodePadded(key),
        PbtNodeGroupKeyLayout.Variable => DecodeVariable(key),
        _ => throw new ArgumentOutOfRangeException(nameof(layout)),
    };

    private static ReadOnlySpan<byte> EncodePadded<TPath>(PbtColumns column, TPath groupKey, Span<byte> destination)
        where TPath : struct, IPbtNodePath<TPath>
    {
        Span<byte> key = destination[..(PathLength(column) + TrailerLength)];
        Span<byte> path = key[..^TrailerLength];
        path.Clear();
        groupKey.CopyBitsTo(0, path, 0, groupKey.BitDepth);
        key[^1] = (byte)(groupKey.BitDepth / PbtFourLevelGroupGeometry.LevelsPerGroup);
        return key;
    }

    private static PbtStorageNodePath DecodePadded(ReadOnlySpan<byte> key)
    {
        int capacity = key.Length - TrailerLength;
        if (capacity is not (PbtTreeKey.MaxLength or PbtStorageTreeKey.MaxLength))
            throw new InvalidDataException("Invalid persisted PBT node-group key length.");
        int depth = key[^1] * PbtFourLevelGroupGeometry.LevelsPerGroup;
        // The root group lives under its own metadata key, never in a node-group column.
        if (depth == 0 || !PbtFourLevelGroupGeometry.IsGroupDepth(depth))
            throw new InvalidDataException("A persisted PBT node-group key depth must be a four-level boundary.");
        int pathLength = (depth + 7) >> 3;
        if (pathLength > capacity || key[pathLength..capacity].ContainsAnyExcept((byte)0))
            throw new InvalidDataException("Invalid persisted PBT node-group key padding.");
        return CreatePath(key[..pathLength], depth);
    }

    private static ReadOnlySpan<byte> EncodeVariable<TPath>(PbtColumns column, TPath groupKey, Span<byte> destination)
        where TPath : struct, IPbtNodePath<TPath>
    {
        int pathLength = (groupKey.BitDepth + 7) >> 3;
        if (pathLength > PathLength(column)) throw new ArgumentOutOfRangeException(nameof(groupKey));
        Span<byte> key = destination[..(pathLength + TrailerLength)];
        key[^2] = 0;
        groupKey.CopyBitsTo(0, key, 0, groupKey.BitDepth);
        key[^1] = (groupKey.BitDepth & 7) == 0 ? ByteAlignedTrailer : NibbleAlignedTrailer;
        return key;
    }

    private static PbtStorageNodePath DecodeVariable(ReadOnlySpan<byte> key)
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
        return CreatePath(key[..^TrailerLength], depth);
    }

    private static PbtStorageNodePath CreatePath(ReadOnlySpan<byte> path, int depth)
    {
        try { return PbtStorageNodePath.Create(path, depth); }
        catch (ArgumentException exception) { throw new InvalidDataException("Invalid persisted PBT node-group key padding.", exception); }
    }
}
