
// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;

namespace Nethermind.Core.BlockAccessLists;

public readonly struct StorageChange(ushort blockAccessIndex, Bytes32 newValue) : IEquatable<StorageChange>
{
    public ushort BlockAccessIndex { get; init; } = blockAccessIndex;
    public Bytes32 NewValue { get; init; } = newValue;

    public readonly bool Equals(StorageChange other) =>
        BlockAccessIndex == other.BlockAccessIndex &&
        NewValue.Unwrap().SequenceEqual(other.NewValue.Unwrap());

    public override readonly bool Equals(object? obj) =>
        obj is StorageChange other && Equals(other);

    public override readonly int GetHashCode() =>
        HashCode.Combine(BlockAccessIndex, NewValue);

    public static bool operator ==(StorageChange left, StorageChange right) =>
        left.Equals(right);

    public static bool operator !=(StorageChange left, StorageChange right) =>
        !(left == right);
}
