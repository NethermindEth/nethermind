// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Per-account changes from a decoded BAL. Index-keyed change families are stored as plain
/// arrays kept sorted by <see cref="IIndexedChange.Index"/> (the decoder validates ordering),
/// so reads can binary-search via <see cref="System.MemoryExtensions"/>. Storage changes are
/// kept in two parallel structures: a hash map for O(1) <see cref="TryGetSlotChanges"/>
/// lookups (used during EVM execution) and an array sorted by slot key for ordered iteration
/// (used by the cache prewarmer's sorted-merge with <see cref="StorageReads"/>). The only
/// mutations permitted are those performed by prestate loading (additions at index <c>-1</c>
/// + the related flags); both representations are updated in lockstep when prestate loading
/// adds a previously read-only slot.
/// </summary>
public class ReadOnlyAccountChanges : IEquatable<ReadOnlyAccountChanges>
{
    [JsonConverter(typeof(AddressConverter))]
    public Address Address { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ReadOnlySlotChanges[] StorageChanges
    {
        get
        {
            if (_sortedDirty) RebuildSortedArrays();
            return _orderedStorageChanges;
        }
    }

    /// <summary>Slot keys, sorted ascending — exposed as <see cref="IList{T}"/> for indexed access.</summary>
    [JsonIgnore]
    public IList<UInt256> ChangedSlots
    {
        get
        {
            if (_sortedDirty) RebuildSortedArrays();
            return _changedSlots;
        }
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public UInt256[] StorageReads => _storageReads;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public BalanceChange[] BalanceChanges => _balanceChanges;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public NonceChange[] NonceChanges => _nonceChanges;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public CodeChange[] CodeChanges => _codeChanges;

    [JsonIgnore]
    public bool ExistedBeforeBlock { get; private set; }

    [JsonIgnore]
    public bool EmptyBeforeBlock { get; private set; }

    [JsonIgnore]
    public bool AccountChanged { get; private set; }

    private readonly Dictionary<UInt256, ReadOnlySlotChanges> _storageChanges;
    private ReadOnlySlotChanges[] _orderedStorageChanges;
    private UInt256[] _changedSlots;
    private bool _sortedDirty;
    private readonly UInt256[] _storageReads;
    private BalanceChange[] _balanceChanges;
    private NonceChange[] _nonceChanges;
    private CodeChange[] _codeChanges;

    public ReadOnlyAccountChanges(
        Address address,
        ReadOnlySlotChanges[] storageChanges,
        UInt256[] storageReads,
        BalanceChange[] balanceChanges,
        NonceChange[] nonceChanges,
        CodeChange[] codeChanges)
    {
        Address = address;
        _orderedStorageChanges = storageChanges;
        _storageChanges = new Dictionary<UInt256, ReadOnlySlotChanges>(storageChanges.Length);
        _changedSlots = new UInt256[storageChanges.Length];
        for (int i = 0; i < storageChanges.Length; i++)
        {
            ReadOnlySlotChanges sc = storageChanges[i];
            _storageChanges.Add(sc.Key, sc);
            _changedSlots[i] = sc.Key;
        }
        _storageReads = storageReads;
        _balanceChanges = balanceChanges;
        _nonceChanges = nonceChanges;
        _codeChanges = codeChanges;
    }

    public ReadOnlyAccountChanges(Address address) : this(address, [], [], [], [], []) { }

    public bool TryGetSlotChanges(UInt256 key, [NotNullWhen(true)] out ReadOnlySlotChanges? slotChanges)
        => _storageChanges.TryGetValue(key, out slotChanges);

    public BalanceChange? BalanceChangeAtIndex(int index) => GetExact(_balanceChanges, index);
    public NonceChange? NonceChangeAtIndex(int index) => GetExact(_nonceChanges, index);
    public CodeChange? CodeChangeAtIndex(int index) => GetExact(_codeChanges, index);

    public bool HasSlotChangesAtIndex(int index)
    {
        foreach (ReadOnlySlotChanges slotChanges in _orderedStorageChanges)
        {
            if (HasExactIndex(slotChanges.Changes, index)) return true;
        }
        return false;
    }

    public IEnumerable<SlotChangeAtIndex> SlotChangesAtIndex(int index)
    {
        foreach (ReadOnlySlotChanges slotChanges in _orderedStorageChanges)
        {
            StorageChange? change = GetExact(slotChanges.Changes, index);
            if (change is not null)
            {
                yield return new SlotChangeAtIndex(slotChanges.Key, change.Value);
            }
        }
    }

    /// <summary>True iff this account has no balance/nonce/code/slot change at <paramref name="index"/>.
    /// Storage reads are not changes; this only inspects mutating entries.</summary>
    public bool HasNoChangesAtIndex(ushort index)
        => BalanceChangeAtIndex(index) is null
        && NonceChangeAtIndex(index) is null
        && CodeChangeAtIndex(index) is null
        && !HasSlotChangesAtIndex(index);

    /// <summary>Most recent balance strictly before <paramref name="blockAccessIndex"/>; null if none.</summary>
    public UInt256? GetBalance(int blockAccessIndex) => TryGetLastBefore(_balanceChanges, blockAccessIndex, out BalanceChange last) ? last.Value : null;

    public UInt256? GetNonce(int blockAccessIndex) => TryGetLastBefore(_nonceChanges, blockAccessIndex, out NonceChange last) ? last.Value : null;

    public byte[] GetCode(int blockAccessIndex)
        => TryGetLastBefore(_codeChanges, blockAccessIndex, out CodeChange last) ? last.Code : [];

    public ValueHash256 GetCodeHash(int blockAccessIndex)
        => TryGetLastBefore(_codeChanges, blockAccessIndex, out CodeChange last) ? last.CodeHash : Keccak.OfAnEmptyString.ValueHash256;

    public bool AccountExists(int blockAccessIndex)
    {
        if (ExistedBeforeBlock)
        {
            return true;
        }

        // Skip the prestate entry at index -1 (added by LoadPreStateBalance / LoadPreStateNonce
        // for every account in the BAL, including accounts that did not yet exist). Existence
        // by `blockAccessIndex` requires a real tx-level balance or nonce change at index in
        // [0, blockAccessIndex) — only that proves the account was created in a prior tx.
        foreach (NonceChange change in _nonceChanges)
        {
            if (change.Index < 0) continue;
            return change.Index < blockAccessIndex;
        }

        foreach (BalanceChange change in _balanceChanges)
        {
            if (change.Index < 0) continue;
            return change.Index < blockAccessIndex;
        }

        return false;
    }

    public bool Equals(ReadOnlyAccountChanges? other)
    {
        if (other is null) return false;
        if (Address != other.Address) return false;
        if (_storageChanges.Count != other._storageChanges.Count) return false;
        foreach (KeyValuePair<UInt256, ReadOnlySlotChanges> kv in _storageChanges)
        {
            if (!other._storageChanges.TryGetValue(kv.Key, out ReadOnlySlotChanges? otherVal) || !kv.Value.Equals(otherVal))
                return false;
        }
        return _storageReads.SequenceEqual(other._storageReads)
            && _balanceChanges.SequenceEqual(other._balanceChanges)
            && _nonceChanges.SequenceEqual(other._nonceChanges)
            && _codeChanges.SequenceEqual(other._codeChanges);
    }

    public override bool Equals(object? obj) => obj is ReadOnlyAccountChanges other && Equals(other);

    public override int GetHashCode() => Address.GetHashCode();

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.Append(Address);
        if (BalanceChanges.Length > 0) sb.Append($" balance=[{string.Join(", ", BalanceChanges)}]");
        if (NonceChanges.Length > 0) sb.Append($" nonce=[{string.Join(", ", NonceChanges)}]");
        if (CodeChanges.Length > 0) sb.Append($" code=[{string.Join(", ", CodeChanges)}]");
        if (StorageChanges.Length > 0) sb.Append($" storage=[{string.Join(", ", (object[])StorageChanges)}]");
        if (StorageReads.Length > 0) sb.Append($" reads=[{string.Join(", ", StorageReads)}]");
        return sb.ToString();
    }

    // === Mutations limited to LoadPreStateToSuggestedBlockAccessList (called from BlockAccessListManager) ===
    // Prestate is keyed at index -1, smaller than every real change, so we prepend (one realloc)
    // to preserve the sorted-by-index invariant.

    public void LoadPreStateBalance(UInt256 balance) => _balanceChanges = [new BalanceChange(-1, balance), .. _balanceChanges];
    public void LoadPreStateNonce(ulong nonce) => _nonceChanges = [new NonceChange(-1, nonce), .. _nonceChanges];
    public void LoadPreStateCode(byte[] code) => _codeChanges = [new CodeChange(-1, code), .. _codeChanges];

    public void LoadPreStateStorage(UInt256 slot, UInt256 value)
    {
        if (!_storageChanges.TryGetValue(slot, out ReadOnlySlotChanges? slotChanges))
        {
            slotChanges = new ReadOnlySlotChanges(slot);
            _storageChanges.Add(slot, slotChanges);
            _sortedDirty = true;
        }
        slotChanges.LoadPreStateChange(new StorageChange(-1, value));
    }

    public IEnumerable<UInt256> GetSlotsForPreStateLoad()
    {
        foreach (UInt256 slot in _changedSlots) yield return slot;
        foreach (UInt256 r in _storageReads) yield return r;
    }

    public void SetExistedBeforeBlock(bool value) => ExistedBeforeBlock = value;
    public void SetEmptyBeforeBlock(bool value) => EmptyBeforeBlock = value;
    public void RecordWasChanged()
        => AccountChanged = _balanceChanges.Length > 0 || _nonceChanges.Length > 0 || _codeChanges.Length > 0 || _storageChanges.Count > 0;

    private void RebuildSortedArrays()
    {
        _sortedDirty = false;
        int count = _storageChanges.Count;
        UInt256[] keys = new UInt256[count];
        ReadOnlySlotChanges[] ordered = new ReadOnlySlotChanges[count];
        int i = 0;
        foreach (KeyValuePair<UInt256, ReadOnlySlotChanges> kv in _storageChanges)
        {
            keys[i] = kv.Key;
            ordered[i] = kv.Value;
            i++;
        }
        Array.Sort(keys, ordered);
        _changedSlots = keys;
        _orderedStorageChanges = ordered;
    }

    /// <summary>Returns the change with <c>Index == index</c> if any; otherwise null.</summary>
    private static T? GetExact<T>(T[] changes, int index) where T : struct, IIndexedChange
    {
        ReadOnlySpan<T> span = changes;
        int idx = span.BinarySearch(new IndexKey<T>(index));
        return idx >= 0 ? span[idx] : null;
    }

    private static bool HasExactIndex<T>(T[] changes, int index) where T : struct, IIndexedChange
    {
        ReadOnlySpan<T> span = changes;
        return span.BinarySearch(new IndexKey<T>(index)) >= 0;
    }

    /// <summary>Returns the entry with the largest Index strictly less than <paramref name="blockAccessIndex"/>, or false if none.</summary>
    private static bool TryGetLastBefore<T>(T[] changes, int blockAccessIndex, out T last) where T : struct, IIndexedChange
    {
        ReadOnlySpan<T> span = changes;
        int idx = span.BinarySearch(new IndexKey<T>(blockAccessIndex));
        // (idx if found, ~idx otherwise) is the position of the first entry with Index >= target;
        // the last strictly-before is one step earlier.
        int lastBefore = (idx >= 0 ? idx : ~idx) - 1;
        if (lastBefore < 0)
        {
            last = default;
            return false;
        }
        last = span[lastBefore];
        return true;
    }
}
