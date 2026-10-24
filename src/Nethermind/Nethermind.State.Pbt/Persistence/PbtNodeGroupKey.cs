// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Pbt;
using Nethermind.Pbt.NodeBuffer;
using Nethermind.State.Pbt.NodeBuffer;

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
        if (!PbtTwoLevelGroupGeometry.IsGroupDepth(depth))
            throw new InvalidDataException("A persisted PBT node-group key depth must be a group boundary.");
        ReadOnlySpan<byte> path = key[..^TrailerLength];
        if (!PbtNodeCodec.IsCanonicalPath(path, depth, PbtStorageNodePath.MaxBitDepth))
            throw new InvalidDataException("Invalid persisted PBT node-group key padding.");
        return PbtStorageNodePath.Create(path, depth);
    }

    /// <summary>The groups of the tree under <paramref name="rootPointer"/> reserved at or after <paramref name="lowerBound"/>: the root group first, then the rest in key order.</summary>
    /// <remarks>
    /// The root group is keyed apart from the others. Below it, a linked group's subtree is one contiguous key range whose
    /// prefix is the group's path, and the ranges of a group's links follow their slot order. A group sorts before its
    /// subtree or inside one of its children's ranges, so it is carried down as pending until the walk passes it. Links below
    /// <paramref name="lowerBound"/>, and links not in the buffer, are not followed. A pin at or below
    /// <paramref name="lowerBound"/> must outlive every use of the records.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A record at or above <paramref name="lowerBound"/> is no longer resident.</exception>
    public static List<PbtGroupRecord> InKeyOrder(IPbtGroupRecordBuffer buffer, ulong rootPointer, ulong lowerBound)
    {
        List<PbtGroupRecord> ordered = [];
        if (!TryResolve(buffer, rootPointer, lowerBound, out PbtGroupRecord rootRecord)) return ordered;
        ordered.Add(rootRecord);
        MergeBelow(buffer, rootRecord, lowerBound, [], ordered);
        return ordered;
    }

    /// <summary>Emits the groups below <paramref name="record"/> merged with <paramref name="pending"/>, the sorted groups above them whose keys fall in its range.</summary>
    private static void MergeBelow(IPbtGroupRecordBuffer buffer, in PbtGroupRecord record, ulong lowerBound,
        List<(byte[] Key, PbtGroupRecord Record)> pending, List<PbtGroupRecord> ordered)
    {
        PbtGroupLinks links = record.Links;
        Span<byte> key = stackalloc byte[MaxLength];
        for (int slot = 0; slot < PbtTwoLevelGroupGeometry.BoundarySlots; slot++)
        {
            if (!TryResolve(buffer, links[slot], lowerBound, out PbtGroupRecord child)) continue;
            int below = 0;
            while (below < pending.Count && CompareToRange(pending[below].Key, child.Path) < 0) ordered.Add(pending[below++].Record);
            int inside = below;
            while (inside < pending.Count && CompareToRange(pending[inside].Key, child.Path) == 0) inside++;
            List<(byte[] Key, PbtGroupRecord Record)> childPending = pending.GetRange(below, inside - below);
            pending.RemoveRange(0, inside);
            byte[] childKey = Encode(child.Path, key).ToArray();
            int index = 0;
            while (index < childPending.Count && childPending[index].Key.AsSpan().SequenceCompareTo(childKey) < 0) index++;
            childPending.Insert(index, (childKey, child));
            MergeBelow(buffer, child, lowerBound, childPending, ordered);
        }
        foreach ((_, PbtGroupRecord group) in pending) ordered.Add(group);
    }

    private static bool TryResolve(IPbtGroupRecordBuffer buffer, ulong pointer, ulong lowerBound, out PbtGroupRecord record)
    {
        record = default;
        if (pointer == 0 || pointer < lowerBound) return false;
        if (!buffer.TryLocate(pointer, out record)) throw new InvalidOperationException("A group record written since the lower bound is no longer resident.");
        return true;
    }

    /// <returns>Negative or positive when <paramref name="key"/> sorts before or after every key starting with the bits of <paramref name="prefix"/>, or zero when it starts with them.</returns>
    private static int CompareToRange(ReadOnlySpan<byte> key, PbtStorageNodePath prefix)
    {
        Span<byte> bits = stackalloc byte[PbtVariableTreeKey.MaxLength];
        bits.Clear();
        PbtNodePathOperations.CopyTo(prefix, bits);
        int fullBytes = prefix.BitDepth >> 3;
        int order = key[..Math.Min(key.Length, fullBytes)].SequenceCompareTo(bits[..Math.Min(key.Length, fullBytes)]);
        if (order != 0) return order;
        if (key.Length < fullBytes) return -1;
        int remainder = prefix.BitDepth & 7;
        if (remainder == 0) return 0;
        if (key.Length == fullBytes) return -1;
        int mask = 0xFF << (8 - remainder) & 0xFF;
        return (key[fullBytes] & mask) - (bits[fullBytes] & mask);
    }
}
