
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Core.BlockAccessLists;

public class AccountChanges : IEquatable<AccountChanges>
{
    [JsonConverter(typeof(AddressConverter))]
    public Address Address { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IList<SlotChanges> StorageChanges => _storageChanges.Values;

    [JsonIgnore(Condition = JsonIgnoreCondition.Always)]
    public IList<UInt256> ChangedSlots => _storageChanges.Keys;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SortedSet<UInt256> StorageReads => _storageReads;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IList<BalanceChange> BalanceChanges => _balanceChanges.Values;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IList<NonceChange> NonceChanges => _nonceChanges.Values;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IList<CodeChange> CodeChanges => _codeChanges.Values;

    [JsonIgnore]
    public bool ExistedBeforeBlock { get; set; }

    [JsonIgnore]
    public bool EmptyBeforeBlock { get; set; }

    // todo: optimize to use hashmaps where appropriate, separate data structures for tracing and state reading
    private readonly SortedList<UInt256, SlotChanges> _storageChanges;
    private readonly SortedSet<UInt256> _storageReads;
    private readonly SortedList<int, BalanceChange> _balanceChanges;
    private readonly SortedList<int, NonceChange> _nonceChanges;
    private readonly SortedList<int, CodeChange> _codeChanges;

    public AccountChanges()
    {
        Address = Address.Zero;
        _storageChanges = new(GenericComparer.GetOptimized<UInt256>());
        _storageReads = new(GenericComparer.GetOptimized<UInt256>());
        _balanceChanges = new(GenericComparer.GetOptimized<int>());
        _nonceChanges = new(GenericComparer.GetOptimized<int>());
        _codeChanges = new(GenericComparer.GetOptimized<int>());
    }

    public AccountChanges(Address address)
    {
        Address = address;
        _storageChanges = new(GenericComparer.GetOptimized<UInt256>());
        _storageReads = new(GenericComparer.GetOptimized<UInt256>());
        _balanceChanges = new(GenericComparer.GetOptimized<int>());
        _nonceChanges = new(GenericComparer.GetOptimized<int>());
        _codeChanges = new(GenericComparer.GetOptimized<int>());
    }

    public AccountChanges(Address address, SortedList<UInt256, SlotChanges> storageChanges, SortedSet<UInt256> storageReads, SortedList<int, BalanceChange> balanceChanges, SortedList<int, NonceChange> nonceChanges, SortedList<int, CodeChange> codeChanges)
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
        // Only merge reads for slots that don't already have changes in this BAL.
        foreach (UInt256 read in other._storageReads)
        {
            if (!HasStorageChange(read))
            {
                _storageReads.Add(read);
            }
        }

        foreach (KeyValuePair<UInt256, SlotChanges> kv in other._storageChanges)
        {
            if (_storageChanges.TryGetValue(kv.Key, out SlotChanges? existing))
            {
                existing.Merge(kv.Value);
            }
            else
            {
                _storageChanges.Add(kv.Key, kv.Value);
                // When a new change is merged for a slot that previously only had a read,
                // remove the now-redundant read. A change entry supersedes a read.
                RemoveStorageRead(kv.Key);
            }
        }

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
            _storageReads.Add(key);
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

    public bool HasSlotChangesAtIndex(int index)
    {
        foreach (SlotChanges slotChanges in StorageChanges)
        {
            if (slotChanges.Changes.ContainsKey(index))
                return true;
        }
        return false;
    }

    public IEnumerable<SlotChanges> SlotChangesAtIndex(int index)
    {
        foreach (SlotChanges slotChanges in StorageChanges)
        {
            if (slotChanges.Changes.TryGetValue(index, out StorageChange storageChange))
            {
                yield return new(slotChanges.Key, new SortedList<int, StorageChange>(GenericComparer.GetOptimized<int>()) { { index, storageChange } });
            }
        }
    }

    public void AddStorageRead(UInt256 key)
        => _storageReads.Add(key);

    public void RemoveStorageRead(UInt256 key)
        => _storageReads.Remove(key);

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
        => _balanceChanges[balanceChange.Index] = balanceChange;

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
        => _nonceChanges.Add(nonceChange.Index, nonceChange);

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
        => _codeChanges.Add(codeChange.Index, codeChange);

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

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.Append(Address);
        if (BalanceChanges.Count > 0)
            sb.Append($" balance=[{string.Join(", ", BalanceChanges)}]");
        if (NonceChanges.Count > 0)
            sb.Append($" nonce=[{string.Join(", ", NonceChanges)}]");
        if (CodeChanges.Count > 0)
            sb.Append($" code=[{string.Join(", ", CodeChanges)}]");
        if (StorageChanges.Count > 0)
            sb.Append($" storage=[{string.Join(", ", StorageChanges)}]");
        if (StorageReads.Count > 0)
            sb.Append($" reads=[{string.Join(", ", StorageReads)}]");
        return sb.ToString();
    }

