// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

public record SlotChanges(UInt256 Slot, SortedList<int, StorageChange> Changes)
{
    public SlotChanges(UInt256 slot) : this(slot, []) { }

    public virtual bool Equals(SlotChanges? other) =>
        other is not null &&
        Slot.Equals(other.Slot) &&
        Changes.SequenceEqual(other.Changes);

    public override int GetHashCode() =>
        HashCode.Combine(Slot, Changes);

    public override string ToString() => $"{Slot}:[{string.Join(", ", Changes.Values)}]";


    public void Merge(SlotChanges other)
    {
        foreach (KeyValuePair<int, StorageChange> kv in other.Changes)
        {
            Changes[kv.Key] = kv.Value;
        }
    }

    public void AddStorageChange(StorageChange storageChange)
        => Changes.Add(storageChange.BlockAccessIndex, storageChange);

    public bool TryPopStorageChange(int index, [NotNullWhen(true)] out StorageChange? storageChange)
    {
        storageChange = null;

        if (Changes.Count == 0)
            return false;

        StorageChange lastChange = Changes.Values.Last();

        if (lastChange.BlockAccessIndex == index)
        {
            Changes.RemoveAt(Changes.Count - 1);
            storageChange = lastChange;
            return true;
        }

        return false;
    }

    public byte[] Get(int blockAccessIndex)
    {
        UInt256 lastValue = 0;
        foreach (KeyValuePair<int, StorageChange> change in Changes)
        {
            if (change.Key >= blockAccessIndex)
            {
                return [.. lastValue.ToBigEndian().WithoutLeadingZeros()];
            }
            lastValue = change.Value.NewValue;
        }
        return [.. lastValue.ToBigEndian().WithoutLeadingZeros()];
    }
}
