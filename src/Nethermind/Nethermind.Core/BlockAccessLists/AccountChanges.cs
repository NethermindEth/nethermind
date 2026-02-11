
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
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
    public Address Address { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IList<SlotChanges> StorageChanges => _storageChanges.Values;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyCollection<StorageRead> StorageReads => _storageReads;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IList<BalanceChange> BalanceChanges => _balanceChanges.Values;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IList<NonceChange> NonceChanges => _nonceChanges.Values;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IList<CodeChange> CodeChanges => _codeChanges.Values;

    [JsonIgnore]
    public bool ExistedBeforeBlock { get; set; }

    private readonly SortedList<UInt256, SlotChanges> _storageChanges;
    private readonly SortedSet<StorageRead> _storageReads;
    private readonly SortedList<int, BalanceChange> _balanceChanges;
    private readonly SortedList<int, NonceChange> _nonceChanges;
    private readonly SortedList<int, CodeChange> _codeChanges;
    // private bool _isDestroyed = false;
    // private ValueHash256 _codeHash;
    // private bool _existedPreBlock = false;

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

    public AccountChanges(Address address, SortedList<UInt256, SlotChanges> storageChanges, SortedSet<StorageRead> storageReads, SortedList<int, BalanceChange> balanceChanges, SortedList<int, NonceChange> nonceChanges, SortedList<int, CodeChange> codeChanges)
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

    public void Merge(AccountChanges other)
    {
        _storageChanges.AddRange(other._storageChanges);
        _storageReads.AddRange(other._storageReads);
        _balanceChanges.AddRange(other._balanceChanges);
        _nonceChanges.AddRange(other._nonceChanges);
        _codeChanges.AddRange(other._codeChanges);
    }

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

    public IEnumerable<SlotChanges> SlotChangesAtIndex(int index)
    {
        foreach (SlotChanges slotChanges in StorageChanges)
        {
            if (slotChanges.Changes.TryGetValue(index, out StorageChange storageChange))
            {
                yield return new(slotChanges.Slot, new SortedList<int, StorageChange>() { { index, storageChange } });
            }
        }
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
        // _isDestroyed = true;
    }

    // public void CreateAccount()
    // {
    //     _isDestroyed = false;
    // }

    public void AddBalanceChange(BalanceChange balanceChange)
        => _balanceChanges[balanceChange.BlockAccessIndex] = balanceChange;

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

    public BalanceChange? BalanceChangeAtIndex(ushort index)
        => _balanceChanges.TryGetValue(index, out BalanceChange balanceChange) ? balanceChange : null;

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

    public NonceChange? NonceChangeAtIndex(ushort index)
        => _nonceChanges.TryGetValue(index, out NonceChange nonceChange) ? nonceChange : null;

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

    public CodeChange? CodeChangeAtIndex(int index)
        => _codeChanges.TryGetValue(index, out CodeChange codeChange) ? codeChange : null;

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
        UInt256 lastNonce = UInt256.MaxValue;
        foreach (KeyValuePair<int, NonceChange> change in _nonceChanges)
        {
            if (change.Key >= blockAccessIndex)
            {
                return lastNonce;
            }
            lastNonce = change.Value.NewNonce;
        }
        return lastNonce;
    }

    public UInt256 GetBalance(int blockAccessIndex)
    {
        UInt256 lastBalance = UInt256.MaxValue;
        foreach (KeyValuePair<int, BalanceChange> change in _balanceChanges)
        {
            if (change.Key >= blockAccessIndex)
            {
                return lastBalance;
            }
            lastBalance = change.Value.PostBalance;
        }
        return lastBalance;
    }

    public byte[] GetCode(int blockAccessIndex)
    {
        byte[] lastCode = [];
        foreach (KeyValuePair<int, CodeChange> change in _codeChanges)
        {
            if (change.Key >= blockAccessIndex)
            {
                return lastCode;
            }
            lastCode = change.Value.NewCode;
        }
        return lastCode;
    }

    public HashSet<UInt256> GetAllSlots(int blockAccessIndex)
    {
        HashSet<UInt256> slots = [];
        foreach (SlotChanges slotChange in _storageChanges.Values)
        {
            UInt256 lastValue = 0;
            foreach (StorageChange storageChange in slotChange.Changes.Values)
            {
                if (storageChange.BlockAccessIndex > blockAccessIndex)
                {
                    if (lastValue != 0)
                    {
                        slots.Add(slotChange.Slot);
                    }
                    break;
                }
                lastValue = storageChange.NewValue;
            }
        }
        return slots;
    }

    // add to codechanges when generating?
    public ValueHash256 GetCodeHash(int blockAccessIndex) =>
        ValueKeccak.Compute(GetCode(blockAccessIndex));

    // check if account exists at start of tx at index
    public bool AccountExists(int blockAccessIndex)
    {
        if (ExistedBeforeBlock)
        {
            // cannot be destroyed if already exists
            return true;
        }

        foreach (KeyValuePair<int, NonceChange> change in _nonceChanges)
        {
            if (change.Key < blockAccessIndex)
            {
                return true;
            }
            else
            {
                break;
            }
        }

        foreach (KeyValuePair<int, BalanceChange> change in _balanceChanges)
        {
            if (change.Key < blockAccessIndex)
            {
                return true;
            }
            else
            {
                break;
            }
        }

        return false;
    }

    // assumes prestate not loaded
    public void CheckWasChanged()
    {
        _wasChanged = _balanceChanges.Count > 0 || _nonceChanges.Count > 0 || _codeChanges.Count > 0 || _storageChanges.Count > 0;
    }

    [JsonIgnore]
    public bool AccountChanged => _wasChanged;
    private bool _wasChanged = false;
}
