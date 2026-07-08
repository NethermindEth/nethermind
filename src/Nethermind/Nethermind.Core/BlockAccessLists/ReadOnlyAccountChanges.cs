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

/// <summary>
/// Per-account changes from a decoded BAL. Index-keyed change families are stored as plain
/// arrays kept sorted by <see cref="IIndexedChange.Index"/> (the decoder validates ordering),
/// so reads can binary-search via <see cref="System.MemoryExtensions"/>. Storage changes are
/// kept in two parallel structures: a hash map for O(1) <see cref="TryGetSlotChanges"/>
/// lookups (used during EVM execution) and an array sorted by slot key for ordered iteration
/// (used by the cache prewarmer's sorted-merge with <see cref="StorageReads"/>).
/// </summary>
/// <remarks>
/// Instances are immutable after construction: parallel transaction workers read concurrently
/// and any missing entry at the current block-access index falls through to the per-worker
/// parent-state snapshot (see <c>BlockAccessListBasedWorldState</c>).
/// </remarks>
public class ReadOnlyAccountChanges : IEquatable<ReadOnlyAccountChanges>
{
    [JsonConverter(typeof(AddressConverter))]
    public Address Address { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ReadOnlySlotChanges[] StorageChanges { get; }

    /// Slot keys, sorted ascending
    [JsonIgnore]
    public UInt256[] ChangedSlots { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public UInt256[] StorageReads { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public BalanceChange[] BalanceChanges { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public NonceChange[] NonceChanges { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public CodeChange[] CodeChanges { get; }

    private readonly Dictionary<UInt256, ReadOnlySlotChanges>? _storageChanges;
    private readonly HashSet<UInt256>? _storageReadSet;

    public ReadOnlyAccountChanges(
        Address address,
        ReadOnlySlotChanges[] storageChanges,
        UInt256[] storageReads,
        BalanceChange[] balanceChanges,
        NonceChange[] nonceChanges,
        CodeChange[] codeChanges)
    {
        Address = address;
        StorageChanges = storageChanges;
        if (storageChanges.Length > 0)
        {
            _storageChanges = new Dictionary<UInt256, ReadOnlySlotChanges>(storageChanges.Length);
            UInt256[] changedSlots = new UInt256[storageChanges.Length];
            for (int i = 0; i < storageChanges.Length; i++)
            {
                ReadOnlySlotChanges sc = storageChanges[i];
                _storageChanges.Add(sc.Key, sc);
                changedSlots[i] = sc.Key;
            }
            ChangedSlots = changedSlots;
        }
        else
        {
            _storageChanges = null;
            ChangedSlots = [];
        }
        StorageReads = storageReads;
        BalanceChanges = balanceChanges;
        NonceChanges = nonceChanges;
        CodeChanges = codeChanges;
        // Hash-set lookup beats array.Contains() for accounts with many declared reads; allocated
        // lazily to avoid the overhead on accounts that never get queried via IsStorageRead.
        _storageReadSet = storageReads.Length > 4 ? [.. storageReads] : null;
    }

    public ReadOnlyAccountChanges(Address address) : this(address, [], [], [], [], []) { }

    public bool TryGetSlotChanges(UInt256 key, [NotNullWhen(true)] out ReadOnlySlotChanges? slotChanges)
        => _storageChanges.TryGetValueOrNull(key, out slotChanges);

    public bool IsStorageRead(UInt256 slot)
    {
        if (_storageReadSet is not null)
        {
            return _storageReadSet.Contains(slot);
        }

        ReadOnlySpan<UInt256> reads = StorageReads;
        for (int i = 0; i < reads.Length; i++)
        {
            if (reads[i].Equals(slot)) return true;
        }
        return false;
    }

    public BalanceChange? BalanceChangeAtIndex(uint index) => GetExact(BalanceChanges, index);

    public NonceChange? NonceChangeAtIndex(uint index) => GetExact(NonceChanges, index);

    public CodeChange? CodeChangeAtIndex(uint index) => GetExact(CodeChanges, index);

    public bool HasSlotChangesAtIndex(uint index)
    {
        foreach (ReadOnlySlotChanges slotChanges in StorageChanges)
        {
            if (HasExactIndex(slotChanges.Changes, index)) return true;
        }
        return false;
    }

    public IEnumerable<SlotChangeAtIndex> SlotChangesAtIndex(uint index)
    {
        foreach (ReadOnlySlotChanges slotChanges in StorageChanges)
        {
            StorageChange? change = GetExact(slotChanges.Changes, index);
            if (change is not null)
            {
                yield return new SlotChangeAtIndex(slotChanges.Key, change.Value);
            }
        }
    }

    /// <summary>
    /// True iff this account has no balance/nonce/code/slot change at <paramref name="index"/>.
    /// Storage reads are not changes; this only inspects mutating entries.
    /// </summary>
    public bool HasNoChangesAtIndex(uint index)
        => BalanceChangeAtIndex(index) is null
        && NonceChangeAtIndex(index) is null
        && CodeChangeAtIndex(index) is null
        && !HasSlotChangesAtIndex(index);

    /// <summary>
    /// True iff this account has any tx-level mutation declared in the BAL.
    /// </summary>
    [JsonIgnore]
    public bool HasStateChanges
        => BalanceChanges.Length > 0
            || NonceChanges.Length > 0
            || CodeChanges.Length > 0
            || StorageChanges.Length > 0;

    /// <summary>
    /// Most recent balance strictly before <paramref name="blockAccessIndex"/>; null if none.
    /// </summary>
    public UInt256? GetBalance(uint blockAccessIndex)
        => TryGetLastBefore(BalanceChanges, blockAccessIndex, out BalanceChange last) ? last.Value : null;

    public UInt256? GetNonce(uint blockAccessIndex)
        => TryGetLastBefore(NonceChanges, blockAccessIndex, out NonceChange last) ? last.Value : null;

    public byte[]? GetCode(uint blockAccessIndex)
        => TryGetLastBefore(CodeChanges, blockAccessIndex, out CodeChange last) ? last.Code : null;

    // The explicit (ValueHash256?) on the null branch matters: ValueHash256 has an implicit
    // conversion operator from Hash256? that returns default(ValueHash256) for a null source,
    // so without the cast C# resolves the conditional's best common type as ValueHash256
    // (non-nullable) and the "null" branch becomes default(ValueHash256) lifted to HasValue=true.
    public ValueHash256? GetCodeHash(uint blockAccessIndex)
        => TryGetLastBefore(CodeChanges, blockAccessIndex, out CodeChange last) ? last.CodeHash : (ValueHash256?)null;

    public bool TryGetLastBalanceChangeBefore(uint blockAccessIndex, out BalanceChange balanceChange)
        => TryGetLastBefore(BalanceChanges, blockAccessIndex, out balanceChange);

    public bool TryGetLastNonceChangeBefore(uint blockAccessIndex, out NonceChange nonceChange)
        => TryGetLastBefore(NonceChanges, blockAccessIndex, out nonceChange);

    public bool TryGetLastCodeChangeBefore(uint blockAccessIndex, out CodeChange codeChange)
        => TryGetLastBefore(CodeChanges, blockAccessIndex, out codeChange);

    public bool Equals(ReadOnlyAccountChanges? other)
    {
        if (other is null) return false;
        if (Address != other.Address) return false;
        if (StorageChanges.Length != other.StorageChanges.Length) return false;
        if (_storageChanges is not null)
        {
            Dictionary<UInt256, ReadOnlySlotChanges> otherDict = other._storageChanges!;
            foreach (KeyValuePair<UInt256, ReadOnlySlotChanges> kv in _storageChanges)
            {
                if (!otherDict.TryGetValue(kv.Key, out ReadOnlySlotChanges? otherVal) || !kv.Value.Equals(otherVal))
                    return false;
            }
        }
        // Span casts force MemoryExtensions.SequenceEqual (zero-alloc) over LINQ's.
        return ((ReadOnlySpan<UInt256>)StorageReads).SequenceEqual(other.StorageReads)
            && ((ReadOnlySpan<BalanceChange>)BalanceChanges).SequenceEqual(other.BalanceChanges)
            && ((ReadOnlySpan<NonceChange>)NonceChanges).SequenceEqual(other.NonceChanges)
            && ((ReadOnlySpan<CodeChange>)CodeChanges).SequenceEqual(other.CodeChanges);
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

    /// <summary>
    /// Returns the change with <c>Index == index</c> if any; otherwise null.
    /// </summary>
    private static T? GetExact<T>(T[] changes, uint index) where T : struct, IIndexedChange
    {
        if (changes.Length <= 4)
        {
            for (int i = 0; i < changes.Length; i++)
            {
                T candidate = changes[i];
                if (candidate.Index == index) return candidate;
            }

            return null;
        }

        ReadOnlySpan<T> span = changes;
        int idx = span.BinarySearch(new IndexKey<T>(index));
        return idx >= 0 ? span[idx] : null;
    }

    private static bool HasExactIndex<T>(T[] changes, uint index) where T : struct, IIndexedChange
    {
        if (changes.Length <= 4)
        {
            for (int i = 0; i < changes.Length; i++)
            {
                if (changes[i].Index == index) return true;
            }

            return false;
        }

        ReadOnlySpan<T> span = changes;
        return span.BinarySearch(new IndexKey<T>(index)) >= 0;
    }

    /// <summary>
    /// Returns the entry with the largest Index strictly less than <paramref name="blockAccessIndex"/>, or false if none.
    /// </summary>
    private static bool TryGetLastBefore<T>(T[] changes, uint blockAccessIndex, out T last) where T : struct, IIndexedChange
    {
        if (changes.Length <= 4)
        {
            for (int i = changes.Length - 1; i >= 0; i--)
            {
                T candidate = changes[i];
                if (candidate.Index < blockAccessIndex)
                {
                    last = candidate;
                    return true;
                }
            }

            last = default;
            return false;
        }

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
