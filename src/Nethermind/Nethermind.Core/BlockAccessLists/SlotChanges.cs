// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

public record SlotChanges(UInt256 Key, IndexedChanges<StorageChange> Changes)
{
    public SlotChanges(UInt256 slot) : this(slot, new IndexedChanges<StorageChange>()) { }

    public SlotChanges(UInt256 slot, SortedList<uint, StorageChange> changes) : this(slot, IndexedChanges<StorageChange>.FromSortedList(changes)) { }

    public virtual bool Equals(SlotChanges? other)
    {
        if (other is null || !Key.Equals(other.Key) || Changes.Count != other.Changes.Count)
            return false;

        for (int i = 0; i < Changes.Count; i++)
        {
            if (Changes.Keys[i] != other.Changes.Keys[i] ||
                !Changes.Values[i].Equals(other.Changes.Values[i]))
            {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode() =>
        HashCode.Combine(Key, Changes);

    public override string ToString() => $"{Key}:[{string.Join(", ", Changes.Values)}]";


    public void Merge(SlotChanges other)
        => Changes.SetRange(other.Changes);

    public void AddStorageChange(StorageChange storageChange)
        => Changes.Add(storageChange);

    internal bool TryPopStorageChangeDirect(uint index, out StorageChange storageChange)
        => Changes.TryPopLast(index, out storageChange);

    public byte[] Get(uint blockAccessIndex)
    {
        Span<byte> tmp = stackalloc byte[32];
        UInt256 lastValue = 0;
        foreach (KeyValuePair<uint, StorageChange> change in Changes)
        {
            if (change.Key != Eip7928Constants.PrestateIndex && change.Key >= blockAccessIndex)
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
