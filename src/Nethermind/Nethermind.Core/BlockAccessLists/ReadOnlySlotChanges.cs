// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json.Serialization;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Per-slot changes from a decoded BAL. Backed by a <see cref="StorageChange"/> array sorted by
/// <see cref="StorageChange.Index"/> (the decoder validates ordering on the way in), so
/// <see cref="TryGetLastBefore"/> can binary-search via <see cref="System.MemoryExtensions"/>.
/// </summary>
public class ReadOnlySlotChanges(UInt256 key, StorageChange[] changes) : IEquatable<ReadOnlySlotChanges>
{
    public UInt256 Key { get; } = key;

    [JsonConverter(typeof(StorageChangesByIndexConverter))]
    public StorageChange[] Changes { get; } = changes;

    public bool Equals(ReadOnlySlotChanges? other)
        => other is not null && Key.Equals(other.Key) && ((ReadOnlySpan<StorageChange>)Changes).SequenceEqual(other.Changes);

    public override bool Equals(object? obj) => obj is ReadOnlySlotChanges other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Key, Changes.Length);

    public override string ToString() => $"{Key}:[{string.Join(", ", Changes)}]";

    public ReadOnlySlotChanges(UInt256 key) : this(key, []) { }

    /// <summary>Last storage change strictly before <paramref name="blockAccessIndex"/>.
    /// Returns <c>false</c> when no entry is recorded before the index — caller can fall through
    /// to a parent-state reader.</summary>
    public bool TryGetLastBefore(uint blockAccessIndex, out StorageChange storageChange)
    {
        ReadOnlySpan<StorageChange> span = Changes;
        int idx = span.BinarySearch(new IndexKey<StorageChange>(blockAccessIndex));
        // Whether found exactly or not, idx (or ~idx) is the position of the first entry with
        // Index >= blockAccessIndex. The last strictly-before entry is one step earlier.
        int lastBefore = (idx >= 0 ? idx : ~idx) - 1;
        if (lastBefore < 0)
        {
            storageChange = default;
            return false;
        }

        storageChange = span[lastBefore];
        return true;
    }
}
