// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Per-account changes assembled from one or more <see cref="AccountChangesAtIndex"/> via merging.
/// Append-only and ordered by index; uses <see cref="InlineList{T}"/> per change family so the
/// common single-change case avoids the <see cref="List{T}"/> backing-array allocation.
/// </summary>
/// <remarks>
/// Storage changes and storage reads are kept in unsorted <see cref="Dictionary{TKey, TValue}"/> /
/// <see cref="HashSet{T}"/>. EIP-7928 requires slot-sorted output on the wire, but that's paid
/// once per block via <see cref="GetSortedStorageChanges"/> / <see cref="GetSortedStorageReads"/>
/// at encode time rather than O(log n) on every per-tx merge.
/// </remarks>
public class GeneratedAccountChanges(Address address)
{
    [JsonConverter(typeof(AddressConverter))]
    public Address Address { get; } = address;

    private InlineList<BalanceChange> _balanceChanges;
    private InlineList<NonceChange> _nonceChanges;
    private InlineList<CodeChange> _codeChanges;

    private readonly Dictionary<UInt256, GeneratedSlotChanges> _storageChanges = new();
    private readonly HashSet<UInt256> _storageReads = [];

    public int BalanceChangesCount => _balanceChanges.Count;
    public int NonceChangesCount => _nonceChanges.Count;
    public int CodeChangesCount => _codeChanges.Count;

    /// <summary>Non-allocating view over the per-account balance changes. The order is the
    /// merge order, which is monotonically increasing by <see cref="BalanceChange.Index"/>.</summary>
    /// <remarks>
    /// Properties with ref-like return types (e.g. <see cref="ReadOnlySpan{T}"/>) confuse the
    /// JSON source generator (SYSLIB1225); the explicit <see cref="JsonIgnoreAttribute"/> opts
    /// these out of metadata generation entirely.
    /// </remarks>
    [JsonIgnore]
    public ReadOnlySpan<BalanceChange> BalanceChangesSpan => _balanceChanges.AsSpan();
    [JsonIgnore]
    public ReadOnlySpan<NonceChange> NonceChangesSpan => _nonceChanges.AsSpan();
    [JsonIgnore]
    public ReadOnlySpan<CodeChange> CodeChangesSpan => _codeChanges.AsSpan();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyCollection<GeneratedSlotChanges> StorageChanges => _storageChanges.Values;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyCollection<UInt256> StorageReads => _storageReads;

    /// <summary>
    /// Slot-key-sorted snapshot of the per-slot changes. Used by the RLP encoder, which requires
    /// storage_changes in ascending-by-slot-key order per EIP-7928.
    /// </summary>
    public GeneratedSlotChanges[] GetSortedStorageChanges()
    {
        GeneratedSlotChanges[] sorted = [.. _storageChanges.Values];
        Array.Sort(sorted, static (a, b) => a.Key.CompareTo(b.Key));
        return sorted;
    }

    /// <summary>
    /// Slot-key-sorted snapshot of the storage reads. Used by the RLP encoder, which requires
    /// storage_reads in ascending order per EIP-7928.
    /// </summary>
    public UInt256[] GetSortedStorageReads()
    {
        UInt256[] sorted = [.. _storageReads];
        Array.Sort(sorted);
        return sorted;
    }

    public bool TryGetSlotChanges(UInt256 key, [NotNullWhen(true)] out GeneratedSlotChanges? slotChanges)
        => _storageChanges.TryGetValue(key, out slotChanges);

    /// <summary>Per-family change lists are appended in monotonically increasing <c>Index</c>
    /// order during <see cref="Merge"/>, so we binary-search via <see cref="IndexKey{T}"/>
    /// rather than scanning. Mirrors <see cref="ReadOnlyAccountChanges"/>.</summary>
    public BalanceChange? BalanceChangeAtIndex(uint index) => GetExact(_balanceChanges.AsSpan(), index);
    public NonceChange? NonceChangeAtIndex(uint index) => GetExact(_nonceChanges.AsSpan(), index);
    public CodeChange? CodeChangeAtIndex(uint index) => GetExact(_codeChanges.AsSpan(), index);

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

