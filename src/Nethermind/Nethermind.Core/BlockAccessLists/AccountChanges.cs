
// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json.Serialization;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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
    public EnumerableWithCount<BalanceChange> BalanceChanges => new(_balanceChanges.Values.Where(c => c.BlockAccessIndex != -1), _balanceChanges.Count);

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public EnumerableWithCount<NonceChange> NonceChanges => new(_nonceChanges.Values.Where(c => c.BlockAccessIndex != -1), _nonceChanges.Count);

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public EnumerableWithCount<CodeChange> CodeChanges => new(_codeChanges.Values.Where(c => c.BlockAccessIndex != -1), _codeChanges.Count);

    [JsonIgnore]
    public ValueHash256 CodeHash { get => _codeHash; set => _codeHash = value; }

    private readonly SortedDictionary<byte[], SlotChanges> _storageChanges;
    private readonly SortedSet<StorageRead> _storageReads;
    private readonly SortedList<int, BalanceChange> _balanceChanges;
    private readonly SortedList<int, NonceChange> _nonceChanges;
    private readonly SortedList<int, CodeChange> _codeChanges;
    private bool _isDestroyed = false;
    private ValueHash256 _codeHash;
    // private bool _existedPreBlock = false;
    // fetch whole account in prestate load?

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

    public AccountChanges(Address address, SortedDictionary<byte[], SlotChanges> storageChanges, SortedSet<StorageRead> storageReads, SortedList<int, BalanceChange> balanceChanges, SortedList<int, NonceChange> nonceChanges, SortedList<int, CodeChange> codeChanges)
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
    
    public void ClearEmptySlotChangesAndAddRead(byte[] key)
    {
        if (TryGetSlotChanges(key, out SlotChanges? slotChanges) && slotChanges.Changes.Count == 0)
        {
            _storageChanges.Remove(key);
            _storageReads.Add(new(key));
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
        _isDestroyed = true;
    }

    public void CreateAccount()
    {
        _isDestroyed = false;
    }

    public void AddBalanceChange(BalanceChange balanceChange)
        => _balanceChanges.Add(balanceChange.BlockAccessIndex, balanceChange);

    public bool PopBalanceChange(int index, [NotNullWhen(true)] out BalanceChange? balanceChange)
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

    public bool PopNonceChange(int index, [NotNullWhen(true)] out NonceChange? nonceChange)
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

    public bool PopCodeChange(int index, [NotNullWhen(true)] out CodeChange? codeChange)
    {
        codeChange = null;
        if (PopChange(_codeChanges, index, out CodeChange change))
        {
            codeChange = change;
            return true;
        }
        return false;
    }

    private static bool PopChange<T>(SortedList<int, T> changes, int index, [NotNullWhen(true)] out T? change) where T : IIndexedChange
    {
        change = default;

        if (changes.Count == 0)
            return false;

        KeyValuePair<int, T> lastChange = changes.Last();

        if (lastChange.Key == index)
        {
            changes.RemoveAt(changes.Count - 1);
            change = lastChange.Value;
            return true;
        }

        return false;
    }

    public UInt256 GetNonce(int blockAccessIndex)
    {
        return 0;
    }

    public UInt256 GetBalance(int blockAccessIndex)
    {
        return 0;
    }

    public byte[] GetCode(int blockAccessIndex)
    {
        return [];
    }

    public bool IsStorageEmpty(int blockAccessIndex)
    {
        return false;
    }

    public HashSet<byte[]> GetAllSlots(int blockAccessIndex)
    {
        return [];
    }

    // add to codechanges when generating?
    public ValueHash256 GetCodeHash(int blockAccessIndex)
    {
        return new();
    }

    public bool AccountExists(int blockAccessIndex)
        => !_isDestroyed; // check through BAL
}
