
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

    public AccountChanges()
    {
        Address = Address.Zero;
        StorageChanges = new(Bytes.Comparer);
        StorageReads = [];
        BalanceChanges = [];
        NonceChanges = [];
        CodeChanges = [];
    }

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

    public override readonly string? ToString()
    {
        string storageChangesList = string.Join(",\n\t\t\t", [.. StorageChanges.Values.Select(s => s.ToString())]);
        string storageChanges = StorageChanges.Count == 0 ? "[] #storage_changes" : $"[ #storage_changes\n\t\t\t{storageChangesList}\n\t\t]";
        string storageReadsList = string.Join(",\n\t\t\t", [.. StorageReads.Select(s => s.ToString())]);
        string storageReads = StorageReads.Count == 0 ? "[] #storage_reads" : $"[ #storage_reads\n\t\t\t{storageReadsList}\n\t\t]";
        string balanceChangesList = string.Join(",\n\t\t\t", [.. BalanceChanges.Values.Select(s => s.ToString())]);
        string balanceChanges = BalanceChanges.Count == 0 ? "[] #balance_changes" : $"[ #balance_changes\n\t\t\t{balanceChangesList}\n\t\t]";
        string nonceChangesList = string.Join(",\n\t\t\t", [.. NonceChanges.Values.Select(s => s.ToString())]);
        string nonceChanges = NonceChanges.Count == 0 ? "[] #nonce_changes" : $"[ #nonce_changes\n\t\t\t{nonceChangesList}\n\t\t]";
        string codeChangesList = string.Join(",\n\t\t\t", [.. CodeChanges.Values.Select(s => s.ToString())]);
        string codeChanges = CodeChanges.Count == 0 ? "[] #code_changes" : $"[ #code_changes\n\t\t\t{codeChangesList}\n\t\t]";
        return $"\t[\n\t\t{Address},\n\t\t{storageChanges},\n\t\t{storageReads},\n\t\t{balanceChanges},\n\t\t{nonceChanges},\n\t\t{codeChanges}\n\t]";
    }
}
