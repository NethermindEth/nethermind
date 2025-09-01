// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Core.BlockAccessLists;

public readonly struct BlockAccessList : IEquatable<BlockAccessList>
{
    public SortedDictionary<Address, AccountChanges> AccountChanges { get; init; }

    public BlockAccessList()
    {
        AccountChanges = [];
    }

    public BlockAccessList(SortedDictionary<Address, AccountChanges> accountChanges)
    {
        AccountChanges = accountChanges;
    }

    public readonly bool Equals(BlockAccessList other) =>
        AccountChanges.Count == other.AccountChanges.Count;

    public override readonly bool Equals(object? obj) =>
        obj is BlockAccessList other && Equals(other);

    public override readonly int GetHashCode() =>
        AccountChanges.Count.GetHashCode();

    public static bool operator ==(BlockAccessList left, BlockAccessList right) =>
        left.Equals(right);

    public static bool operator !=(BlockAccessList left, BlockAccessList right) =>
        !(left == right);
}