    public UInt256? GetNonce(int blockAccessIndex)
    {
        // todo: binary search
        UInt256? lastNonce = null;
        foreach (KeyValuePair<int, NonceChange> change in _nonceChanges)
        {
            if (change.Key >= blockAccessIndex)
            {
                return lastNonce;
            }
            lastNonce = change.Value.Value;
        }
        return lastNonce;
    }

    public UInt256? GetBalance(int blockAccessIndex)
    {
        UInt256? lastBalance = null;
        foreach (KeyValuePair<int, BalanceChange> change in _balanceChanges)
        {
            if (change.Key >= blockAccessIndex)
            {
                return lastBalance;
            }
            lastBalance = change.Value.Value;
        }
        return lastBalance;
    }

    public byte[] GetCode(int blockAccessIndex)
    {
        GetCodeChange(blockAccessIndex, out CodeChange? codeChange);
        return codeChange!.Value.Code;
    }

    public ValueHash256 GetCodeHash(int blockAccessIndex)
    {
        GetCodeChange(blockAccessIndex, out CodeChange? codeChange);
        return codeChange!.Value.CodeHash;
    }

    public HashSet<UInt256> GetAllSlots(int blockAccessIndex)
    {
        HashSet<UInt256> slots = [];
        foreach (SlotChanges slotChange in _storageChanges.Values)
        {
            UInt256 lastValue = 0;
            foreach (StorageChange storageChange in slotChange.Changes.Values)
            {
                if (storageChange.Index > blockAccessIndex)
                {
                    if (lastValue != 0)
                    {
                        slots.Add(slotChange.Key);
                    }
                    break;
                }
                lastValue = storageChange.Value;
            }
        }
        return slots;
    }

    // check if account exists at start of tx at index
    public bool AccountExists(int blockAccessIndex)
    {
        if (ExistedBeforeBlock)
        {
            // cannot be destroyed if already exists
            return true;
        }

        if (blockAccessIndex == 0)
        {
            return ExistedBeforeBlock;
        }

        // When the account did not exist before the block, prestate entries at index -1
        // are just default placeholders — they do not indicate account creation.
        // Only changes at index >= 0 (i.e., from actual transactions) prove the account
        // was created during this block.
        foreach (KeyValuePair<int, NonceChange> change in _nonceChanges)
        {
            if (change.Key >= 0 && change.Key < blockAccessIndex)
            {
                return true;
            }
            else if (change.Key >= blockAccessIndex)
            {
                break;
            }
        }

        foreach (KeyValuePair<int, BalanceChange> change in _balanceChanges)
        {
            if (change.Key >= 0 && change.Key < blockAccessIndex)
            {
                return true;
            }
            else if (change.Key >= blockAccessIndex)
            {
                break;
            }
        }

        return false;
    }

    // assumes prestate not loaded
    public void CheckWasChanged() => _wasChanged = _balanceChanges.Count > 0 || _nonceChanges.Count > 0 || _codeChanges.Count > 0 || _storageChanges.Count > 0;

    [JsonIgnore]
    public bool AccountChanged => _wasChanged;
    private bool _wasChanged = false;

    private void GetCodeChange(int blockAccessIndex, out CodeChange? codeChange)
    {
        codeChange = null;
        foreach (KeyValuePair<int, CodeChange> change in _codeChanges)
        {
            if (change.Key >= blockAccessIndex)
            {
                return;
            }
            codeChange = change.Value;
        }
    }

    private static bool PopChange<T>(SortedList<int, T> changes, int index, [NotNullWhen(true)] out T? change) where T : IIndexedChange
    {
        change = default;

        if (changes.Count == 0)
            return false;

        int c = changes.Count;
        KeyValuePair<int, T> lastChange = new(changes.Keys[c - 1], changes.Values[c - 1]);

        if (lastChange.Key == index)
        {
            change = lastChange.Value;
            changes.RemoveAt(c - 1);
            return true;
        }

        return false;
    }
}
