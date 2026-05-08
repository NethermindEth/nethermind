// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

public record SlotChanges(UInt256 Key, SortedList<int, StorageChange> Changes)
{
    public SlotChanges(UInt256 slot) : this(slot, new(GenericComparer.GetOptimized<int>())) { }

    public virtual bool Equals(SlotChanges? other) =>
        other is not null &&
        Key.Equals(other.Key) &&
        Changes.SequenceEqual(other.Changes);

    public override int GetHashCode() =>
        HashCode.Combine(Key, Changes);

    public override string ToString() => $"{Key}:[{string.Join(", ", Changes.Values)}]";


    public void Merge(SlotChanges other)
    {
        foreach (KeyValuePair<int, StorageChange> kv in other.Changes)
        {
            Changes[kv.Key] = kv.Value;
        }
    }

    public void AddStorageChange(StorageChange storageChange)
        => Changes.Add(storageChange.Index, storageChange);

    public bool TryPopStorageChange(int index, [NotNullWhen(true)] out StorageChange? storageChange)
    {
        storageChange = null;

        if (Changes.Count == 0)
            return false;

        StorageChange lastChange = Changes.Values[Changes.Count - 1];

        if (lastChange.Index == index)
        {
            Changes.RemoveAt(Changes.Count - 1);
            storageChange = lastChange;
            return true;
        }

        return false;
    }

    public byte[] Get(int blockAccessIndex)
    {
        Span<byte> tmp = stackalloc byte[32];
        UInt256 lastValue = 0;
        foreach (KeyValuePair<int, StorageChange> change in Changes)
        {
            if (change.Key >= blockAccessIndex)
            {
                lastValue.ToBigEndian(tmp);
                return [.. tmp.WithoutLeadingZeros()];
            }
            lastValue = change.Value.Value;
        }

        lastValue.ToBigEndian(tmp);
        return [.. tmp.WithoutLeadingZeros()];
    }
}
