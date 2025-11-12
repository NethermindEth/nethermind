
// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json.Serialization;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.BlockAccessLists;

public class AccountChanges : IEquatable<AccountChanges>
{
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

    private readonly SortedDictionary<byte[], SlotChanges> _storageChanges;
    private readonly SortedSet<StorageRead> _storageReads;
    private readonly SortedList<ushort, BalanceChange> _balanceChanges;
    private readonly SortedList<ushort, NonceChange> _nonceChanges;
    private readonly SortedList<ushort, CodeChange> _codeChanges;

    public AccountChanges()
    {
        Address = Address.Zero;
        _storageChanges = new(Bytes.Comparer);
        _storageReads = [];
        _balanceChanges = [];
        _nonceChanges = [];
        _codeChanges = [];
    }

    public AccountChanges(Address address)
    {
        Address = address;
        _storageChanges = new(Bytes.Comparer);
        _storageReads = [];
        _balanceChanges = [];
        _nonceChanges = [];
        _codeChanges = [];
    }

    public AccountChanges(Address address, SortedDictionary<byte[], SlotChanges> storageChanges, SortedSet<StorageRead> storageReads, SortedList<ushort, BalanceChange> balanceChanges, SortedList<ushort, NonceChange> nonceChanges, SortedList<ushort, CodeChange> codeChanges)
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
    public bool HasStorageChange(byte[] key)
        => _storageChanges.ContainsKey(key);

    public bool TryGetSlotChanges(byte[] key, [NotNullWhen(true)] out SlotChanges? slotChanges)
        => _storageChanges.TryGetValue(key, out slotChanges);
    
    public void ClearSlotChangesIfEmpty(byte[] key)
    {
        if (TryGetSlotChanges(key, out SlotChanges? slotChanges) && slotChanges.Changes.Count == 0)
        {
            _storageChanges.Remove(key);
        }
    }

    public SlotChanges GetOrAddSlotChanges(byte[] key)
    {
        if (!_storageChanges.TryGetValue(key, out SlotChanges? existing))
        {
            SlotChanges slotChanges = new(key);
            _storageChanges.Add(key, slotChanges);
            return slotChanges;
        }
        return existing;
    }

    public void AddStorageRead(byte[] key)
        => _storageReads.Add(new(key));

    public void RemoveStorageRead(byte[] key)
        => _storageReads.Remove(new(key));

    public void SelfDestruct()
    {
        foreach (byte[] key in _storageChanges.Keys)
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

    // public override string? ToString()
    // {
    //     string storageChangesList = string.Join(",\n\t\t\t", [.. StorageChanges.Values.Select(s => s.ToString())]);
    //     string storageChanges = StorageChanges.Count == 0 ? "[] #storage_changes" : $"[ #storage_changes\n\t\t\t{storageChangesList}\n\t\t]";
    //     string storageReadsList = string.Join(",\n\t\t\t", [.. StorageReads.Select(s => s.ToString())]);
    //     string storageReads = StorageReads.Count == 0 ? "[] #storage_reads" : $"[ #storage_reads\n\t\t\t{storageReadsList}\n\t\t]";
    //     string balanceChangesList = string.Join(",\n\t\t\t", [.. BalanceChanges.Values.Select(s => s.ToString())]);
    //     string balanceChanges = BalanceChanges.Count == 0 ? "[] #balance_changes" : $"[ #balance_changes\n\t\t\t{balanceChangesList}\n\t\t]";
    //     string nonceChangesList = string.Join(",\n\t\t\t", [.. NonceChanges.Values.Select(s => s.ToString())]);
    //     string nonceChanges = NonceChanges.Count == 0 ? "[] #nonce_changes" : $"[ #nonce_changes\n\t\t\t{nonceChangesList}\n\t\t]";
    //     string codeChangesList = string.Join(",\n\t\t\t", [.. CodeChanges.Values.Select(s => s.ToString())]);
    //     string codeChanges = CodeChanges.Count == 0 ? "[] #code_changes" : $"[ #code_changes\n\t\t\t{codeChangesList}\n\t\t]";
    //     return $"\t[\n\t\t{Address},\n\t\t{storageChanges},\n\t\t{storageReads},\n\t\t{balanceChanges},\n\t\t{nonceChanges},\n\t\t{codeChanges}\n\t]";
    // }
}
