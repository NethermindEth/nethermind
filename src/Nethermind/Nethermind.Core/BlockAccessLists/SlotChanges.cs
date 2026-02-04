
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json.Serialization;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Core.BlockAccessLists;

public class SlotChanges(UInt256 slot, SortedList<ushort, StorageChange> changes) : IEquatable<SlotChanges>
{
    [JsonConverter(typeof(UInt256Converter))]
    public UInt256 Slot { get; init; } = slot;
    public SortedList<ushort, StorageChange> Changes { get; init; } = changes;

    public SlotChanges(UInt256 slot) : this(slot, [])
    {
    }

    public bool Equals(SlotChanges? other) =>
        other is not null &&
        Slot.Equals(other.Slot) &&
        Changes.SequenceEqual(other.Changes);

    public override bool Equals(object? obj) =>
        obj is SlotChanges other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Slot, Changes);

    public static bool operator ==(SlotChanges left, SlotChanges right) =>
        left.Equals(right);

    public static bool operator !=(SlotChanges left, SlotChanges right) =>
        !(left == right);

    public bool PopStorageChange(ushort index, [NotNullWhen(true)] out StorageChange? storageChange)
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
