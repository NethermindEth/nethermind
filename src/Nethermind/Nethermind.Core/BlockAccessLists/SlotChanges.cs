// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

public record SlotChanges(UInt256 Slot, SortedList<ushort, StorageChange> Changes)
{
    public SlotChanges(UInt256 slot) : this(slot, []) { }

    public virtual bool Equals(SlotChanges? other) =>
        other is not null &&
        Slot.Equals(other.Slot) &&
        Changes.SequenceEqual(other.Changes);

    public override int GetHashCode() =>
        HashCode.Combine(Slot, Changes);

    public override string ToString() => $"{Slot}:[{string.Join(", ", Changes.Values)}]";

    public bool TryPopStorageChange(ushort index, [NotNullWhen(true)] out StorageChange? storageChange)
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
}
