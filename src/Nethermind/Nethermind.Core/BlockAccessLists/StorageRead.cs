// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Text.Json.Serialization;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Json;

namespace Nethermind.Core.BlockAccessLists;

public readonly struct StorageRead(byte[] key) : IEquatable<StorageRead>, IComparable<StorageRead>
{
    [JsonConverter(typeof(ByteArrayConverter))]
    public byte[] Key { get; init; } = key;

    public int CompareTo(StorageRead other)
        => Bytes.BytesComparer.Compare(Key, other.Key);

    public readonly bool Equals(StorageRead other) =>
        Key.SequenceEqual(other.Key);

    public override readonly bool Equals(object? obj) =>
        obj is StorageRead other && Equals(other);

    public override readonly int GetHashCode() =>
        Key.GetHashCode();

    public static bool operator ==(StorageRead left, StorageRead right) =>
        left.Equals(right);

    public static bool operator !=(StorageRead left, StorageRead right) =>
        !(left == right);
}
