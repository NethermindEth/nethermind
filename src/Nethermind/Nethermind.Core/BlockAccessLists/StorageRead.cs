// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json.Serialization;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Core.BlockAccessLists;

public readonly struct StorageRead(UInt256 key) : IEquatable<StorageRead>, IComparable<StorageRead>
{
    [JsonConverter(typeof(ByteArrayConverter))]
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
}
