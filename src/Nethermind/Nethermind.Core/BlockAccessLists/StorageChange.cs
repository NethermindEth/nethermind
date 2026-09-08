// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

public readonly struct StorageChange(uint index, EvmWord value) : IEquatable<StorageChange>, IIndexedChange
{
    public readonly uint Index { get; } = index;

    /// <summary>
    /// Storage value as 32 big-endian bytes. Wire-shape; matches RLP encoding directly.
    /// Construct from a <see cref="UInt256"/> via the convenience ctor below.
    /// </summary>
    public readonly EvmWord Value = value;

    /// <summary>
    /// Convenience ctor that flips a <see cref="UInt256"/> (host-endian) into the 32-byte BE wire form.
    /// </summary>
    public StorageChange(uint index, in UInt256 value)
        : this(index, Unsafe.As<UInt256, EvmWord>(ref Unsafe.AsRef(in value)).ByteSwap())
    {
    }

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
