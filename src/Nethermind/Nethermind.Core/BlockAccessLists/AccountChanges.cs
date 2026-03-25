
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

[assembly: InternalsVisibleTo("Nethermind.Merge.Plugin.Test")]

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

    // todo: optimize to use hashmaps where appropriate, separate data structures for tracing and state reading
    private readonly SortedList<UInt256, SlotChanges> _storageChanges;
    private readonly SortedSet<StorageRead> _storageReads;
    private readonly SortedList<int, BalanceChange> _balanceChanges;
    private readonly SortedList<int, NonceChange> _nonceChanges;
    private readonly SortedList<int, CodeChange> _codeChanges;

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

    public BalanceChange? BalanceChangeAtIndex(ushort index)
        => _balanceChanges.TryGetValue(index, out BalanceChange balanceChange) ? balanceChange : null;

    public NonceChange? NonceChangeAtIndex(ushort index)
        => _nonceChanges.TryGetValue(index, out NonceChange nonceChange) ? nonceChange : null;

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

    public UInt256 GetNonce(int blockAccessIndex)
    {
        // todo: binary search
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

        if (blockAccessIndex == 0)
        {
            return ExistedBeforeBlock;
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

    public void LoadPreBlockState(UInt256 balance, ulong nonce, byte[] code)
    {
        _balanceChanges.Add(-1, new(-1, balance));
        _nonceChanges.Add(-1, new(-1, nonce));
        _codeChanges.Add(-1, new(-1, code));
    }

    public void LoadPreBlockState(StorageCell storageCell, ReadOnlySpan<byte> value)
        => GetOrAddSlotChanges(storageCell.Index).AddStorageChange(new(-1, new(value, true)));

    // assumes prestate not loaded
    public void CheckWasChanged()
    {
        _wasChanged = _balanceChanges.Count > 0 || _nonceChanges.Count > 0 || _codeChanges.Count > 0 || _storageChanges.Count > 0;
    }

    public void Merge(GeneratingAccountChanges other)
    {
        foreach (SlotChanges slotChanges in other.StorageChanges)
        {
            if (_storageChanges.TryGetValue(slotChanges.Slot, out SlotChanges? existing))
            {
                existing.Merge(slotChanges);
            }
            else
            {
                _storageChanges.Add(slotChanges.Slot, slotChanges);
            }
        }

        foreach (StorageRead storageRead in other.StorageReads)
        {
            _storageReads.Add(storageRead);
        }

        if (other.BalanceChange is not null)
        {
            _balanceChanges.Add(other.BalanceChange.Value.BlockAccessIndex, other.BalanceChange.Value);
        }

        if (other.NonceChange is not null)
        {
            _nonceChanges.Add(other.NonceChange.Value.BlockAccessIndex, other.NonceChange.Value);
        }

        if (other.CodeChange is not null)
        {
            _codeChanges.Add(other.CodeChange.Value.BlockAccessIndex, other.CodeChange.Value);
        }
    }

    [JsonIgnore]
    public bool AccountChanged => _wasChanged;
    private bool _wasChanged = false;

    internal void AddBalanceChange(BalanceChange balanceChange)
        => _balanceChanges.Add(balanceChange.BlockAccessIndex, balanceChange);

    internal void AddNonceChange(NonceChange nonceChange)
        => _nonceChanges.Add(nonceChange.BlockAccessIndex, nonceChange);

    internal void AddCodeChange(CodeChange codeChange)
        => _codeChanges.Add(codeChange.BlockAccessIndex, codeChange);

    internal void AddStorageChange(UInt256 key, StorageChange storageChange)
        => GetOrAddSlotChanges(key).AddStorageChange(storageChange);

    internal void AddStorageRead(StorageRead storageRead)
        => _storageReads.Add(storageRead);

    private SlotChanges GetOrAddSlotChanges(UInt256 key)
    {
        if (!_storageChanges.TryGetValue(key, out SlotChanges? existing))
        {
            SlotChanges slotChanges = new(key);
            _storageChanges.Add(key, slotChanges);
            return slotChanges;
        }
        return existing;
    }
}
