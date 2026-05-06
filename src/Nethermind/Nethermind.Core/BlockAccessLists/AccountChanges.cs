
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
    private readonly SortedList<uint, BalanceChange> _balanceChanges;
    private readonly SortedList<uint, NonceChange> _nonceChanges;
    private readonly SortedList<uint, CodeChange> _codeChanges;

    public AccountChanges()
    {
        Address = Address.Zero;
        _storageChanges = new(GenericComparer.GetOptimized<UInt256>());
        _storageReads = new(GenericComparer.GetOptimized<UInt256>());
        _balanceChanges = new(PrestateAwareIndexComparer.Instance);
        _nonceChanges = new(PrestateAwareIndexComparer.Instance);
        _codeChanges = new(PrestateAwareIndexComparer.Instance);
    }

    public AccountChanges(Address address)
    {
        Address = address;
        _storageChanges = new(GenericComparer.GetOptimized<UInt256>());
        _storageReads = new(GenericComparer.GetOptimized<UInt256>());
        _balanceChanges = new(PrestateAwareIndexComparer.Instance);
        _nonceChanges = new(PrestateAwareIndexComparer.Instance);
        _codeChanges = new(PrestateAwareIndexComparer.Instance);
    }

    public AccountChanges(Address address, SortedList<UInt256, SlotChanges> storageChanges, SortedSet<UInt256> storageReads, SortedList<uint, BalanceChange> balanceChanges, SortedList<uint, NonceChange> nonceChanges, SortedList<uint, CodeChange> codeChanges)
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
        ListEquals(StorageChanges, other.StorageChanges) &&
        SetEquals(StorageReads, other.StorageReads) &&
        ListEquals(BalanceChanges, other.BalanceChanges) &&
        ListEquals(NonceChanges, other.NonceChanges) &&
        ListEquals(CodeChanges, other.CodeChanges);

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

    public bool HasSlotChangesAtIndex(uint index)
    {
        foreach (SlotChanges slotChanges in StorageChanges)
        {
            if (slotChanges.Changes.ContainsKey(index))
                return true;
        }
        return false;
    }

    public IEnumerable<(SlotChanges SlotChanges, StorageChange StorageChange)> SlotChangePairsAtIndex(uint index)
    {
        foreach (SlotChanges slotChanges in StorageChanges)
        {
            if (slotChanges.Changes.TryGetValue(index, out StorageChange storageChange))
            {
                yield return (slotChanges, storageChange);
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

    internal bool TryPopBalanceChange(uint index, out BalanceChange balanceChange)
        => PopChange(_balanceChanges, index, out balanceChange);

    public BalanceChange? BalanceChangeAtIndex(uint index)
        => _balanceChanges.TryGetValue(index, out BalanceChange balanceChange) ? balanceChange : null;

    public void AddNonceChange(NonceChange nonceChange)
        => _nonceChanges.Add(nonceChange.Index, nonceChange);

    internal bool TryPopNonceChange(uint index, out NonceChange nonceChange)
        => PopChange(_nonceChanges, index, out nonceChange);

    public NonceChange? NonceChangeAtIndex(uint index)
        => _nonceChanges.TryGetValue(index, out NonceChange nonceChange) ? nonceChange : null;

    public void AddCodeChange(CodeChange codeChange)
        => _codeChanges.Add(codeChange.Index, codeChange);

    internal bool TryPopCodeChange(uint index, out CodeChange codeChange)
        => PopChange(_codeChanges, index, out codeChange);

    public CodeChange? CodeChangeAtIndex(uint index)
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

    public UInt256? GetNonce(uint blockAccessIndex)
    {
        int changeIndex = FindFirstRealIndexAtOrAfter(_nonceChanges.Keys, blockAccessIndex);
        return changeIndex == 0 ? null : _nonceChanges.Values[changeIndex - 1].Value;
    }

    public UInt256? GetBalance(uint blockAccessIndex)
    {
        int changeIndex = FindFirstRealIndexAtOrAfter(_balanceChanges.Keys, blockAccessIndex);
        return changeIndex == 0 ? null : _balanceChanges.Values[changeIndex - 1].Value;
    }

    public byte[] GetCode(uint blockAccessIndex)
    {
        GetCodeChange(blockAccessIndex, out CodeChange? codeChange);
        if (codeChange is null)
        {
            ThrowMissingCodeChange(blockAccessIndex);
        }

        return codeChange.Value.Code;
    }

    public ValueHash256 GetCodeHash(uint blockAccessIndex)
    {
        GetCodeChange(blockAccessIndex, out CodeChange? codeChange);
        if (codeChange is null)
        {
            ThrowMissingCodeChange(blockAccessIndex);
        }

        return codeChange.Value.CodeHash;
    }

    public HashSet<UInt256> GetAllSlots(uint blockAccessIndex)
    {
        HashSet<UInt256> slots = [];
        foreach (SlotChanges slotChange in _storageChanges.Values)
        {
            UInt256 lastValue = 0;
            foreach (StorageChange storageChange in slotChange.Changes.Values)
            {
                if (storageChange.Index != Eip7928Constants.PrestateIndex && storageChange.Index > blockAccessIndex)
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
    public bool AccountExists(uint blockAccessIndex)
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

        foreach (KeyValuePair<uint, NonceChange> change in _nonceChanges)
        {
            if (change.Key == Eip7928Constants.PrestateIndex)
            {
                continue;
            }

            if (change.Key < blockAccessIndex)
            {
                return true;
            }
            else if (change.Key >= blockAccessIndex)
            {
                break;
            }
        }

        foreach (KeyValuePair<uint, BalanceChange> change in _balanceChanges)
        {
            if (change.Key == Eip7928Constants.PrestateIndex)
            {
                continue;
            }

            if (change.Key < blockAccessIndex)
            {
                return true;
            }
            else if (change.Key >= blockAccessIndex)
            {
                break;
            }
        }

        CodeChange? lastCodeChange = null;
        foreach (KeyValuePair<uint, CodeChange> change in _codeChanges)
        {
            if (change.Key == Eip7928Constants.PrestateIndex)
            {
                continue;
            }

            if (change.Key >= blockAccessIndex)
            {
                break;
            }

            lastCodeChange = change.Value;
        }

        if (lastCodeChange is not null)
        {
            return lastCodeChange.Value.Code.Length != 0;
        }

        return false;
    }

    // assumes prestate not loaded
    public void CheckWasChanged() => _wasChanged = _balanceChanges.Count > 0 || _nonceChanges.Count > 0 || _codeChanges.Count > 0 || _storageChanges.Count > 0;

    [JsonIgnore]
    public bool AccountChanged => _wasChanged;
    private bool _wasChanged = false;

    private void GetCodeChange(uint blockAccessIndex, out CodeChange? codeChange)
    {
        int changeIndex = FindFirstRealIndexAtOrAfter(_codeChanges.Keys, blockAccessIndex);
        codeChange = changeIndex == 0 ? null : _codeChanges.Values[changeIndex - 1];
    }

    public bool SlotChangesAtIndexEqual(AccountChanges other, uint index)
    {
        using IEnumerator<(SlotChanges SlotChanges, StorageChange StorageChange)> left = SlotChangePairsAtIndex(index).GetEnumerator();
        using IEnumerator<(SlotChanges SlotChanges, StorageChange StorageChange)> right = other.SlotChangePairsAtIndex(index).GetEnumerator();

        while (true)
        {
            bool hasLeft = left.MoveNext();
            bool hasRight = right.MoveNext();
            if (!hasLeft || !hasRight)
            {
                return hasLeft == hasRight;
            }

            (SlotChanges leftSlot, StorageChange leftChange) = left.Current;
            (SlotChanges rightSlot, StorageChange rightChange) = right.Current;
            if (leftSlot.Key != rightSlot.Key || !leftChange.Equals(rightChange))
            {
                return false;
            }
        }
    }

    public string DescribeSlotChangesAtIndex(uint index)
    {
        StringBuilder builder = new();
        bool first = true;

        foreach ((SlotChanges slotChanges, StorageChange storageChange) in SlotChangePairsAtIndex(index))
        {
            if (!first)
            {
                builder.Append(", ");
            }

            builder.Append(slotChanges.Key);
            builder.Append(':');
            builder.Append(storageChange);
            first = false;
        }

        return builder.ToString();
    }

    private static int FindFirstRealIndexAtOrAfter(IList<uint> keys, uint blockAccessIndex)
    {
        int low = 0;
        int high = keys.Count;
        while (low < high)
        {
            int mid = low + ((high - low) >> 1);
            uint key = keys[mid];
            if (key == Eip7928Constants.PrestateIndex || key < blockAccessIndex)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    [DoesNotReturn]
    private void ThrowMissingCodeChange(uint blockAccessIndex)
        => throw new InvalidOperationException($"No code change found for {Address} at or before index {blockAccessIndex}. Was BAL prestate loaded?");

    private static bool ListEquals<T>(IList<T> left, IList<T> right)
        where T : IEquatable<T>
    {
        if (left.Count != right.Count)
            return false;

        for (int i = 0; i < left.Count; i++)
        {
            if (!left[i].Equals(right[i]))
                return false;
        }

        return true;
    }

    private static bool SetEquals<T>(SortedSet<T> left, SortedSet<T> right)
        => left.Count == right.Count && left.SetEquals(right);

    private static bool PopChange<T>(SortedList<uint, T> changes, uint index, out T change) where T : struct, IIndexedChange
    {
        int count = changes.Count;
        if (count != 0)
        {
            count--;
            IList<uint> keys = changes.Keys;
            if (keys[count] == index)
            {
                change = changes.Values[count];
                changes.RemoveAt(count);
                return true;
            }
        }
        change = default;
        return false;
    }
}
