
// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Nethermind.Core.Collections;
using System.Text.Json.Serialization;
using Nethermind.Serialization.Json;

namespace Nethermind.Core.BlockAccessLists;

public class SlotChanges(byte[] slot, SortedList<int, StorageChange> changes) : IEquatable<SlotChanges>
{
    [JsonConverter(typeof(ByteArrayConverter))]
    public byte[] Slot { get; init; } = slot;

    public EnumerableWithCount<StorageChange> Changes =>
        _changes.Keys.FirstOrDefault() == -1 ?
            new(_changes.Values.Skip(1), _changes.Count - 1) :
            new(_changes.Values, _changes.Count);

    private readonly SortedList<int, StorageChange> _changes = changes;

    public SlotChanges(byte[] slot) : this(slot, [])
    {
    }

    public bool Equals(SlotChanges? other) =>
        other is not null &&
        CompareByteArrays(Slot, other.Slot) &&
        Changes.SequenceEqual(other.Changes);

    public override bool Equals(object? obj) =>
        obj is SlotChanges other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Slot, Changes);

    private static bool CompareByteArrays(byte[]? left, byte[]? right) =>
        left switch
        {
            null when right == null => true,
            null => false,
            _ when right == null => false,
            _ => left.SequenceEqual(right)
        };

    public static bool operator ==(SlotChanges left, SlotChanges right) =>
        left.Equals(right);

    public static bool operator !=(SlotChanges left, SlotChanges right) =>
        !(left == right);

    public void AddStorageChange(StorageChange storageChange)
        => _changes.Add(storageChange.BlockAccessIndex, storageChange);

    public bool PopStorageChange(int index, [NotNullWhen(true)] out StorageChange? storageChange)
    {
        storageChange = null;

        if (_changes.Count == 0)
            return false;

        StorageChange lastChange = Changes.Last();

        if (lastChange.BlockAccessIndex == index)
        {
            _changes.RemoveAt(_changes.Count - 1);
            storageChange = lastChange;
            return true;
        }

        return false;
    }

    public byte[] Get(int blockAccessIndex)
    {
        return [];
    }

}