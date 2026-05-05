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
/// Append-only and ordered by index; uses simple <see cref="List{T}"/> per change family because
/// merge contributions arrive sorted, so no <see cref="SortedList{TKey, TValue}"/> is needed.
/// </summary>
public class GeneratedAccountChanges(Address address)
{
    [JsonConverter(typeof(AddressConverter))]
    public Address Address { get; } = address;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<BalanceChange> BalanceChanges { get; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<NonceChange> NonceChanges { get; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<CodeChange> CodeChanges { get; } = [];

    private readonly SortedDictionary<UInt256, GeneratedSlotChanges> _storageChanges
        = new(GenericComparer.GetOptimized<UInt256>());
    private readonly SortedSet<UInt256> _storageReads
        = new(GenericComparer.GetOptimized<UInt256>());

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyCollection<GeneratedSlotChanges> StorageChanges => _storageChanges.Values;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyCollection<UInt256> StorageReads => _storageReads;

    public bool TryGetSlotChanges(UInt256 key, [NotNullWhen(true)] out GeneratedSlotChanges? slotChanges)
        => _storageChanges.TryGetValue(key, out slotChanges);

    /// <summary>Per-family change lists are appended in monotonically increasing <c>Index</c>
    /// order during <see cref="Merge"/>, so we binary-search via <see cref="IndexKey{T}"/>
    /// rather than scanning. Mirrors <see cref="ReadOnlyAccountChanges"/>.</summary>
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

    /// <summary>Structural equality of the per-index slice of this account against the suggested
    /// (decoded) account. Address is not compared (callers ensure they're matched). Walks both
    /// sides without allocating: per-slot lookup is an O(log n) binary search via
    /// <see cref="IndexKey{T}"/>; both storage maps iterate in sorted-by-slot-key order so
    /// account-level slot pairing is a single linear merge-walk.</summary>
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
        // Both sides iterate slots in sorted-by-key order:
        //   - this:  SortedDictionary<UInt256, GeneratedSlotChanges>.Values
        //   - other: ReadOnlySlotChanges[] (decoder produces sorted)
        // Walk in lockstep, skipping slots that have no change at this index on either side.
        // Concrete struct enumerator type avoids the boxing the IEnumerable/IEnumerator
        // interface would force; Dispose chain bottoms out at empty TreeSet.Enumerator.Dispose
        // so we skip the `using` for clarity — the manual MoveNext/Current control here is
        // intentional, and there's no resource to release.
        SortedDictionary<UInt256, GeneratedSlotChanges>.ValueCollection.Enumerator a
            = _storageChanges.Values.GetEnumerator();
        ReadOnlySlotChanges[] b = other.StorageChanges;
        int j = 0;
        bool aHas = a.MoveNext();
        StorageChange aChange = default, bChange = default;

        while (true)
        {
            // Advance each side to the next slot that has a change at `index`, capturing the
            // change in the same binary search rather than re-searching after presence is known.
            bool aMatched = false;
            while (aHas)
            {
                if (TryGetSlotChangeAtIndex(a.Current, index, out aChange)) { aMatched = true; break; }
                aHas = a.MoveNext();
            }
            bool bMatched = false;
            while (j < b.Length)
            {
                if (TryGetSlotChangeAtIndex(b[j], index, out bChange)) { bMatched = true; break; }
                j++;
            }

            if (!aMatched && !bMatched) return true;
            if (aMatched != bMatched) return false;
            if (a.Current.Key != b[j].Key) return false;
            if (!aChange.Equals(bChange)) return false;

            aHas = a.MoveNext();
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

    /// <summary>Merge the per-index source into this accumulator. Caller must ensure indices arrive monotonically.</summary>
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
        if (BalanceChanges.Count > 0) sb.Append($" balance=[{string.Join(", ", BalanceChanges)}]");
        if (NonceChanges.Count > 0) sb.Append($" nonce=[{string.Join(", ", NonceChanges)}]");
        if (CodeChanges.Count > 0) sb.Append($" code=[{string.Join(", ", CodeChanges)}]");
        if (_storageChanges.Count > 0) sb.Append($" storage=[{string.Join(", ", _storageChanges.Values)}]");
        if (_storageReads.Count > 0) sb.Append($" reads=[{string.Join(", ", _storageReads)}]");
        return sb.ToString();
    }
}
