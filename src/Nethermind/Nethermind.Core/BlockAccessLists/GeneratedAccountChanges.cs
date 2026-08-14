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
/// Append-only and ordered by index.
/// </summary>
public class GeneratedAccountChanges(Address address) : IComparable<GeneratedAccountChanges>
{
    [JsonConverter(typeof(AddressConverter))]
    public Address Address { get; } = address;

    public int CompareTo(GeneratedAccountChanges? other) => other is null ? 1 : Address.CompareTo(other.Address);

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<BalanceChange> BalanceChanges { get; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<NonceChange> NonceChanges { get; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<CodeChange> CodeChanges { get; } = [];

    private readonly Dictionary<UInt256, GeneratedSlotChanges> _storageChanges = new(GenericEqualityComparer.GetOptimized<UInt256>());
    private readonly HashSet<UInt256> _storageReads = new(GenericEqualityComparer.GetOptimized<UInt256>());

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyCollection<GeneratedSlotChanges> StorageChanges => _storageChanges.Values;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyCollection<UInt256> StorageReads => _storageReads;

    /// <summary>
    /// Slot-key-sorted snapshot; pooled, dispose after use.
    /// </summary>
    public ArrayPoolListRef<GeneratedSlotChanges> GetSortedStorageChanges()
    {
        ArrayPoolListRef<GeneratedSlotChanges> result = new(_storageChanges.Count);
        foreach (GeneratedSlotChanges sc in _storageChanges.Values) result.Add(sc);
        result.AsSpan().Sort(GenericComparer.GetOptimized<GeneratedSlotChanges>());
        return result;
    }

    /// <summary>
    /// Slot-key-sorted snapshot; pooled, dispose after use.
    /// </summary>
    public ArrayPoolListRef<UInt256> GetSortedStorageReads()
    {
        ArrayPoolListRef<UInt256> result = new(_storageReads.Count);
        foreach (UInt256 r in _storageReads) result.Add(r);
        result.AsSpan().Sort(GenericComparer.GetOptimized<UInt256>());
        return result;
    }

    public bool TryGetSlotChanges(UInt256 key, [NotNullWhen(true)] out GeneratedSlotChanges? slotChanges)
        => _storageChanges.TryGetValue(key, out slotChanges);

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

    /// <summary>
    /// True iff there is no balance/nonce/code/slot change at <paramref name="index"/>; reads are ignored.
    /// </summary>
    public bool HasNoChangesAtIndex(uint index)
        => BalanceChangeAtIndex(index) is null
        && NonceChangeAtIndex(index) is null
        && CodeChangeAtIndex(index) is null
        && !HasSlotChangesAtIndex(index);

    /// <summary>
    /// Structural equality at <paramref name="index"/> vs the suggested account (address not compared).
    /// </summary>
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
        // Linear walk in slot-key order: generated side sorted once, suggested side already sorted.
        using ArrayPoolListRef<GeneratedSlotChanges> sortedGen = GetSortedStorageChanges();
        ReadOnlySpan<GeneratedSlotChanges> a = sortedGen.AsSpan();
        ReadOnlySpan<ReadOnlySlotChanges> b = other.StorageChanges;

        int i = 0, j = 0;
        StorageChange aChange = default, bChange = default;
        while (true)
        {
            for (; i < a.Length; i++)
            {
                if (TryGetSlotChangeAtIndex(a[i], index, out aChange)) break;
            }
            for (; j < b.Length; j++)
            {
                if (TryGetSlotChangeAtIndex(b[j], index, out bChange)) break;
            }

            bool aMatched = i < a.Length;
            bool bMatched = j < b.Length;
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
    /// Merge per-index source. Caller ensures indices arrive monotonically.
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
            AppendJoined(sb, " storage=[", sorted.AsSpan(), "]");
        }
        if (_storageReads.Count > 0)
        {
            using ArrayPoolListRef<UInt256> sorted = GetSortedStorageReads();
            AppendJoined(sb, " reads=[", sorted.AsSpan(), "]");
        }
        return sb.ToString();
    }

    private static void AppendJoined<T>(StringBuilder sb, string prefix, ReadOnlySpan<T> values, string suffix)
    {
        sb.Append(prefix);
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            if (values[i] is not null) sb.Append(values[i]!.ToString());
        }
        sb.Append(suffix);
    }
}
