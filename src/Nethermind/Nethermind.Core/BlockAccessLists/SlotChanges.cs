
// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;

namespace Nethermind.Core.BlockAccessLists;

public readonly struct SlotChanges(byte[] slot, List<StorageChange> changes) : IEquatable<SlotChanges>
{
    public byte[] Slot { get; init; } = slot;
    public List<StorageChange> Changes { get; init; } = changes;

    public SlotChanges(byte[] slot) : this(slot, [])
    {
    }

    public readonly bool Equals(SlotChanges other) =>
        CompareByteArrays(Slot, other.Slot) &&
        Changes.SequenceEqual(other.Changes);

    public override readonly bool Equals(object? obj) =>
        obj is SlotChanges other && Equals(other);

    public override readonly int GetHashCode() =>
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
}
