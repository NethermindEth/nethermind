
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Core.BlockAccessLists;

public class AccountChanges : IEquatable<AccountChanges>
{
    [JsonConverter(typeof(AddressConverter))]
    public Address Address { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyList<SlotChanges> StorageChanges => _storageChanges;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyList<StorageRead> StorageReads => _storageReads;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyList<BalanceChange> BalanceChanges => _balanceChanges;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyList<NonceChange> NonceChanges => _nonceChanges;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyList<CodeChange> CodeChanges => _codeChanges;

    private readonly List<SlotChanges> _storageChanges;
    private readonly Dictionary<UInt256, SlotChanges> _storageChangesByKey;
    private readonly List<StorageRead> _storageReads;
    private readonly HashSet<UInt256> _storageReadsSet;
    private readonly List<BalanceChange> _balanceChanges;
    private readonly List<NonceChange> _nonceChanges;
    private readonly List<CodeChange> _codeChanges;

    private static readonly Comparer<SlotChanges> s_slotComparer =
        Comparer<SlotChanges>.Create((a, b) => a.Slot.CompareTo(b.Slot));

    public AccountChanges() : this(Address.Zero) { }

    public AccountChanges(Address address)
    {
        Address = address;
        _storageChanges = [];
        _storageChangesByKey = [];
        _storageReads = [];
        _storageReadsSet = [];
        _balanceChanges = [];
        _nonceChanges = [];
        _codeChanges = [];
    }

    public AccountChanges(
        Address address,
        List<SlotChanges> storageChanges,
        List<StorageRead> storageReads,
        List<BalanceChange> balanceChanges,
        List<NonceChange> nonceChanges,
        List<CodeChange> codeChanges)
    {
        Address = address;
        _storageChanges = storageChanges;
        _storageChangesByKey = new(storageChanges.Count);
        foreach (SlotChanges sc in storageChanges)
        {
            _storageChangesByKey[sc.Slot] = sc;
        }

        _storageReads = storageReads;
        _storageReadsSet = new(storageReads.Count);
        foreach (StorageRead sr in storageReads)
        {
            _storageReadsSet.Add(sr.Key);
        }

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
        => _storageChangesByKey.ContainsKey(key);

    public bool TryGetSlotChanges(UInt256 key, [NotNullWhen(true)] out SlotChanges? slotChanges)
        => _storageChangesByKey.TryGetValue(key, out slotChanges);

    public void ClearEmptySlotChangesAndAddRead(UInt256 key)
    {
        if (_storageChangesByKey.TryGetValue(key, out SlotChanges? slotChanges) && slotChanges.Changes.Count == 0)
        {
            _storageChangesByKey.Remove(key);
            RemoveSortedSlot(key);
            AddStorageRead(key);
        }
    }

    public SlotChanges GetOrAddSlotChanges(UInt256 key)
    {
        if (!_storageChangesByKey.TryGetValue(key, out SlotChanges? existing))
        {
            SlotChanges slotChanges = new(key);
            _storageChangesByKey[key] = slotChanges;
            InsertSortedSlot(slotChanges);
            return slotChanges;
        }
        return existing;
    }

    public IEnumerable<SlotChanges> SlotChangesAtIndex(ushort index)
    {
        foreach (SlotChanges slotChanges in _storageChanges)
        {
            if (slotChanges.TryGetChangeAtIndex(index, out StorageChange storageChange))
            {
                yield return new(slotChanges.Slot, [storageChange]);
            }
        }
    }

    public void AddStorageRead(UInt256 key)
    {
        if (_storageReadsSet.Add(key))
        {
            InsertSortedRead(new(key));
        }
    }

    public void RemoveStorageRead(UInt256 key)
    {
        if (_storageReadsSet.Remove(key))
        {
            RemoveSortedRead(key);
        }
    }

    public void SelfDestruct()
    {
        // Snapshot keys before clearing (AddStorageRead mutates the same structures).
        UInt256[] slotKeys = new UInt256[_storageChanges.Count];
        for (int i = 0; i < _storageChanges.Count; i++)
        {
            slotKeys[i] = _storageChanges[i].Slot;
        }

        _storageChanges.Clear();
        _storageChangesByKey.Clear();
        _nonceChanges.Clear();
        _codeChanges.Clear();

        foreach (UInt256 key in slotKeys)
        {
            AddStorageRead(key);
        }
    }

    public void AddBalanceChange(BalanceChange balanceChange)
        => _balanceChanges.Add(balanceChange);

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

    public BalanceChange? BalanceChangeAtIndex(ushort index)
    {
        foreach (BalanceChange bc in _balanceChanges)
        {
            if (bc.BlockAccessIndex == index) return bc;
        }
        return null;
    }

    public void AddNonceChange(NonceChange nonceChange)
        => _nonceChanges.Add(nonceChange);

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

    public NonceChange? NonceChangeAtIndex(ushort index)
    {
        foreach (NonceChange nc in _nonceChanges)
        {
            if (nc.BlockAccessIndex == index) return nc;
        }
        return null;
    }

    public void AddCodeChange(CodeChange codeChange)
        => _codeChanges.Add(codeChange);

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

    public CodeChange? CodeChangeAtIndex(ushort index)
    {
        foreach (CodeChange cc in _codeChanges)
        {
            if (cc.BlockAccessIndex == index) return cc;
        }
        return null;
    }

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

    private void InsertSortedSlot(SlotChanges slotChanges)
    {
        int idx = _storageChanges.BinarySearch(slotChanges, s_slotComparer);
        if (idx < 0) idx = ~idx;
        _storageChanges.Insert(idx, slotChanges);
    }

    private void RemoveSortedSlot(UInt256 key)
    {
        // _storageChanges is kept sorted by Slot — binary search on Slot.
        int lo = 0, hi = _storageChanges.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            int cmp = _storageChanges[mid].Slot.CompareTo(key);
            if (cmp == 0)
            {
                _storageChanges.RemoveAt(mid);
                return;
            }
            if (cmp < 0) lo = mid + 1;
            else hi = mid - 1;
        }
    }

    private void InsertSortedRead(StorageRead storageRead)
    {
        int lo = 0, hi = _storageReads.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            int cmp = _storageReads[mid].CompareTo(storageRead);
            if (cmp < 0) lo = mid + 1;
            else hi = mid - 1;
        }
        _storageReads.Insert(lo, storageRead);
    }

    private void RemoveSortedRead(UInt256 key)
    {
        int lo = 0, hi = _storageReads.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            int cmp = _storageReads[mid].Key.CompareTo(key);
            if (cmp == 0)
            {
                _storageReads.RemoveAt(mid);
                return;
            }
            if (cmp < 0) lo = mid + 1;
            else hi = mid - 1;
        }
    }

    private static bool PopChange<T>(List<T> changes, ushort index, [NotNullWhen(true)] out T? change) where T : IIndexedChange
    {
        change = default;

        if (changes.Count == 0)
            return false;

        T lastChange = changes[^1];

        if (lastChange.BlockAccessIndex == index)
        {
            changes.RemoveAt(changes.Count - 1);
            change = lastChange;
            return true;
        }

        return false;
    }
}
