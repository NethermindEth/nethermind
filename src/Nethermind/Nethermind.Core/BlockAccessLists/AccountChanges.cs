
// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json.Serialization;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Core.BlockAccessLists;

public class AccountChanges : IEquatable<AccountChanges>
{
    [JsonConverter(typeof(AddressConverter))]
    public Address Address { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IEnumerable<SlotChanges> StorageChanges => _storageChanges.Values;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IEnumerable<StorageRead> StorageReads => _storageReads;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IEnumerable<BalanceChange> BalanceChanges => _balanceChanges.Values;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IEnumerable<NonceChange> NonceChanges => _nonceChanges.Values;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IEnumerable<CodeChange> CodeChanges => _codeChanges.Values;

    private readonly SortedDictionary<UInt256, SlotChanges> _storageChanges;
    private readonly SortedSet<StorageRead> _storageReads;
    private readonly SortedList<ushort, BalanceChange> _balanceChanges;
    private readonly SortedList<ushort, NonceChange> _nonceChanges;
    private readonly SortedList<ushort, CodeChange> _codeChanges;

    public AccountChanges()
    {
        Address = Address.Zero;
        _storageChanges = [];
        _storageReads = [];
        _balanceChanges = [];
        _nonceChanges = [];
        _codeChanges = [];
    }

    public AccountChanges(Address address)
    {
        Address = address;
        _storageChanges = [];
        _storageReads = [];
        _balanceChanges = [];
        _nonceChanges = [];
        _codeChanges = [];
    }

    public AccountChanges(Address address, SortedDictionary<UInt256, SlotChanges> storageChanges, SortedSet<StorageRead> storageReads, SortedList<ushort, BalanceChange> balanceChanges, SortedList<ushort, NonceChange> nonceChanges, SortedList<ushort, CodeChange> codeChanges)
    {
        Address = address;
        _storageChanges = storageChanges;
        _storageReads = storageReads;
        _balanceChanges = balanceChanges;
        _nonceChanges = nonceChanges;
        _codeChanges = codeChanges;
    }

    public bool Equals(AccountChanges? other) =>
        other is not null &&
        Address == other.Address &&
        StorageChanges.SequenceEqual(other.StorageChanges) &&
        StorageReads.SequenceEqual(other.StorageReads) &&
        BalanceChanges.SequenceEqual(other.BalanceChanges) &&
        NonceChanges.SequenceEqual(other.NonceChanges) &&
        CodeChanges.SequenceEqual(other.CodeChanges);

    public override bool Equals(object? obj) =>
        obj is AccountChanges other && Equals(other);
    public override int GetHashCode() =>
        Address.GetHashCode();

    public static bool operator ==(AccountChanges left, AccountChanges right) =>
        left.Equals(right);

    public static bool operator !=(AccountChanges left, AccountChanges right) =>
        !(left == right);

    // n.b. implies that length of changes is zero
    public bool HasStorageChange(UInt256 key)
        => _storageChanges.ContainsKey(key);

    public bool TryGetSlotChanges(UInt256 key, [NotNullWhen(true)] out SlotChanges? slotChanges)
        => _storageChanges.TryGetValue(key, out slotChanges);

    public void ClearEmptySlotChangesAndAddRead(UInt256 key)
    {
        if (TryGetSlotChanges(key, out SlotChanges? slotChanges) && slotChanges.Changes.Count == 0)
        {
            _storageChanges.Remove(key);
            _storageReads.Add(new(key));
        }
    }

    public SlotChanges GetOrAddSlotChanges(UInt256 key)
    {
        if (!_storageChanges.TryGetValue(key, out SlotChanges? existing))
        {
            SlotChanges slotChanges = new(key);
            _storageChanges.Add(key, slotChanges);
            return slotChanges;
        }
        return existing;
    }

    public void AddStorageRead(UInt256 key)
        => _storageReads.Add(new(key));

    public void RemoveStorageRead(UInt256 key)
        => _storageReads.Remove(new(key));

    public void SelfDestruct()
    {
        foreach (UInt256 key in _storageChanges.Keys)
        {
            AddStorageRead(key);
        }

        _storageChanges.Clear();
        _nonceChanges.Clear();
        _codeChanges.Clear();
    }

    public void AddBalanceChange(BalanceChange balanceChange)
        => _balanceChanges.Add(balanceChange.BlockAccessIndex, balanceChange);

    public bool PopBalanceChange(ushort index, [NotNullWhen(true)] out BalanceChange? balanceChange)
    {
        balanceChange = null;
        if (PopChange(_balanceChanges, index, out BalanceChange change))
        {
            balanceChange = change;
            return true;
        }
        return false;
    }

    public void AddNonceChange(NonceChange nonceChange)
        => _nonceChanges.Add(nonceChange.BlockAccessIndex, nonceChange);

    public bool PopNonceChange(ushort index, [NotNullWhen(true)] out NonceChange? nonceChange)
    {
        nonceChange = null;
        if (PopChange(_nonceChanges, index, out NonceChange change))
        {
            nonceChange = change;
            return true;
        }
        return false;
    }

    public void AddCodeChange(CodeChange codeChange)
        => _codeChanges.Add(codeChange.BlockAccessIndex, codeChange);

    public bool PopCodeChange(ushort index, [NotNullWhen(true)] out CodeChange? codeChange)
    {
        codeChange = null;
        if (PopChange(_codeChanges, index, out CodeChange change))
        {
            codeChange = change;
            return true;
        }
        return false;
    }

    private static bool PopChange<T>(SortedList<ushort, T> changes, ushort index, [NotNullWhen(true)] out T? change) where T : IIndexedChange
    {
        change = default;

        if (changes.Count == 0)
            return false;

        KeyValuePair<ushort, T> lastChange = changes.Last();

        if (lastChange.Key == index)
        {
            changes.RemoveAt(changes.Count - 1);
            change = lastChange.Value;
            return true;
        }

        return false;
    }
}
