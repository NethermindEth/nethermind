// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json.Serialization;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Per-slot changes from a decoded BAL, sorted by <see cref="StorageChange.Index"/>
/// (decoder-validated). Most slots are touched in exactly one tx, so the first change is stored
/// inline and the <see cref="StorageChange"/> array + <c>uint[]</c> index lane are only
/// allocated when a slot has more than one change. Read paths
/// (<see cref="TryGetLastBefore"/>, <see cref="TryGetAtIndex"/>, <see cref="HasAtIndex"/>) hit
/// the inline field directly for the common case; only multi-change slots fall through to the
/// binary search.
/// </summary>
public class ReadOnlySlotChanges : IEquatable<ReadOnlySlotChanges>
{
    public UInt256 Key { get; }

    private readonly StorageChange _first;
    // Full array (including _first at [0]) when count > 1; null when count <= 1.
    private readonly StorageChange[]? _multiple;
    // Parallel index lane for the multi-change path; null when count <= 1.
    private readonly uint[]? _indices;
    private readonly int _count;
    // Lazily materialised [_first] for count == 1 callers that need the array (encoder, JSON).
    // Benign race: concurrent first-access from multiple threads is rare in practice (Changes is
    // not hit on the validation read path) and any racing writers produce equal arrays.
    private StorageChange[]? _singletonChanges;

    public ReadOnlySlotChanges(UInt256 key, StorageChange[] changes)
    {
        Key = key;
        _count = changes.Length;
        if (_count == 0) return;
        _first = changes[0];
        if (_count == 1)
        {
            // Reuse the caller's array as the singleton cache when it's already length-1 — saves
            // a redundant allocation on the dominant decoder path.
            _singletonChanges = changes;
            return;
        }
        _multiple = changes;
        uint[] indices = new uint[_count];
        for (int i = 0; i < _count; i++) indices[i] = changes[i].Index;
        _indices = indices;
    }

    public ReadOnlySlotChanges(UInt256 key) : this(key, []) { }

    [JsonConverter(typeof(StorageChangesByIndexConverter))]
    public StorageChange[] Changes
    {
        get
        {
            if (_multiple is not null) return _multiple;
            if (_count == 0) return [];
            return _singletonChanges ??= [_first];
        }
    }

    public int Count => _count;

    public bool Equals(ReadOnlySlotChanges? other)
    {
        if (other is null) return false;
        if (!Key.Equals(other.Key)) return false;
        if (_count != other._count) return false;
        if (_count == 0) return true;
        if (_multiple is null) return _first.Equals(other._first);
        return ((ReadOnlySpan<StorageChange>)_multiple).SequenceEqual(other._multiple);
    }

    public override bool Equals(object? obj) => obj is ReadOnlySlotChanges other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Key, _count);

    public override string ToString() => $"{Key}:[{string.Join(", ", Changes)}]";

    /// <summary>Last storage change strictly before <paramref name="blockAccessIndex"/>.
    /// Returns <c>false</c> when no entry is recorded before the index — caller can fall through
    /// to a parent-state reader.</summary>
    public bool TryGetLastBefore(uint blockAccessIndex, out StorageChange storageChange)
    {
        if (_count == 0)
        {
            storageChange = default;
            return false;
        }
        if (_multiple is null)
        {
            if (_first.Index < blockAccessIndex)
            {
                storageChange = _first;
                return true;
            }
            storageChange = default;
            return false;
        }
        int idx = ((ReadOnlySpan<uint>)_indices!).BinarySearch(blockAccessIndex);
        // idx (if found) or ~idx (if not) is the position of the first entry with Index >= target;
        // strictly-before is one step earlier.
        int lastBefore = (idx >= 0 ? idx : ~idx) - 1;
        if (lastBefore < 0)
        {
            storageChange = default;
            return false;
        }
        storageChange = _multiple[lastBefore];
        return true;
    }

    /// <summary>The change recorded at exactly <paramref name="index"/>, if any.</summary>
    public bool TryGetAtIndex(uint index, out StorageChange storageChange)
    {
        if (_count == 0)
        {
            storageChange = default;
            return false;
        }
        if (_multiple is null)
        {
            if (_first.Index == index)
            {
                storageChange = _first;
                return true;
            }
            storageChange = default;
            return false;
        }
        int idx = ((ReadOnlySpan<uint>)_indices!).BinarySearch(index);
        if (idx >= 0)
        {
            storageChange = _multiple[idx];
            return true;
        }
        storageChange = default;
        return false;
    }

    /// <summary>True iff this slot has a change recorded at exactly <paramref name="index"/>.</summary>
    public bool HasAtIndex(uint index)
    {
        if (_count == 0) return false;
        if (_multiple is null) return _first.Index == index;
        return ((ReadOnlySpan<uint>)_indices!).BinarySearch(index) >= 0;
    }
}
