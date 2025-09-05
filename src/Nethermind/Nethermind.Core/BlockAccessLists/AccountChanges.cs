
// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.BlockAccessLists;

public readonly struct AccountChanges : IEquatable<AccountChanges>
{
    public Address Address { get; init; }
    public SortedDictionary<byte[], SlotChanges> StorageChanges { get; init; }
    public SortedSet<StorageRead> StorageReads { get; init; }
    public SortedList<ushort, BalanceChange> BalanceChanges { get; init; }
    public SortedList<ushort, NonceChange> NonceChanges { get; init; }
    public SortedList<ushort, CodeChange> CodeChanges { get; init; }

    public AccountChanges(Address address)
    {
        Address = address;
        StorageChanges = new(Bytes.Comparer);
        StorageReads = [];
        BalanceChanges = [];
        NonceChanges = [];
        CodeChanges = [];
    }

    public AccountChanges(Address address, SortedDictionary<byte[], SlotChanges> storageChanges, SortedSet<StorageRead> storageReads, SortedList<ushort, BalanceChange> balanceChanges, SortedList<ushort, NonceChange> nonceChanges, SortedList<ushort, CodeChange> codeChanges)
    {
        Address = address;
        StorageChanges = storageChanges;
        StorageReads = storageReads;
        BalanceChanges = balanceChanges;
        NonceChanges = nonceChanges;
        CodeChanges = codeChanges;
    }

    public readonly bool Equals(AccountChanges other) =>
        Address == other.Address &&
        StorageChanges.Values.SequenceEqual(other.StorageChanges.Values) &&
        StorageReads.SequenceEqual(other.StorageReads) &&
        BalanceChanges.SequenceEqual(other.BalanceChanges) &&
        NonceChanges.SequenceEqual(other.NonceChanges) &&
        CodeChanges.SequenceEqual(other.CodeChanges);

    public override readonly bool Equals(object? obj) =>
        obj is AccountChanges other && Equals(other);

    public override readonly int GetHashCode() =>
        Address.GetHashCode();

    public static bool operator ==(AccountChanges left, AccountChanges right) =>
        left.Equals(right);

    public static bool operator !=(AccountChanges left, AccountChanges right) =>
        !(left == right);
}
