// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json.Serialization;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Per-slot changes from a decoded BAL, sorted by <see cref="StorageChange.Index"/>
/// (decoder-validated). A parallel <c>uint[]</c> index lane drives the binary-search lookups
/// (<see cref="TryGetLastBefore"/>, <see cref="TryGetAtIndex"/>, <see cref="HasAtIndex"/>).
/// </summary>
public class ReadOnlySlotChanges(UInt256 key, StorageChange[] changes) : IEquatable<ReadOnlySlotChanges>
{
    public UInt256 Key { get; } = key;

    [JsonConverter(typeof(StorageChangesByIndexConverter))]
    public StorageChange[] Changes { get; } = changes;

    private readonly uint[] _indices = ExtractIndices(changes);

    public ReadOnlySlotChanges(UInt256 key) : this(key, []) { }

    public bool Equals(ReadOnlySlotChanges? other)
        => other is not null && Key.Equals(other.Key) && ((ReadOnlySpan<StorageChange>)Changes).SequenceEqual(other.Changes);

    public override bool Equals(object? obj) => obj is ReadOnlySlotChanges other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Key, Changes.Length);

    public override string ToString() => $"{Key}:[{string.Join(", ", Changes)}]";

    /// <summary>Last storage change strictly before <paramref name="blockAccessIndex"/>.
    /// Returns <c>false</c> when no entry is recorded before the index — caller can fall through
    /// to a parent-state reader.</summary>
    public bool TryGetLastBefore(uint blockAccessIndex, out StorageChange storageChange)
    {
        ReadOnlySpan<uint> indices = _indices;
        int idx = indices.BinarySearch(blockAccessIndex);
        // Whether found exactly or not, idx (or ~idx) is the position of the first entry with
        // Index >= blockAccessIndex. The last strictly-before entry is one step earlier.
        int lastBefore = (idx >= 0 ? idx : ~idx) - 1;
        if (lastBefore < 0)
        {
            storageChange = default;
            return false;
        }

        storageChange = Changes[lastBefore];
        return true;
    }

    /// <summary>The change recorded at exactly <paramref name="index"/>, if any.</summary>
    public bool TryGetAtIndex(uint index, out StorageChange storageChange)
    {
        int idx = ((ReadOnlySpan<uint>)_indices).BinarySearch(index);
        if (idx >= 0)
        {
            storageChange = Changes[idx];
            return true;
        }
        storageChange = default;
        return false;
    }

    /// <summary>True iff this slot has a change recorded at exactly <paramref name="index"/>.</summary>
    public bool HasAtIndex(uint index)
        => ((ReadOnlySpan<uint>)_indices).BinarySearch(index) >= 0;

    private static uint[] ExtractIndices(StorageChange[] changes)
    {
        if (changes.Length == 0) return [];
        uint[] indices = new uint[changes.Length];
        for (int i = 0; i < changes.Length; i++)
        {
            indices[i] = changes[i].Index;
        }
        return indices;
    }
}
