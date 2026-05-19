// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json.Serialization;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Per-slot changes from a decoded BAL, sorted by <see cref="StorageChange.Index"/>
/// (decoder-validated). The first change is stored inline; <see cref="_multiple"/> and the
/// parallel <c>uint[]</c> index lane are only allocated for slots with more than one change.
/// </summary>
public class ReadOnlySlotChanges : IEquatable<ReadOnlySlotChanges>
{
    public UInt256 Key { get; }

    private readonly StorageChange _first;
    // count > 1: full array including _first at [0]. count <= 1: null.
    private readonly StorageChange[]? _multiple;
    private readonly uint[]? _indices;
    private readonly int _count;
    // count == 1 only; lazy [_first] for the array-form Changes accessor.
    // Single-writer post-construction in practice (validation read path skips Changes); the ??= is
    // a benign race if ever shared — both threads compute the same [_first] array.
    private StorageChange[]? _singletonChanges;

    public ReadOnlySlotChanges(UInt256 key, StorageChange[] changes)
    {
        Key = key;
        _count = changes.Length;
        if (_count == 0) return;
        _first = changes[0];
        if (_count == 1)
        {
            // Caller's already-allocated length-1 array doubles as the singleton cache.
            // Caller must not mutate changes[0] post-construction (decoder path does not).
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

    public bool Equals(ReadOnlySlotChanges? other)
    {
        if (other is null) return false;
        if (!Key.Equals(other.Key)) return false;
        if (_count != other._count) return false;
        if (_count == 0) return true;
        if (_multiple is null) return _first.Equals(other._first);
        // _count match + _multiple != null ⇒ other._multiple != null by the same invariant.
        return ((ReadOnlySpan<StorageChange>)_multiple).SequenceEqual(other._multiple!);
    }

    public override bool Equals(object? obj) => obj is ReadOnlySlotChanges other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Key, _count);

    public override string ToString() => $"{Key}:[{string.Join(", ", Changes)}]";

    /// <summary>
    /// Last storage change strictly before <paramref name="blockAccessIndex"/>; <c>false</c> if none.
    /// </summary>
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
        return IndexLane.TryGetLastBefore(_indices!, _multiple, blockAccessIndex, out storageChange);
    }

    /// <summary>
    /// The change at exactly <paramref name="index"/>, if any.
    /// </summary>
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
        return IndexLane.TryGetExact(_indices!, _multiple, index, out storageChange);
    }

    public bool HasAtIndex(uint index)
    {
        if (_count == 0) return false;
        if (_multiple is null) return _first.Index == index;
        return IndexLane.HasExact(_indices!, index);
    }
}
