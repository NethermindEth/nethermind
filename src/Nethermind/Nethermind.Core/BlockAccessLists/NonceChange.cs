// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.BlockAccessLists;

public readonly struct NonceChange(ushort blockAccessIndex, ulong newNonce) : IEquatable<NonceChange>, IIndexedChange
{
    public ushort BlockAccessIndex { get; init; } = blockAccessIndex;
    public ulong NewNonce { get; init; } = newNonce;

    public readonly bool Equals(NonceChange other) =>
        BlockAccessIndex == other.BlockAccessIndex &&
        NewNonce == other.NewNonce;

    public override readonly bool Equals(object? obj) =>
        obj is NonceChange other && Equals(other);

    public override readonly int GetHashCode() =>
        HashCode.Combine(BlockAccessIndex, NewNonce);

    public static bool operator ==(NonceChange left, NonceChange right) =>
        left.Equals(right);

    public static bool operator !=(NonceChange left, NonceChange right) =>
        !(left == right);

    public override readonly string? ToString()
        => $"{BlockAccessIndex}, {NewNonce}";
}
