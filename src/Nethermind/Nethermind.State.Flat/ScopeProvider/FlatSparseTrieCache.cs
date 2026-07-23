// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Sparse;

namespace Nethermind.State.Flat.ScopeProvider;

/// <summary>
/// One warm sparse-trie generation retained between blocks: the state trie plus a warm storage
/// trie per account touched in a recent block, anchored at <see cref="StateRoot"/>. Owns the
/// tries; <see cref="Dispose"/> releases every arena.
/// </summary>
internal sealed class RetainedGeneration(
    ValueHash256 stateRoot,
    SparseTrie stateTrie,
    Dictionary<Hash256AsKey, SparseTrie> storageTries) : IDisposable
{
    public ValueHash256 StateRoot { get; } = stateRoot;
    public SparseTrie StateTrie { get; } = stateTrie;
    public Dictionary<Hash256AsKey, SparseTrie> StorageTries { get; } = storageTries;

    /// <summary>Total pool-rented arena bytes across the state trie and every storage trie.</summary>
    public long RentedBytes
    {
        get
        {
            long total = StateTrie.RentedBytes;
            foreach (KeyValuePair<Hash256AsKey, SparseTrie> kv in StorageTries)
            {
                total += kv.Value.RentedBytes;
            }

            return total;
        }
    }

    /// <summary>Arena bytes made unreachable by mutation across the whole generation.</summary>
    public long DeadBytes
    {
        get
        {
            long total = StateTrie.DeadBytes;
            foreach (KeyValuePair<Hash256AsKey, SparseTrie> kv in StorageTries)
            {
                total += kv.Value.DeadBytes;
            }

            return total;
        }
    }

    public void Dispose()
    {
        StateTrie.Dispose();
        foreach (KeyValuePair<Hash256AsKey, SparseTrie> kv in StorageTries)
        {
            kv.Value.Dispose();
        }

        StorageTries.Clear();
    }
}

/// <summary>
/// Holds at most one accepted main-processing sparse-trie generation, checked out destructively on
/// an exact parent-state-root match so a scope exclusively mutates its candidate and an aborted
/// block leaves the cache cold. Parent mismatch is a miss; the scope then rebuilds from a blinded
/// committed root.
/// </summary>
/// <remarks>
/// Admission is all-or-nothing within the existing trie-cache envelope: a generation whose rented
/// arena bytes exceed <see cref="_budgetBytes"/>, or whose dead bytes exceed a quarter of its
/// rented bytes, is dropped whole rather than compacted, and the next scope rebuilds cold. The
/// budget is currently a standalone cap reusing <c>TrieCacheMemoryBudget</c>; folding it into one
/// shared counter with <see cref="TrieNodeCache"/> is a follow-up once retention proves out.
/// Only the main-processing provider owns a cache; read-only, historical, resettable, and tracing
/// providers never feed it.
/// </remarks>
internal sealed class FlatSparseTrieCache(ulong budgetBytes) : IDisposable
{
    private readonly long _budgetBytes = (long)budgetBytes;
    private readonly Lock _lock = new();
    private RetainedGeneration? _held;

    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public long Rejections { get; private set; }

    /// <summary>
    /// Hands out the held generation when it is anchored at <paramref name="parentStateRoot"/>,
    /// removing it from the cache; the caller then owns and mutates it. A mismatch or empty cache
    /// returns <c>null</c> (rebuild cold) and leaves any held generation in place.
    /// </summary>
    public RetainedGeneration? TryCheckout(in ValueHash256 parentStateRoot)
    {
        lock (_lock)
        {
            if (_held is not null && _held.StateRoot == parentStateRoot)
            {
                RetainedGeneration generation = _held;
                _held = null;
                Hits++;
                return generation;
            }

            Misses++;
            return null;
        }
    }

    /// <summary>
    /// Offers a committed generation for retention. Accepted only within the envelope and under the
    /// dead-byte fragmentation limit; otherwise it is disposed. Replaces (and disposes) any
    /// generation held from a scope that never checked it back out.
    /// </summary>
    public void Admit(RetainedGeneration generation)
    {
        long rented = generation.RentedBytes;
        if (rented > _budgetBytes || generation.DeadBytes > rented / 4)
        {
            generation.Dispose();
            lock (_lock) { Rejections++; }
            return;
        }

        RetainedGeneration? previous;
        lock (_lock)
        {
            previous = _held;
            _held = generation;
        }

        previous?.Dispose();
    }

    public void Dispose()
    {
        RetainedGeneration? held;
        lock (_lock)
        {
            held = _held;
            _held = null;
        }

        held?.Dispose();
    }
}
