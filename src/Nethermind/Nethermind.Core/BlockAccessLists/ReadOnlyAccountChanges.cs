// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Per-account changes from a decoded BAL. Index-keyed change families are stored as plain
/// arrays kept sorted by <see cref="IIndexedChange.Index"/> (the decoder validates ordering).
/// Reads (<see cref="GetBalance"/>, <see cref="GetNonce"/>, <see cref="GetCode"/>,
/// <see cref="BalanceChangeAtIndex"/>, etc.) binary-search a parallel <c>uint[]</c> index lane
/// built once at construction, rather than walking the fat change structs themselves —
/// 16 indices per cacheline vs ~1, which compounds on the hot accounts that accumulate many
/// tx-level changes per block. Storage changes are kept in two parallel structures: a hash map
/// for O(1) <see cref="TryGetSlotChanges"/> lookups (used during EVM execution) and an array
/// sorted by slot key for ordered iteration (used by the cache prewarmer's sorted-merge with
/// <see cref="StorageReads"/>).
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

    private readonly Dictionary<UInt256, ReadOnlySlotChanges> _storageChanges;
    private readonly HashSet<UInt256>? _storageReadSet;

    // Parallel-to-{Balance,Nonce,Code}Changes arrays of Index values. Drive the binary searches
    // without reading the full change struct on every comparison.
    private readonly uint[] _balanceIndices;
    private readonly uint[] _nonceIndices;
    private readonly uint[] _codeIndices;

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
        _storageChanges = new Dictionary<UInt256, ReadOnlySlotChanges>(storageChanges.Length);
        UInt256[] changedSlots = new UInt256[storageChanges.Length];
        for (int i = 0; i < storageChanges.Length; i++)
        {
            ReadOnlySlotChanges sc = storageChanges[i];
            _storageChanges.Add(sc.Key, sc);
            changedSlots[i] = sc.Key;
        }
        ChangedSlots = changedSlots;
        StorageReads = storageReads;
        BalanceChanges = balanceChanges;
        NonceChanges = nonceChanges;
        CodeChanges = codeChanges;
        _balanceIndices = ExtractIndices<BalanceChange>(balanceChanges);
        _nonceIndices = ExtractIndices<NonceChange>(nonceChanges);
        _codeIndices = ExtractIndices<CodeChange>(codeChanges);
        // Hash-set lookup beats array.Contains() for accounts with many declared reads; allocated
        // lazily to avoid the overhead on accounts that never get queried via IsStorageRead.
        _storageReadSet = storageReads.Length > 4 ? [.. storageReads] : null;
    }

    public ReadOnlyAccountChanges(Address address) : this(address, [], [], [], [], []) { }

    public bool TryGetSlotChanges(UInt256 key, [NotNullWhen(true)] out ReadOnlySlotChanges? slotChanges)
        => _storageChanges.TryGetValue(key, out slotChanges);

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

    public BalanceChange? BalanceChangeAtIndex(uint index) => GetExact(_balanceIndices, BalanceChanges, index);

    public NonceChange? NonceChangeAtIndex(uint index) => GetExact(_nonceIndices, NonceChanges, index);

    public CodeChange? CodeChangeAtIndex(uint index) => GetExact(_codeIndices, CodeChanges, index);

    public bool HasSlotChangesAtIndex(uint index)
    {
        foreach (ReadOnlySlotChanges slotChanges in StorageChanges)
        {
            if (slotChanges.HasAtIndex(index)) return true;
        }
        return false;
    }

    public IEnumerable<SlotChangeAtIndex> SlotChangesAtIndex(uint index)
    {
        foreach (ReadOnlySlotChanges slotChanges in StorageChanges)
        {
            if (slotChanges.TryGetAtIndex(index, out StorageChange change))
            {
                yield return new SlotChangeAtIndex(slotChanges.Key, change);
            }
        }
    }

    /// <summary>True iff this account has no balance/nonce/code/slot change at <paramref name="index"/>.
    /// Storage reads are not changes; this only inspects mutating entries.</summary>
    public bool HasNoChangesAtIndex(uint index)
        => BalanceChangeAtIndex(index) is null
        && NonceChangeAtIndex(index) is null
        && CodeChangeAtIndex(index) is null
        && !HasSlotChangesAtIndex(index);

    /// <summary>True iff this account has any tx-level mutation declared in the BAL. Used by
    /// <c>BlockAccessListBasedWorldState.GetAccountChanges</c> to enumerate the addresses that
    /// will be visibly modified by the block. Computed; not serialised.</summary>
    [JsonIgnore]
    public bool HasStateChanges
        => BalanceChanges.Length > 0
            || NonceChanges.Length > 0
            || CodeChanges.Length > 0
            || _storageChanges.Count > 0;

    /// <summary>Most recent balance strictly before <paramref name="blockAccessIndex"/>; null if none.</summary>
    public UInt256? GetBalance(uint blockAccessIndex)
        => TryGetLastBefore(_balanceIndices, BalanceChanges, blockAccessIndex, out BalanceChange last) ? last.Value : null;

    public UInt256? GetNonce(uint blockAccessIndex)
        => TryGetLastBefore(_nonceIndices, NonceChanges, blockAccessIndex, out NonceChange last) ? last.Value : null;

    public byte[]? GetCode(uint blockAccessIndex)
        => TryGetLastBefore(_codeIndices, CodeChanges, blockAccessIndex, out CodeChange last) ? last.Code : null;

    // The explicit (ValueHash256?) on the null branch matters: ValueHash256 has an implicit
    // conversion operator from Hash256? that returns default(ValueHash256) for a null source,
    // so without the cast C# resolves the conditional's best common type as ValueHash256
    // (non-nullable) and the "null" branch becomes default(ValueHash256) lifted to HasValue=true.
    public ValueHash256? GetCodeHash(uint blockAccessIndex)
        => TryGetLastBefore(_codeIndices, CodeChanges, blockAccessIndex, out CodeChange last) ? last.CodeHash : (ValueHash256?)null;

    public bool TryGetLastBalanceChangeBefore(uint blockAccessIndex, out BalanceChange balanceChange)
        => TryGetLastBefore(_balanceIndices, BalanceChanges, blockAccessIndex, out balanceChange);

    public bool TryGetLastNonceChangeBefore(uint blockAccessIndex, out NonceChange nonceChange)
        => TryGetLastBefore(_nonceIndices, NonceChanges, blockAccessIndex, out nonceChange);

    public bool TryGetLastCodeChangeBefore(uint blockAccessIndex, out CodeChange codeChange)
        => TryGetLastBefore(_codeIndices, CodeChanges, blockAccessIndex, out codeChange);

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
        // MemoryExtensions.SequenceEqual on ReadOnlySpan<T>: zero-alloc, no iterator,
        // not LINQ (see coding-style.md). Implicit array->span conversion suffices.
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

    /// <summary>Returns the change with <c>Index == index</c> if any; otherwise null. Searches the
    /// dense <paramref name="indices"/> lane and indexes into the parallel <paramref name="values"/>
    /// array, so each comparison loads 4 bytes instead of a full <typeparamref name="T"/> struct.</summary>
    private static T? GetExact<T>(uint[] indices, T[] values, uint index) where T : struct
    {
        int idx = ((ReadOnlySpan<uint>)indices).BinarySearch(index);
        return idx >= 0 ? values[idx] : null;
    }

    /// <summary>Returns the entry with the largest Index strictly less than
    /// <paramref name="blockAccessIndex"/>, or <c>false</c> if none. Same lane shape as
    /// <see cref="GetExact{T}"/>.</summary>
    private static bool TryGetLastBefore<T>(uint[] indices, T[] values, uint blockAccessIndex, out T last) where T : struct
    {
        int idx = ((ReadOnlySpan<uint>)indices).BinarySearch(blockAccessIndex);
        // (idx if found, ~idx otherwise) is the position of the first entry with Index >= target;
        // the last strictly-before is one step earlier.
        int lastBefore = (idx >= 0 ? idx : ~idx) - 1;
        if (lastBefore < 0)
        {
            last = default;
            return false;
        }
        last = values[lastBefore];
        return true;
    }

    private static uint[] ExtractIndices<T>(T[] changes) where T : struct, IIndexedChange
    {
        if (changes.Length == 0) return [];
        uint[] indices = new uint[changes.Length];
        for (int i = 0; i < changes.Length; i++)
        {
            indices[i] = changes[i].Index;
        }
        return indices;
    }
}
