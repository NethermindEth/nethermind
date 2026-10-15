// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Persistence;

/// <summary>Persisted node-group key.</summary>
/// <remarks>
/// The group path bytes, then a trailer byte: the number of path bits in the last byte, 0 when the path fills it. Keys
/// carry no padding, so a subtree is still one contiguous key range, but the trailer is compared against the next path
/// byte of longer keys. A byte-aligned group's 0 never exceeds that byte, and on a tie the shorter key wins, so it sorts
/// at the front of its subtree; any other group's trailer sorts it after the descendants whose next byte is below it,
/// inside its subtree's range rather than at its front.
/// </remarks>
public static class PbtNodeGroupKey
{
    private const int TrailerLength = 1;
    public const int MaxLength = PbtVariableTreeKey.MaxLength + TrailerLength;

    public static ReadOnlySpan<byte> Encode<TPath>(TPath groupKey, Span<byte> destination)
        where TPath : struct, IPbtNodePath<TPath>
    {
        Span<byte> key = destination[..(PbtBitPrefix.ByteCount(groupKey.BitDepth) + TrailerLength)];
        PbtNodePathOperations.CopyTo(groupKey, key);
        key[^1] = (byte)(groupKey.BitDepth & 7);
        return key;
    }

    public static PbtStorageNodePath Decode(ReadOnlySpan<byte> key)
    {
        // The root group lives under its own metadata key, never in a node-group column.
        if (key.Length < 1 + TrailerLength || key.Length - TrailerLength > PbtVariableTreeKey.MaxLength)
            throw new InvalidDataException("Invalid persisted PBT node-group key length.");
        if (key[^1] > 7) throw new InvalidDataException("Invalid persisted PBT node-group key trailer.");
        int bitsInLastByte = key[^1] == 0 ? 8 : key[^1];
        int depth = (key.Length - TrailerLength - 1) * 8 + bitsInLastByte;
        if (!PbtThreeLevelGroupGeometry.IsGroupDepth(depth))
            throw new InvalidDataException("A persisted PBT node-group key depth must be a group boundary.");
        ReadOnlySpan<byte> path = key[..^TrailerLength];
        if (!PbtNodeCodec.IsCanonicalPath(path, depth, PbtStorageNodePath.MaxBitDepth))
            throw new InvalidDataException("Invalid persisted PBT node-group key padding.");
        return PbtStorageNodePath.Create(path, depth);
    }
}
