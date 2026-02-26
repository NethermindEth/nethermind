// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

public readonly struct StorageRead(UInt256 key) : IEquatable<StorageRead>, IComparable<StorageRead>
{
    public UInt256 Key { get; init; } = key;

    public int CompareTo(StorageRead other)
        => Key.CompareTo(other.Key);

    public readonly bool Equals(StorageRead other) =>
        Key.Equals(other.Key);

    public override readonly bool Equals(object? obj) =>
        obj is StorageRead other && Equals(other);

    public override readonly int GetHashCode() =>
        Key.GetHashCode();

    public static bool operator ==(StorageRead left, StorageRead right) =>
        left.Equals(right);

    public static bool operator !=(StorageRead left, StorageRead right) =>
        !(left == right);

    public override readonly string ToString() => Key.ToString();
}
