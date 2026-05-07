
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Core.BlockAccessLists;

public class AccountChanges : IEquatable<AccountChanges>
{
    [JsonConverter(typeof(AddressConverter))]
    public Address Address { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IList<SlotChanges> StorageChanges => GetSortedStorageChanges();

    [JsonIgnore(Condition = JsonIgnoreCondition.Always)]
    public IList<UInt256> ChangedSlots => GetSortedChangedSlots();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SortedSet<UInt256> StorageReads => _storageReads;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IndexedChangeValues<BalanceChange> BalanceChanges => _balanceChanges.Values;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IndexedChangeValues<NonceChange> NonceChanges => _nonceChanges.Values;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IndexedChangeValues<CodeChange> CodeChanges => _codeChanges.Values;

    internal IndexedChanges<BalanceChange> BalanceChangeSet => _balanceChanges;

    internal IndexedChanges<NonceChange> NonceChangeSet => _nonceChanges;

    internal IndexedChanges<CodeChange> CodeChangeSet => _codeChanges;

    internal int StorageChangesCount => _storageChanges.Count;

    [JsonIgnore]
    public bool ExistedBeforeBlock { get; set; }

    [JsonIgnore]
    public bool EmptyBeforeBlock { get; set; }

    private readonly Dictionary<UInt256, SlotChanges> _storageChanges;
    private readonly SortedSet<UInt256> _storageReads;
    private readonly IndexedChanges<BalanceChange> _balanceChanges;
    private readonly IndexedChanges<NonceChange> _nonceChanges;
    private readonly IndexedChanges<CodeChange> _codeChanges;
    private SlotChanges[]? _sortedStorageChanges;
    private UInt256[]? _sortedChangedSlots;

    public AccountChanges()
    {
        Address = Address.Zero;
        _storageChanges = new(GenericEqualityComparer.GetOptimized<UInt256>());
        _storageReads = new(GenericComparer.GetOptimized<UInt256>());
        _balanceChanges = new();
        _nonceChanges = new();
        _codeChanges = new();
    }

    public AccountChanges(Address address)
    {
        Address = address;
        _storageChanges = new(GenericEqualityComparer.GetOptimized<UInt256>());
        _storageReads = new(GenericComparer.GetOptimized<UInt256>());
        _balanceChanges = new();
        _nonceChanges = new();
        _codeChanges = new();
    }

    public AccountChanges(Address address, SortedList<UInt256, SlotChanges> storageChanges, SortedSet<UInt256> storageReads, SortedList<uint, BalanceChange> balanceChanges, SortedList<uint, NonceChange> nonceChanges, SortedList<uint, CodeChange> codeChanges)
    {
        Address = address;
        _storageChanges = ToDictionary(storageChanges);
        SeedSortedStorageCaches(storageChanges);
        _storageReads = storageReads;
        _balanceChanges = IndexedChanges<BalanceChange>.FromSortedList(balanceChanges);
        _nonceChanges = IndexedChanges<NonceChange>.FromSortedList(nonceChanges);
        _codeChanges = IndexedChanges<CodeChange>.FromSortedList(codeChanges);
    }

    public static AccountChanges FromIndexedChanges(Address address, SortedList<UInt256, SlotChanges> storageChanges, SortedSet<UInt256> storageReads, IndexedChanges<BalanceChange> balanceChanges, IndexedChanges<NonceChange> nonceChanges, IndexedChanges<CodeChange> codeChanges) =>
        new(address, storageChanges, storageReads, balanceChanges, nonceChanges, codeChanges);

    public static AccountChanges FromSortedStorageChanges(Address address, SlotChanges[] storageChanges, SortedSet<UInt256> storageReads, IndexedChanges<BalanceChange> balanceChanges, IndexedChanges<NonceChange> nonceChanges, IndexedChanges<CodeChange> codeChanges) =>
        new(address, storageChanges, storageReads, balanceChanges, nonceChanges, codeChanges);

    private AccountChanges(Address address, SortedList<UInt256, SlotChanges> storageChanges, SortedSet<UInt256> storageReads, IndexedChanges<BalanceChange> balanceChanges, IndexedChanges<NonceChange> nonceChanges, IndexedChanges<CodeChange> codeChanges)
    {
        Address = address;
        _storageChanges = ToDictionary(storageChanges);
        SeedSortedStorageCaches(storageChanges);
        _storageReads = storageReads;
        _balanceChanges = balanceChanges;
        _nonceChanges = nonceChanges;
        _codeChanges = codeChanges;
    }

    private AccountChanges(Address address, SlotChanges[] storageChanges, SortedSet<UInt256> storageReads, IndexedChanges<BalanceChange> balanceChanges, IndexedChanges<NonceChange> nonceChanges, IndexedChanges<CodeChange> codeChanges)
    {
        Address = address;
        _storageChanges = new(storageChanges.Length, GenericEqualityComparer.GetOptimized<UInt256>());
        for (int i = 0; i < storageChanges.Length; i++)
        {
            SlotChanges slotChanges = storageChanges[i];
            _storageChanges.Add(slotChanges.Key, slotChanges);
        }
        _sortedStorageChanges = storageChanges;
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

        bool addedStorageChange = false;
        foreach (KeyValuePair<UInt256, SlotChanges> kv in other._storageChanges)
        {
            if (_storageChanges.TryGetValue(kv.Key, out SlotChanges? existing))
            {
                existing.Merge(kv.Value);
            }
            else
            {
                _storageChanges.Add(kv.Key, kv.Value);
                addedStorageChange = true;
                // When a new change is merged for a slot that previously only had a read,
                // remove the now-redundant read. A change entry supersedes a read.
                RemoveStorageRead(kv.Key);
            }
        }

        if (addedStorageChange)
        {
            InvalidateStorageOrder();
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
            InvalidateStorageOrder();
            _storageReads.Add(key);
        }
    }

    public SlotChanges GetOrAddSlotChanges(UInt256 key)
    {
        if (!_storageChanges.TryGetValue(key, out SlotChanges? existing))
        {
            SlotChanges slotChanges = new(key);
            _storageChanges.Add(key, slotChanges);
            InvalidateStorageOrder();
            return slotChanges;
        }
        return existing;
    }

    public bool HasSlotChangesAtIndex(uint index)
    {
        SlotChanges[] storageChanges = GetSortedStorageChanges();
        for (int i = 0; i < storageChanges.Length; i++)
        {
            SlotChanges slotChanges = storageChanges[i];
            if (slotChanges.Changes.ContainsKey(index))
                return true;
        }
        return false;
    }

    public SlotChangePairsAtIndexEnumerable SlotChangePairsAtIndex(uint index) =>
        new(GetSortedStorageChanges(), index);

    public void AddStorageRead(UInt256 key)
        => _storageReads.Add(key);

    public void RemoveStorageRead(UInt256 key)
        => _storageReads.Remove(key);

    public void SelfDestruct()
    {
        SlotChanges[] storageChanges = GetSortedStorageChanges();
        for (int i = 0; i < storageChanges.Length; i++)
        {
            AddStorageRead(storageChanges[i].Key);
        }

        _storageChanges.Clear();
        InvalidateStorageOrder();
        _nonceChanges.Clear();
        _codeChanges.Clear();
    }

    public void AddBalanceChange(BalanceChange balanceChange)
        => _balanceChanges.Set(balanceChange);

    internal bool TryPopBalanceChange(uint index, out BalanceChange balanceChange)
        => _balanceChanges.TryPopLast(index, out balanceChange);

    public BalanceChange? BalanceChangeAtIndex(uint index)
        => _balanceChanges.TryGetValue(index, out BalanceChange balanceChange) ? balanceChange : null;

    public void AddNonceChange(NonceChange nonceChange)
        => _nonceChanges.Add(nonceChange);

    internal bool TryPopNonceChange(uint index, out NonceChange nonceChange)
        => _nonceChanges.TryPopLast(index, out nonceChange);

    public NonceChange? NonceChangeAtIndex(uint index)
        => _nonceChanges.TryGetValue(index, out NonceChange nonceChange) ? nonceChange : null;

    public void AddCodeChange(CodeChange codeChange)
        => _codeChanges.Add(codeChange);

    internal bool TryPopCodeChange(uint index, out CodeChange codeChange)
        => _codeChanges.TryPopLast(index, out codeChange);

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

    public UInt256? GetNonce(uint blockAccessIndex) =>
        _nonceChanges.TryGetLastBeforeOrPrestate(blockAccessIndex, out NonceChange nonceChange)
            ? nonceChange.Value
            : null;

    public UInt256? GetBalance(uint blockAccessIndex) =>
        _balanceChanges.TryGetLastBeforeOrPrestate(blockAccessIndex, out BalanceChange balanceChange)
            ? balanceChange.Value
            : null;

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
        SlotChanges[] storageChanges = GetSortedStorageChanges();
        for (int i = 0; i < storageChanges.Length; i++)
        {
            SlotChanges slotChange = storageChanges[i];
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

        if (_nonceChanges.HasRealBefore(blockAccessIndex))
        {
            return true;
        }

        if (_balanceChanges.HasRealBefore(blockAccessIndex))
        {
            return true;
        }

        if (_codeChanges.TryGetLastRealBefore(blockAccessIndex, out CodeChange lastCodeChange))
        {
            return lastCodeChange.Code.Length != 0;
        }

        return false;
    }

    // assumes prestate not loaded
    public void CheckWasChanged() => _wasChanged = _balanceChanges.Count > 0 || _nonceChanges.Count > 0 || _codeChanges.Count > 0 || _storageChanges.Count > 0;

    [JsonIgnore]
    public bool AccountChanged => _wasChanged;
    private bool _wasChanged = false;

    private void GetCodeChange(uint blockAccessIndex, out CodeChange? codeChange) =>
        codeChange = _codeChanges.TryGetLastBeforeOrPrestate(blockAccessIndex, out CodeChange change)
            ? change
            : null;

    private SlotChanges[] GetSortedStorageChanges()
    {
        SlotChanges[]? sortedStorageChanges = _sortedStorageChanges;
        if (sortedStorageChanges is not null)
        {
            return sortedStorageChanges;
        }

        if (_storageChanges.Count == 0)
        {
            _sortedStorageChanges = [];
            return _sortedStorageChanges;
        }

        sortedStorageChanges = new SlotChanges[_storageChanges.Count];
        _storageChanges.Values.CopyTo(sortedStorageChanges, 0);
        Array.Sort(sortedStorageChanges, static (left, right) => left.Key.CompareTo(right.Key));
        _sortedStorageChanges = sortedStorageChanges;
        return sortedStorageChanges;
    }

    private UInt256[] GetSortedChangedSlots()
    {
        UInt256[]? sortedChangedSlots = _sortedChangedSlots;
        if (sortedChangedSlots is not null)
        {
            return sortedChangedSlots;
        }

        SlotChanges[] storageChanges = GetSortedStorageChanges();
        if (storageChanges.Length == 0)
        {
            _sortedChangedSlots = [];
            return _sortedChangedSlots;
        }

        sortedChangedSlots = new UInt256[storageChanges.Length];
        for (int i = 0; i < storageChanges.Length; i++)
        {
            sortedChangedSlots[i] = storageChanges[i].Key;
        }
        _sortedChangedSlots = sortedChangedSlots;
        return sortedChangedSlots;
    }

    private void InvalidateStorageOrder()
    {
        _sortedStorageChanges = null;
        _sortedChangedSlots = null;
    }

    private static Dictionary<UInt256, SlotChanges> ToDictionary(SortedList<UInt256, SlotChanges> storageChanges)
    {
        Dictionary<UInt256, SlotChanges> dictionary = new(storageChanges.Count, GenericEqualityComparer.GetOptimized<UInt256>());
        IList<UInt256> keys = storageChanges.Keys;
        IList<SlotChanges> values = storageChanges.Values;
        for (int i = 0; i < storageChanges.Count; i++)
        {
            dictionary.Add(keys[i], values[i]);
        }

        return dictionary;
    }

    private void SeedSortedStorageCaches(SortedList<UInt256, SlotChanges> storageChanges)
    {
        int count = storageChanges.Count;
        if (count == 0)
        {
            _sortedStorageChanges = [];
            _sortedChangedSlots = [];
            return;
        }

        SlotChanges[] sortedStorageChanges = new SlotChanges[count];
        UInt256[] sortedChangedSlots = new UInt256[count];
        IList<UInt256> keys = storageChanges.Keys;
        IList<SlotChanges> values = storageChanges.Values;
        for (int i = 0; i < count; i++)
        {
            sortedChangedSlots[i] = keys[i];
            sortedStorageChanges[i] = values[i];
        }

        _sortedStorageChanges = sortedStorageChanges;
        _sortedChangedSlots = sortedChangedSlots;
    }

    public bool SlotChangesAtIndexEqual(AccountChanges other, uint index)
    {
        SlotChangePairsAtIndexEnumerable.Enumerator left = SlotChangePairsAtIndex(index).GetEnumerator();
        SlotChangePairsAtIndexEnumerable.Enumerator right = other.SlotChangePairsAtIndex(index).GetEnumerator();

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

    [DoesNotReturn, StackTraceHidden]
    private void ThrowMissingCodeChange(uint blockAccessIndex)
        => throw new InvalidOperationException($"No code change found for {Address} at or before index {blockAccessIndex}. Was BAL prestate loaded?");

    private static bool ListEquals<T>(IReadOnlyList<T> left, IReadOnlyList<T> right)
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

    public readonly struct SlotChangePairsAtIndexEnumerable
    {
        private readonly SlotChanges[] _storageChanges;
        private readonly uint _index;

        internal SlotChangePairsAtIndexEnumerable(SlotChanges[] storageChanges, uint index)
        {
            _storageChanges = storageChanges;
            _index = index;
        }

        public Enumerator GetEnumerator() => new(_storageChanges, _index);

        public struct Enumerator
        {
            private readonly SlotChanges[] _storageChanges;
            private readonly uint _index;
            private int _slotIndex;
            private (SlotChanges SlotChanges, StorageChange StorageChange) _current;

            internal Enumerator(SlotChanges[] storageChanges, uint index)
            {
                _storageChanges = storageChanges;
                _index = index;
                _slotIndex = -1;
                _current = default;
            }

            public readonly (SlotChanges SlotChanges, StorageChange StorageChange) Current => _current;

            public bool MoveNext()
            {
                for (int i = _slotIndex + 1; i < _storageChanges.Length; i++)
                {
                    SlotChanges slotChanges = _storageChanges[i];
                    if (slotChanges.Changes.TryGetValue(_index, out StorageChange storageChange))
                    {
                        _slotIndex = i;
                        _current = (slotChanges, storageChange);
                        return true;
                    }
                }

                _slotIndex = _storageChanges.Length;
                _current = default;
                return false;
            }
        }
    }

}