    /// <summary>Structural equality of the per-index slice of this account against the suggested
    /// (decoded) account. Address is not compared (callers ensure they're matched). Walks both
    /// sides without allocating: per-slot lookup is an O(log n) binary search via
    /// <see cref="IndexKey{T}"/>, and the suggested side's slot list is sorted-by-key so the
    /// generated side is paired with it via a binary search per slot.</summary>
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
        // Walk the generated side (unsorted) and binary-search the suggested side
        // (ReadOnlySlotChanges[], sorted-by-key — decoder validates). For every slot with a
        // change at `index` on the generated side, the suggested side must contain the same slot
        // with an equal change at that index. Symmetry is enforced by counting matches on both
        // sides: if counts agree and every generated match has a paired suggested match, the
        // sets are equal (slot keys are unique within each side).
        ReadOnlySlotChanges[] otherSlots = other.StorageChanges;

        int genMatches = 0;
        foreach (GeneratedSlotChanges slot in _storageChanges.Values)
        {
            if (!TryGetSlotChangeAtIndex(slot, index, out StorageChange genChange)) continue;
            int idx = BinarySearchByKey(otherSlots, slot.Key);
            if (idx < 0) return false;
            if (!TryGetSlotChangeAtIndex(otherSlots[idx], index, out StorageChange othChange)) return false;
            if (!genChange.Equals(othChange)) return false;
            genMatches++;
        }

        int othMatches = 0;
        for (int j = 0; j < otherSlots.Length; j++)
        {
            if (TryGetSlotChangeAtIndex(otherSlots[j], index, out _)) othMatches++;
        }

        return genMatches == othMatches;
    }

    /// <summary>Binary-search <paramref name="sortedSlots"/> (sorted ascending by Key) for the
    /// entry with the given <paramref name="key"/>; returns its index or -1.</summary>
    private static int BinarySearchByKey(ReadOnlySlotChanges[] sortedSlots, UInt256 key)
    {
        int low = 0;
        int high = sortedSlots.Length - 1;
        while (low <= high)
        {
            int mid = low + ((high - low) >> 1);
            int cmp = sortedSlots[mid].Key.CompareTo(key);
            if (cmp == 0) return mid;
            if (cmp < 0) low = mid + 1;
            else high = mid - 1;
        }
        return -1;
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

    /// <summary>O(log n) lookup of the entry with <c>Index == index</c> over a span kept sorted
    /// by index (the merge contract on <see cref="GeneratedAccountChanges"/> guarantees that).</summary>
    private static T? GetExact<T>(ReadOnlySpan<T> changes, uint index) where T : struct, IIndexedChange
    {
        int idx = changes.BinarySearch(new IndexKey<T>(index));
        return idx >= 0 ? changes[idx] : null;
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

    /// <summary>Merge the per-index source into this accumulator. Caller must ensure indices arrive monotonically.</summary>
    public void Merge(AccountChangesAtIndex other)
    {
        if (other.BalanceChange is not null)
        {
            _balanceChanges.Add(other.BalanceChange.Value);
        }
        if (other.NonceChange is not null)
        {
            _nonceChanges.Add(other.NonceChange.Value);
        }
        if (other.CodeChange is not null)
        {
            _codeChanges.Add(other.CodeChange.Value);
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
            // only add reads where there's no existing change for the slot
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
        AppendIfAny(sb, "balance", _balanceChanges.AsSpan());
        AppendIfAny(sb, "nonce", _nonceChanges.AsSpan());
        AppendIfAny(sb, "code", _codeChanges.AsSpan());
        if (_storageChanges.Count > 0) sb.Append($" storage=[{string.Join(", ", (object[])GetSortedStorageChanges())}]");
        if (_storageReads.Count > 0) sb.Append($" reads=[{string.Join(", ", GetSortedStorageReads())}]");
        return sb.ToString();
    }

    private static void AppendIfAny<T>(StringBuilder sb, string label, ReadOnlySpan<T> items) where T : struct
    {
        if (items.Length == 0) return;
        sb.Append(' ').Append(label).Append("=[");
        for (int i = 0; i < items.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(items[i]);
        }
        sb.Append(']');
    }
}
