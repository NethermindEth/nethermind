// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Text.Json.Serialization;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Per-slot changes from a decoded BAL. Backed by a <see cref="StorageChange"/> array sorted by
/// <see cref="StorageChange.Index"/> (the decoder validates ordering on the way in), so
/// <see cref="Get(int)"/> can binary-search via <see cref="System.MemoryExtensions"/>.
/// </summary>
public class ReadOnlySlotChanges(UInt256 key, StorageChange[] changes) : IEquatable<ReadOnlySlotChanges>
{
    public UInt256 Key { get; } = key;

    [JsonConverter(typeof(StorageChangesByIndexConverter))]
    public StorageChange[] Changes { get; private set; } = changes;

    public bool Equals(ReadOnlySlotChanges? other)
        => other is not null && Key.Equals(other.Key) && Changes.SequenceEqual(other.Changes);

    public override bool Equals(object? obj) => obj is ReadOnlySlotChanges other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Key, Changes.Length);

    public override string ToString() => $"{Key}:[{string.Join(", ", Changes)}]";

    public ReadOnlySlotChanges(UInt256 key) : this(key, []) { }

    /// <summary>Storage value as visible at the start of <paramref name="blockAccessIndex"/> (i.e. last change strictly before the index).</summary>
    public byte[] Get(int blockAccessIndex)
    {
        Span<byte> tmp = stackalloc byte[32];
        UInt256 lastValue = 0;

        ReadOnlySpan<StorageChange> span = Changes;
        int idx = span.BinarySearch(new IndexKey<StorageChange>(blockAccessIndex));
        // Whether found exactly or not, idx (or ~idx) is the position of the first entry with
        // Index >= blockAccessIndex. The last strictly-before entry is one step earlier.
        int lastBefore = (idx >= 0 ? idx : ~idx) - 1;
        if (lastBefore >= 0)
        {
            lastValue = span[lastBefore].Value;
        }

        lastValue.ToBigEndian(tmp);
        return [.. tmp.WithoutLeadingZeros()];
    }

    /// <summary>Adds a prestate change at index -1 — only used during prestate loading.
    /// Prepended (one realloc) to preserve the sorted-by-index invariant.</summary>
    public void LoadPreStateChange(StorageChange storageChange) => Changes = [storageChange, .. Changes];
}
