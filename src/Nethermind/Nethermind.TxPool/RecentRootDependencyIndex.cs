// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.TxPool;

/// <summary>
/// Records the <see href="https://eips.ethereum.org/EIPS/eip-8272">EIP-8272</see> <c>(storage_key, entry_hash)</c>
/// dependencies and the expiry slot of each pending transaction carrying a <c>recent_root_verify</c> frame.
/// </summary>
/// <remarks>Ordered by expiry, the first <c>current_slot</c> at which a tuple is out of the window, so a slot advance
/// finds the aged-out transactions without walking the pool. A full recheck reads each distinct storage cell once.
/// Mutation and collection share one lock: the pools insert and remove under their own locks, concurrently.</remarks>
internal sealed class RecentRootDependencyIndex
{
    private readonly Lock _lock = new();
    private readonly Dictionary<ValueHash256, Dependencies> _byTx = [];
    private readonly SortedDictionary<ulong, HashSet<ValueHash256>> _byExpiry = [];

    public int Count
    {
        get { lock (_lock) return _byTx.Count; }
    }

    /// <summary>Records the dependencies of <paramref name="tx"/>, replacing any earlier record under its hash.</summary>
    public void Add(Transaction tx)
    {
        if (!tx.SupportsFrames || !FrameTxValidation.TryGetRecentRootTuples(tx, out ReadOnlyMemory<byte> tuples)) return;

        Dependencies dependencies = Dependencies.Of(tx.Hash!, tuples.Span);
        ValueHash256 key = tx.Hash!.ValueHash256;
        lock (_lock)
        {
            RemoveLocked(key);
            _byTx[key] = dependencies;
            if (!_byExpiry.TryGetValue(dependencies.ExpirySlot, out HashSet<ValueHash256>? expiring))
            {
                _byExpiry[dependencies.ExpirySlot] = expiring = [];
            }

            expiring.Add(key);
        }
    }

    public void Remove(Transaction tx)
    {
        if (!tx.SupportsFrames) return;

        lock (_lock) RemoveLocked(tx.Hash!.ValueHash256);
    }

    /// <summary>Drops the record under <paramref name="hash"/>, if any.</summary>
    public void Remove(Hash256 hash)
    {
        lock (_lock) RemoveLocked(hash.ValueHash256);
    }

    /// <summary>Adds every indexed transaction to <paramref name="into"/>.</summary>
    public void CollectAll(List<Hash256> into)
    {
        lock (_lock)
        {
            foreach (Dependencies dependencies in _byTx.Values) into.Add(dependencies.Hash);
        }
    }

    /// <summary>Adds to <paramref name="into"/> the transactions with a tuple out of the window at <paramref name="currentSlot"/>.</summary>
    public void CollectExpired(ulong currentSlot, List<Hash256> into)
    {
        lock (_lock)
        {
            foreach ((ulong expirySlot, HashSet<ValueHash256> expiring) in _byExpiry)
            {
                if (expirySlot > currentSlot) break;

                foreach (ValueHash256 key in expiring) into.Add(_byTx[key].Hash);
            }
        }
    }

    /// <summary>Adds to <paramref name="into"/> the transactions whose tuples fail the age or storage predicate at
    /// <paramref name="currentSlot"/> against <paramref name="state"/>.</summary>
    /// <remarks>Storage is read outside the lock, so pool inserts and removals do not wait on cold trie reads.</remarks>
    public void CollectInvalid(IReadOnlyStateProvider state, ulong currentSlot, List<Hash256> into)
    {
        Dependencies[] snapshot;
        lock (_lock) snapshot = [.. _byTx.Values];

        Dictionary<StorageCell, UInt256> entries = [];
        foreach (Dependencies dependencies in snapshot)
        {
            if (dependencies.ExpirySlot <= currentSlot
                || dependencies.LatestSlot >= currentSlot
                || !AreCommitted(state, dependencies.Entries, entries))
            {
                into.Add(dependencies.Hash);
            }
        }
    }

    private static bool AreCommitted(IReadOnlyStateProvider state, (StorageCell Cell, UInt256 Entry)[] required, Dictionary<StorageCell, UInt256> entries)
    {
        foreach ((StorageCell cell, UInt256 entry) in required)
        {
            if (!entries.TryGetValue(cell, out UInt256 stored))
            {
                state.Get(cell, out stored);
                entries[cell] = stored;
            }

            if (stored != entry) return false;
        }

        return true;
    }

    private void RemoveLocked(ValueHash256 key)
    {
        if (!_byTx.Remove(key, out Dependencies? dependencies)) return;

        HashSet<ValueHash256> expiring = _byExpiry[dependencies.ExpirySlot];
        expiring.Remove(key);
        if (expiring.Count == 0) _byExpiry.Remove(dependencies.ExpirySlot);
    }

    private sealed record Dependencies(Hash256 Hash, (StorageCell Cell, UInt256 Entry)[] Entries, ulong LatestSlot, ulong ExpirySlot)
    {
        public static Dependencies Of(Hash256 hash, ReadOnlySpan<byte> tuples)
        {
            HashSet<(StorageCell, UInt256)> entries = [];
            ulong earliestSlot = ulong.MaxValue;
            ulong latestSlot = 0;
            for (int offset = 0; offset < tuples.Length; offset += Eip8272Constants.RecentRootTupleLength)
            {
                (ValueHash256 sourceId, ulong slot, ValueHash256 root) = RecentRootStore.ReadTuple(tuples.Slice(offset));
                entries.Add((RecentRootStore.ReferenceCell(sourceId, slot), RecentRootStore.EntryHash(sourceId, slot, root).ToUInt256()));
                earliestSlot = Math.Min(earliestSlot, slot);
                latestSlot = Math.Max(latestSlot, slot);
            }

            ulong expirySlot = earliestSlot > ulong.MaxValue - Eip8272Constants.RecentRootLength
                ? ulong.MaxValue
                : earliestSlot + Eip8272Constants.RecentRootLength;
            return new Dependencies(hash, [.. entries], latestSlot, expirySlot);
        }
    }
}
