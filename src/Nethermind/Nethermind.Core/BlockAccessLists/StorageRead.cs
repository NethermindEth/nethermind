// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.BlockAccessLists;

public readonly struct StorageRead(Bytes32 key) : IEquatable<StorageRead>, IComparable<StorageRead>
{
    public Bytes32 Key { get; init; } = key;

    public int CompareTo(StorageRead other)
        => Bytes.BytesComparer.Compare(Key.Unwrap(), other.Key.Unwrap());

    public readonly bool Equals(StorageRead other) =>
        Key.Unwrap().SequenceEqual(other.Key.Unwrap());

    public override readonly bool Equals(object? obj) =>
        obj is StorageRead other && Equals(other);

    public override readonly int GetHashCode() =>
        Key.GetHashCode();

    public static bool operator ==(StorageRead left, StorageRead right) =>
        left.Equals(right);

    public static bool operator !=(StorageRead left, StorageRead right) =>
        !(left == right);

    public override readonly string? ToString()
        => $"0x{Bytes.ToHexString(Key.Unwrap())}";
}
