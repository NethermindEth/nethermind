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
public static class PbtNodeGroupKey
{
    private const int TrailerLength = 1;
    private const byte ByteAlignedTrailer = 0;
    private const byte NibbleAlignedTrailer = 1;
    public const int MaxLength = PbtVariableTreeKey.MaxLength + TrailerLength;

    public static ReadOnlySpan<byte> Encode<TPath>(TPath groupKey, Span<byte> destination)
        where TPath : struct, IPbtNodePath<TPath>
    {
        Span<byte> key = destination[..(PbtBitPrefix.ByteCount(groupKey.BitDepth) + TrailerLength)];
        PbtNodePathOperations.CopyTo(groupKey, key);
        key[^1] = (groupKey.BitDepth & 7) == 0 ? ByteAlignedTrailer : NibbleAlignedTrailer;
        return key;
    }

    public static PbtStorageNodePath Decode(ReadOnlySpan<byte> key)
    {
        // The root group lives under its own metadata key, never in a node-group column.
        if (key.Length < 1 + TrailerLength || key.Length - TrailerLength > PbtVariableTreeKey.MaxLength)
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
        ReadOnlySpan<byte> path = key[..^TrailerLength];
        if (!PbtNodeCodec.IsCanonicalPath(path, depth, PbtStorageNodePath.MaxBitDepth))
            throw new InvalidDataException("Invalid persisted PBT node-group key padding.");
        return PbtStorageNodePath.Create(path, depth);
    }
}
