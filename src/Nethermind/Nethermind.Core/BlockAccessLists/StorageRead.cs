// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;

namespace Nethermind.Core.BlockAccessLists;

public readonly struct StorageRead(Bytes32 key) : IEquatable<StorageRead>
{
    public Bytes32 Key { get; init; } = key;

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
}
