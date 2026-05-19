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

    /// <summary>
    /// Last storage change strictly before <paramref name="blockAccessIndex"/>; <c>false</c> if none.
    /// </summary>
    public bool TryGetLastBefore(uint blockAccessIndex, out StorageChange storageChange)
        => IndexLane.TryGetLastBefore(_indices, Changes, blockAccessIndex, out storageChange);

    /// <summary>
    /// The change at exactly <paramref name="index"/>, if any.
    /// </summary>
    public bool TryGetAtIndex(uint index, out StorageChange storageChange)
        => IndexLane.TryGetExact(_indices, Changes, index, out storageChange);

    public bool HasAtIndex(uint index) => IndexLane.HasExact(_indices, index);

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
