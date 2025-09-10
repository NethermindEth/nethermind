// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

public readonly struct BalanceChange(ushort blockAccessIndex, UInt256 postBalance) : IEquatable<BalanceChange>
{
    public ushort BlockAccessIndex { get; init; } = blockAccessIndex;
    public UInt256 PostBalance { get; init; } = postBalance;

    public readonly bool Equals(BalanceChange other) =>
        BlockAccessIndex == other.BlockAccessIndex &&
        PostBalance == other.PostBalance;

    public override readonly bool Equals(object? obj) =>
        obj is BalanceChange other && Equals(other);

    public override readonly int GetHashCode() =>
        HashCode.Combine(BlockAccessIndex, PostBalance);

    public static bool operator ==(BalanceChange left, BalanceChange right) =>
        left.Equals(right);

    public static bool operator !=(BalanceChange left, BalanceChange right) =>
        !(left == right);
}
