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
/// Per-account changes from a decoded BAL. Indexed change families are kept sorted by
/// <see cref="IIndexedChange.Index"/> (decoder-validated) and reads binary-search a
/// dense <c>uint[]</c> index lane shared across balance / nonce / code (one allocation,
/// three spans carved by offset). Storage changes are also indexed twice: a hash map for
/// O(1) <see cref="TryGetSlotChanges"/> lookups during EVM execution, and an array sorted
/// by slot key for the cache prewarmer's sorted-merge with <see cref="StorageReads"/>.
/// </summary>
/// <remarks>
/// Immutable after construction; parallel workers read concurrently. Missing entries at the
/// current block-access index fall through to the per-worker parent state in
/// <c>BlockAccessListBasedWorldState</c>.
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
    private readonly AccountIndexLane _lane;

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
        _lane = new AccountIndexLane(balanceChanges, nonceChanges, codeChanges);
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

    public BalanceChange? BalanceChangeAtIndex(uint index) => AccountIndexLane.GetExact(_lane.Balance, BalanceChanges, index);

    public NonceChange? NonceChangeAtIndex(uint index) => AccountIndexLane.GetExact(_lane.Nonce, NonceChanges, index);

    public CodeChange? CodeChangeAtIndex(uint index) => AccountIndexLane.GetExact(_lane.Code, CodeChanges, index);

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
        => AccountIndexLane.TryGetLastBefore(_lane.Balance, BalanceChanges, blockAccessIndex, out BalanceChange last) ? last.Value : null;

    public UInt256? GetNonce(uint blockAccessIndex)
        => AccountIndexLane.TryGetLastBefore(_lane.Nonce, NonceChanges, blockAccessIndex, out NonceChange last) ? last.Value : null;

    public byte[]? GetCode(uint blockAccessIndex)
        => AccountIndexLane.TryGetLastBefore(_lane.Code, CodeChanges, blockAccessIndex, out CodeChange last) ? last.Code : null;

    // The explicit (ValueHash256?) on the null branch matters: ValueHash256 has an implicit
    // conversion operator from Hash256? that returns default(ValueHash256) for a null source,
    // so without the cast C# resolves the conditional's best common type as ValueHash256
    // (non-nullable) and the "null" branch becomes default(ValueHash256) lifted to HasValue=true.
    public ValueHash256? GetCodeHash(uint blockAccessIndex)
        => AccountIndexLane.TryGetLastBefore(_lane.Code, CodeChanges, blockAccessIndex, out CodeChange last) ? last.CodeHash : (ValueHash256?)null;

    public bool TryGetLastBalanceChangeBefore(uint blockAccessIndex, out BalanceChange balanceChange)
        => AccountIndexLane.TryGetLastBefore(_lane.Balance, BalanceChanges, blockAccessIndex, out balanceChange);

    public bool TryGetLastNonceChangeBefore(uint blockAccessIndex, out NonceChange nonceChange)
        => AccountIndexLane.TryGetLastBefore(_lane.Nonce, NonceChanges, blockAccessIndex, out nonceChange);

    public bool TryGetLastCodeChangeBefore(uint blockAccessIndex, out CodeChange codeChange)
        => AccountIndexLane.TryGetLastBefore(_lane.Code, CodeChanges, blockAccessIndex, out codeChange);

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

}
