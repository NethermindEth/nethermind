// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Persistence;

/// <summary>Persisted node-group key.</summary>
/// <remarks>
/// The group path bytes, then a trailer byte holding the number of path bits in the last path byte: 0 when the path
/// fills it, and the bit count otherwise, save that 1 and 4 are swapped, so a nibble-aligned path keeps the trailer 1 it
/// had when groups were four levels deep. Keys carry no padding, so a subtree is still one contiguous key range, but the
/// trailer is compared against the next path byte of longer keys. A byte-aligned group's 0 never exceeds that byte, and
/// on a tie the shorter key wins, so it sorts at the front of its subtree; any other group's trailer sorts it after the
/// descendants whose next byte is below it, inside its subtree's range rather than at its front.
/// </remarks>
internal static class PbtNodeGroupKey
{
    private const int TrailerLength = 1;
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
        key[^1] = SwapOneAndFour(groupKey.BitDepth & 7);
        return key;
    }

    internal static PbtStorageNodePath Decode(ReadOnlySpan<byte> key)
    {
        // The root group lives under its own metadata key, never in a node-group column.
        if (key.Length < 1 + TrailerLength || key.Length - TrailerLength > PbtStorageTreeKey.MaxLength)
            throw new InvalidDataException("Invalid persisted PBT node-group key length.");
        if (key[^1] > 7) throw new InvalidDataException("Invalid persisted PBT node-group key trailer.");
        int bitsInLastByte = key[^1] == 0 ? 8 : SwapOneAndFour(key[^1]);
        int depth = (key.Length - TrailerLength - 1) * 8 + bitsInLastByte;
        if (!PbtGroupGeometry.IsGroupDepth(depth))
            throw new InvalidDataException("A persisted PBT node-group key depth must be a group boundary.");
        try { return new PbtStorageNodePath(key[..^TrailerLength], depth); }
        catch (ArgumentException exception) { throw new InvalidDataException("Invalid persisted PBT node-group key padding.", exception); }
    }

    private static byte SwapOneAndFour(int bits) => (byte)(bits switch { 1 => 4, 4 => 1, _ => bits });
}
