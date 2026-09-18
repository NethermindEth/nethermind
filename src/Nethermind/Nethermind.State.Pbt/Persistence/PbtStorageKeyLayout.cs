// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Persistence;

/// <summary>
/// Persisted <see cref="PbtColumns.Storages"/> key: the address hash, then the zone byte, then the rest of the tree
/// key, so one account's header and overflow slots form a single contiguous range.
/// </summary>
/// <remarks>
/// The tree key is <c>[zone][addressHash][rest]</c>; the persisted form rotates its first 33 bytes. Lengths are
/// unchanged (34 bytes for header slots, 66 for overflow slots), so a persisted key decodes back to a valid tree key.
/// </remarks>
internal static class PbtStorageKeyLayout
{
    private const int ZoneOffset = ValueHash256.MemorySize;
    private const int RestOffset = ZoneOffset + 1;

    /// <summary>Orders tree keys as their persisted encodings sort.</summary>
    public static IComparer<PbtStorageTreeKey> Comparer { get; } = new PersistedOrderComparer();

    public static ReadOnlySpan<byte> Encode(in PbtStorageTreeKey key, Span<byte> destination)
    {
        ReadOnlySpan<byte> bytes = key.Bytes;
        bytes.Slice(1, ValueHash256.MemorySize).CopyTo(destination);
        destination[ZoneOffset] = bytes[0];
        bytes[RestOffset..].CopyTo(destination[RestOffset..]);
        return destination[..bytes.Length];
    }

    public static PbtStorageTreeKey Decode(ReadOnlySpan<byte> persisted)
    {
        Span<byte> key = stackalloc byte[PbtStorageTreeKey.MaxLength];
        key[0] = persisted[ZoneOffset];
        persisted[..ValueHash256.MemorySize].CopyTo(key[1..]);
        persisted[RestOffset..].CopyTo(key[RestOffset..]);
        return new PbtStorageTreeKey(key[..persisted.Length]);
    }

    private sealed class PersistedOrderComparer : IComparer<PbtStorageTreeKey>
    {
        public int Compare(PbtStorageTreeKey x, PbtStorageTreeKey y)
        {
            ReadOnlySpan<byte> left = x.Bytes;
            ReadOnlySpan<byte> right = y.Bytes;
            int order = left.Slice(1, ValueHash256.MemorySize).SequenceCompareTo(right.Slice(1, ValueHash256.MemorySize));
            if (order != 0) return order;
            order = left[0].CompareTo(right[0]);
            return order != 0 ? order : left[RestOffset..].SequenceCompareTo(right[RestOffset..]);
        }
    }
}
