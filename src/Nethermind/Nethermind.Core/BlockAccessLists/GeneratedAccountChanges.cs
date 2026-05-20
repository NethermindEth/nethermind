// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using Nethermind.Core.Collections;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Per-account changes assembled from one or more <see cref="AccountChangesAtIndex"/> via merging.
/// Append-only and ordered by index; uses simple <see cref="List{T}"/> per change family because
/// merge contributions arrive sorted.
/// </summary>
/// <remarks>
/// Storage changes and storage reads are kept in unsorted <see cref="Dictionary{TKey, TValue}"/> /
/// <see cref="HashSet{T}"/>. EIP-7928 requires slot-sorted output on the wire, but that's paid
/// once per block at encode time rather than O(log n) on every per-tx merge.
/// </remarks>
public class GeneratedAccountChanges(Address address)
{
    private static readonly Comparison<GeneratedSlotChanges> _bySlotKey = static (a, b) => a.Key.CompareTo(b.Key);

    [JsonConverter(typeof(AddressConverter))]
    public Address Address { get; } = address;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<BalanceChange> BalanceChanges { get; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<NonceChange> NonceChanges { get; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<CodeChange> CodeChanges { get; } = [];

    private readonly Dictionary<UInt256, GeneratedSlotChanges> _storageChanges = new();
    private readonly HashSet<UInt256> _storageReads = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyCollection<GeneratedSlotChanges> StorageChanges => _storageChanges.Values;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyCollection<UInt256> StorageReads => _storageReads;

    /// <summary>Slot-key-sorted snapshot of the per-slot changes; pooled, dispose after use.</summary>
    public ArrayPoolListRef<GeneratedSlotChanges> GetSortedStorageChanges()
    {
        ArrayPoolListRef<GeneratedSlotChanges> result = new(_storageChanges.Count);
        foreach (GeneratedSlotChanges sc in _storageChanges.Values) result.Add(sc);
        result.AsSpan().Sort(_bySlotKey);
        return result;
    }

    /// <summary>Slot-key-sorted snapshot of the storage reads; pooled, dispose after use.</summary>
    public ArrayPoolListRef<UInt256> GetSortedStorageReads()
    {
        ArrayPoolListRef<UInt256> result = new(_storageReads.Count);
        foreach (UInt256 r in _storageReads) result.Add(r);
        result.AsSpan().Sort(GenericComparer.GetOptimized<UInt256>());
        return result;
    }

    public bool TryGetSlotChanges(UInt256 key, [NotNullWhen(true)] out GeneratedSlotChanges? slotChanges)
        => _storageChanges.TryGetValue(key, out slotChanges);

    /// <summary>Binary-search the entry with <c>Index == index</c>; null if none.
    /// Lists are kept sorted by <c>Index</c> via the monotonic <see cref="Merge"/> contract.</summary>
    public BalanceChange? BalanceChangeAtIndex(uint index) => GetExact(BalanceChanges, index);
    public NonceChange? NonceChangeAtIndex(uint index) => GetExact(NonceChanges, index);
    public CodeChange? CodeChangeAtIndex(uint index) => GetExact(CodeChanges, index);

    public bool HasSlotChangesAtIndex(uint index)
    {
        foreach (GeneratedSlotChanges slot in _storageChanges.Values)
        {
            if (TryGetSlotChangeAtIndex(slot, index, out _)) return true;
        }
        return false;
    }

    /// <summary>True iff this account has no balance/nonce/code/slot change at <paramref name="index"/>.
    /// Storage reads are not changes; this only inspects mutating entries.</summary>
    public bool HasNoChangesAtIndex(uint index)
        => BalanceChangeAtIndex(index) is null
        && NonceChangeAtIndex(index) is null
        && CodeChangeAtIndex(index) is null
        && !HasSlotChangesAtIndex(index);

    /// <summary>Structural equality of this account's slice at <paramref name="index"/> against
    /// the suggested (decoded) account. Address is not compared (callers ensure they match).</summary>
    public bool ChangesAtIndexEqual(ReadOnlyAccountChanges other, uint index)
    {
        if (BalanceChangeAtIndex(index) != other.BalanceChangeAtIndex(index)) return false;
        if (NonceChangeAtIndex(index) != other.NonceChangeAtIndex(index)) return false;

        CodeChange? thisCode = CodeChangeAtIndex(index);
        CodeChange? otherCode = other.CodeChangeAtIndex(index);
        if (thisCode.HasValue != otherCode.HasValue) return false;
        if (thisCode.HasValue && !thisCode.Value.Equals(otherCode!.Value)) return false;

        return SlotChangesAtIndexEqual(other, index);
    }

    private bool SlotChangesAtIndexEqual(ReadOnlyAccountChanges other, uint index)
    {
        // Linear merge over both sides walked in slot-key order. The generated side is sorted once
        // via a pooled snapshot; the suggested side is already sorted (decoder-validated). The
        // concrete struct enumerator is taken explicitly to dodge IEnumerable boxing.
        using ArrayPoolListRef<GeneratedSlotChanges> sortedGen = GetSortedStorageChanges();
        ReadOnlySpan<GeneratedSlotChanges> a = sortedGen.AsSpan();
        ReadOnlySpan<ReadOnlySlotChanges> b = other.StorageChanges;

        int i = 0, j = 0;
        StorageChange aChange = default, bChange = default;
        while (true)
        {
            bool aMatched = false;
            while (i < a.Length)
            {
                if (TryGetSlotChangeAtIndex(a[i], index, out aChange)) { aMatched = true; break; }
                i++;
            }
            bool bMatched = false;
            while (j < b.Length)
            {
                if (TryGetSlotChangeAtIndex(b[j], index, out bChange)) { bMatched = true; break; }
                j++;
            }

            if (!aMatched && !bMatched) return true;
            if (aMatched != bMatched) return false;
            if (a[i].Key != b[j].Key) return false;
            if (!aChange.Equals(bChange)) return false;

            i++;
            j++;
        }
    }

    private static bool TryGetSlotChangeAtIndex(GeneratedSlotChanges slot, uint index, out StorageChange change)
    {
        ReadOnlySpan<StorageChange> span = CollectionsMarshal.AsSpan(slot.Changes);
        int idx = span.BinarySearch(new IndexKey<StorageChange>(index));
        if (idx >= 0)
        {
            change = span[idx];
            return true;
        }
        change = default;
        return false;
    }

    private static bool TryGetSlotChangeAtIndex(ReadOnlySlotChanges slot, uint index, out StorageChange change)
    {
        ReadOnlySpan<StorageChange> span = slot.Changes;
        int idx = span.BinarySearch(new IndexKey<StorageChange>(index));
        if (idx >= 0)
        {
            change = span[idx];
            return true;
        }
        change = default;
        return false;
    }

    /// <summary>O(log n) lookup of the entry with <c>Index == index</c> over a list kept sorted
    /// by index (the merge contract on <see cref="GeneratedAccountChanges"/> guarantees that).</summary>
    private static T? GetExact<T>(List<T> changes, uint index) where T : struct, IIndexedChange
    {
        ReadOnlySpan<T> span = CollectionsMarshal.AsSpan(changes);
        int idx = span.BinarySearch(new IndexKey<T>(index));
        return idx >= 0 ? span[idx] : null;
    }

    public GeneratedSlotChanges GetOrAddSlotChanges(UInt256 key)
    {
        if (!_storageChanges.TryGetValue(key, out GeneratedSlotChanges? existing))
        {
            existing = new GeneratedSlotChanges(key);
            _storageChanges.Add(key, existing);
        }
        return existing;
    }

    public void AddStorageRead(UInt256 key)
    {
        if (!_storageChanges.ContainsKey(key))
        {
            _storageReads.Add(key);
        }
    }

    /// <summary>
    /// Merge the per-index source into this accumulator. Caller must ensure indices arrive monotonically.
    /// </summary>
    public void Merge(AccountChangesAtIndex other)
    {
        if (other.BalanceChange is not null)
        {
            BalanceChanges.Add(other.BalanceChange.Value);
        }
        if (other.NonceChange is not null)
        {
            NonceChanges.Add(other.NonceChange.Value);
        }
        if (other.CodeChange is not null)
        {
            CodeChanges.Add(other.CodeChange.Value);
        }

        foreach (KeyValuePair<UInt256, StorageChange> kv in other.StorageChanges)
        {
            GeneratedSlotChanges slotChanges = GetOrAddSlotChanges(kv.Key);
            slotChanges.Changes.Add(kv.Value);
            // a change supersedes any prior read for the same slot
            _storageReads.Remove(kv.Key);
        }

        foreach (UInt256 read in other.StorageReads)
        {
            if (!_storageChanges.ContainsKey(read))
            {
                _storageReads.Add(read);
            }
        }
    }

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.Append(Address);
        if (BalanceChanges.Count > 0) sb.Append($" balance=[{string.Join(", ", BalanceChanges)}]");
        if (NonceChanges.Count > 0) sb.Append($" nonce=[{string.Join(", ", NonceChanges)}]");
        if (CodeChanges.Count > 0) sb.Append($" code=[{string.Join(", ", CodeChanges)}]");
        if (_storageChanges.Count > 0)
        {
            using ArrayPoolListRef<GeneratedSlotChanges> sorted = GetSortedStorageChanges();
            sb.Append($" storage=[{string.Join(", ", sorted.ToArray())}]");
        }
        if (_storageReads.Count > 0)
        {
            using ArrayPoolListRef<UInt256> sorted = GetSortedStorageReads();
            sb.Append($" reads=[{string.Join(", ", sorted.ToArray())}]");
        }
        return sb.ToString();
    }
}
