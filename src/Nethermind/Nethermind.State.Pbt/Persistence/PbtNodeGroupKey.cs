// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Persistence;

/// <summary>Persisted node-group key.</summary>
/// <remarks>
/// The canonical path prefix at the preceding six-bit boundary, then a selector byte and the original path depth modulo
/// eight. The selector is zero at a six-bit boundary; otherwise its low three bits contain the remaining path bits.
/// A six-bit group and its eight next-level groups share the same prefix. The depth trailer distinguishes the group
/// from its zero-selector child, but does not guarantee that a group sorts before its descendants.
/// </remarks>
public static class PbtNodeGroupKey
{
    private const int TrailerLength = 2;
    public const int MaxLength = PbtVariableTreeKey.MaxLength + TrailerLength;

    public static ReadOnlySpan<byte> Encode<TPath>(TPath groupKey, Span<byte> destination)
        where TPath : struct, IPbtNodePath<TPath>
    {
        int selectorBitCount = groupKey.BitDepth % 6;
        int prefixDepth = groupKey.BitDepth - selectorBitCount;
        int prefixLength = PbtBitPrefix.ByteCount(prefixDepth);
        Span<byte> key = destination[..(prefixLength + TrailerLength)];
        for (int index = 0; index < prefixLength; index++) key[index] = groupKey.GetByte(index);
        if ((prefixDepth & 7) != 0) key[prefixLength - 1] &= (byte)(0xff << (8 - (prefixDepth & 7)));

        byte selector = 0;
        for (int index = prefixDepth; index < groupKey.BitDepth; index++)
            selector = (byte)((selector << 1) | ((groupKey.GetByte(index >> 3) >> (7 - (index & 7))) & 1));
        key[^2] = selector;
        key[^1] = (byte)(groupKey.BitDepth & 7);
        return key;
    }

    public static PbtStorageNodePath Decode(ReadOnlySpan<byte> key)
    {
        // The root group lives under its own metadata key, never in a node-group column.
        if (key.Length < TrailerLength || key.Length > MaxLength)
            throw new InvalidDataException("Invalid persisted PBT node-group key length.");
        if (key[^1] > 7) throw new InvalidDataException("Invalid persisted PBT node-group key trailer.");
        ReadOnlySpan<byte> prefix = key[..^TrailerLength];
        int bitsInLastByte = key[^1] == 0 ? 8 : key[^1];
        int depth = (prefix.Length - 1) * 8 + bitsInLastByte;
        // Removing three selector bits can shorten the prefix by one byte. Only one candidate is a group boundary.
        if (depth % PbtThreeLevelGroupGeometry.LevelsPerGroup != 0) depth += 8;
        if (depth <= 0 || !PbtThreeLevelGroupGeometry.IsGroupDepth(depth))
            throw new InvalidDataException("A persisted PBT node-group key depth must be a non-root group boundary.");
        int selectorBitCount = depth % 6;
        int prefixDepth = depth - selectorBitCount;
        if (!PbtNodeCodec.IsCanonicalPath(prefix, prefixDepth, PbtStorageNodePath.MaxBitDepth))
            throw new InvalidDataException("Invalid persisted PBT node-group key padding.");
        byte selector = key[^2];
        if (selector >= 1 << selectorBitCount)
            throw new InvalidDataException("Invalid persisted PBT node-group key selector.");

        Span<byte> path = stackalloc byte[PbtVariableTreeKey.MaxLength];
        path = path[..PbtBitPrefix.ByteCount(depth)];
        path.Clear();
        prefix.CopyTo(path);
        for (int index = 0; index < selectorBitCount; index++)
        {
            int bitIndex = prefixDepth + index;
            path[bitIndex >> 3] |= (byte)(((selector >> (selectorBitCount - index - 1)) & 1) << (7 - (bitIndex & 7)));
        }
        return PbtStorageNodePath.Create(path, depth);
    }
}
