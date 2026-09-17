// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Persistence;

/// <summary>Persisted node-group key: the group path zero-padded to the column's full-key length, then its nibble count.</summary>
/// <remarks>
/// Unlike <see cref="IPbtNodePath{TSelf}.Encode"/>, which sorts by depth first, this layout sorts a group immediately
/// before its descendants, so a subtree is stored contiguously. The nibble count breaks the tie between a group and a
/// descendant whose extra bits are all zero.
/// </remarks>
internal static class PbtNodeGroupKey
{
    private const int NibbleCountLength = 1;
    internal const int MaxLength = PbtStorageFullKey.MaxLength + NibbleCountLength;

    private static int PathLength(PbtColumns column) =>
        column == PbtColumns.StorageNodeGroups ? PbtStorageFullKey.MaxLength : PbtFullKey.MaxLength;

    internal static ReadOnlySpan<byte> Encode<TPath>(PbtColumns column, TPath groupKey, Span<byte> destination)
        where TPath : struct, IPbtNodePath<TPath>
    {
        Span<byte> key = destination[..(PathLength(column) + NibbleCountLength)];
        Span<byte> path = key[..^NibbleCountLength];
        path.Clear();
        groupKey.CopyBitsTo(0, path, 0, groupKey.BitDepth);
        key[^1] = (byte)(groupKey.BitDepth / PbtFourLevelGroupGeometry.LevelsPerGroup);
        return key;
    }

    internal static PbtStorageNodePath Decode(ReadOnlySpan<byte> key)
    {
        int capacity = key.Length - NibbleCountLength;
        if (capacity is not (PbtFullKey.MaxLength or PbtStorageFullKey.MaxLength))
            throw new InvalidDataException("Invalid persisted PBT node-group key length.");
        int depth = key[^1] * PbtFourLevelGroupGeometry.LevelsPerGroup;
        // The root group lives under its own metadata key, never in a node-group column.
        if (depth == 0 || !PbtFourLevelGroupGeometry.IsGroupDepth(depth))
            throw new InvalidDataException("A persisted PBT node-group key depth must be a four-level boundary.");
        int pathLength = (depth + 7) >> 3;
        if (pathLength > capacity || key[pathLength..capacity].ContainsAnyExcept((byte)0))
            throw new InvalidDataException("Invalid persisted PBT node-group key padding.");
        try { return PbtStorageNodePath.Create(key[..pathLength], depth); }
        catch (ArgumentException exception) { throw new InvalidDataException("Invalid persisted PBT node-group key padding.", exception); }
    }
}
