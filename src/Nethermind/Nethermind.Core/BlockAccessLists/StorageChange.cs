
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

public readonly struct StorageChange(int index, UInt256 value) : IEquatable<StorageChange>, IIndexedChange
{
    public int Index { get; init; } = index;

    public UInt256 Value { get; init; } = value;

    public readonly bool Equals(StorageChange other) =>
        Index == other.Index &&
        Value.Equals(other.Value);

    public override readonly bool Equals(object? obj) =>
        obj is StorageChange other && Equals(other);

    public override readonly int GetHashCode() =>
        HashCode.Combine(Index, Value);

    public static bool operator ==(StorageChange left, StorageChange right) =>
        left.Equals(right);

    public static bool operator !=(StorageChange left, StorageChange right) =>
        !(left == right);

    public override readonly string ToString() => $"{Index}:{Value}";
}
