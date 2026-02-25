
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Text.Json.Serialization;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Core.BlockAccessLists;

public readonly struct StorageChange(ushort blockAccessIndex, UInt256 newValue) : IEquatable<StorageChange>, IIndexedChange
{
    public ushort BlockAccessIndex { get; init; } = blockAccessIndex;
    [JsonConverter(typeof(UInt256Converter))]
    public UInt256 NewValue { get; init; } = newValue;

    public readonly bool Equals(StorageChange other) =>
        BlockAccessIndex == other.BlockAccessIndex &&
        NewValue.Equals(other.NewValue);

    public override readonly bool Equals(object? obj) =>
        obj is StorageChange other && Equals(other);

    public override readonly int GetHashCode() =>
        HashCode.Combine(BlockAccessIndex, NewValue);

    public static bool operator ==(StorageChange left, StorageChange right) =>
        left.Equals(right);

    public static bool operator !=(StorageChange left, StorageChange right) =>
        !(left == right);
}
