// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Sparse;

namespace Nethermind.State.Flat.ScopeProvider;

/// <summary>
/// One persistent CPU worker for incremental state and storage-root work. Scheduling progress stays
/// independent of the shared thread pool; parallel storage-root frontiers may still enlist it.
/// </summary>
internal sealed class SparseTrieRootWorker : IDisposable
{
    private readonly ConcurrentQueue<FlatSparseTrieSession> _jobs = [];
    private readonly SemaphoreSlim _workAvailable = new(0);
    private readonly Thread _thread;
    private int _activeSchedulers;
    private int _disposed;

    public SparseTrieRootWorker()
    {
        _thread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "SparseTrieRoot"
        };
        _thread.Start();
    }

    public bool TrySchedule(FlatSparseTrieSession session)
    {
        if (Volatile.Read(ref _disposed) != 0) return false;

        Interlocked.Increment(ref _activeSchedulers);
        try
        {
            if (Volatile.Read(ref _disposed) != 0) return false;

            _jobs.Enqueue(session);
            _workAvailable.Release();
            return true;
        }
        finally
        {
            Interlocked.Decrement(ref _activeSchedulers);
        }
    }

    private void WorkerLoop()
    {
        while (true)
        {
            _workAvailable.Wait();
            while (_jobs.TryDequeue(out FlatSparseTrieSession? session))
            {
                session.RunStateWorker();
            }

            if (Volatile.Read(ref _disposed) != 0) return;
        }
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;

        SpinWait spinWait = new();
        while (Volatile.Read(ref _activeSchedulers) != 0) spinWait.SpinOnce();
        _workAvailable.Release();
        _thread.Join();
        while (_jobs.TryDequeue(out _)) { }
        _workAvailable.Dispose();
    }
}

internal static class SparseTrieRetention
{
    private const int SparseBudgetDivisor = 4;

    public static bool Enabled { get; } =
        Environment.GetEnvironmentVariable("NETHERMIND_SPARSE_TRIE_RETENTION") != "0";

    // Off by default: streaming committed values to the single root worker makes the block thread
    // wait for it at the commit barrier, which costs more than the overlap saves (fusaka 1k, flat:
    // -20.7% AVG with it off before the warm-up contention fix, -3.4% after). Opt in to measure.
    public static bool ConcurrentRootEnabled { get; } =
        Environment.GetEnvironmentVariable("NETHERMIND_SPARSE_TRIE_CONCURRENT_ROOT") == "1";

    public static ulong GetSparseBudget(ulong totalBudget) => totalBudget / SparseBudgetDivisor;

    public static long GetNodeCacheBudget(ulong totalBudget) =>
        (long)(totalBudget - GetSparseBudget(totalBudget));
}

/// <summary>
/// One warm sparse-trie generation retained between blocks: the state trie plus a warm storage
/// trie per account touched in a recent block, anchored at <see cref="StateRoot"/>. Owns the
/// tries; <see cref="Dispose"/> releases every arena.
/// </summary>
internal sealed class RetainedGeneration(
    ValueHash256 stateRoot,
    SparseTrie stateTrie,
    ConcurrentDictionary<ValueHash256, SparseTrie> storageTries,
    FlatTrieNodeReaderContext? readerContext) : IDisposable
{
    public ValueHash256 StateRoot { get; } = stateRoot;
    public SparseTrie StateTrie { get; } = stateTrie;
    public ConcurrentDictionary<ValueHash256, SparseTrie> StorageTries { get; } = storageTries;
    public FlatTrieNodeReaderContext? ReaderContext { get; } = readerContext;

    /// <summary>Total pool-rented arena bytes across the state trie and every storage trie.</summary>
    public long RentedBytes { get; private set; } = SumBytes(stateTrie, storageTries, static trie => trie.RentedBytes);

    /// <summary>Arena bytes made unreachable by mutation across the whole generation.</summary>
    public long DeadBytes { get; private set; } = SumBytes(stateTrie, storageTries, static trie => trie.DeadBytes);

    private static long SumBytes(
        SparseTrie stateTrie,
        ConcurrentDictionary<ValueHash256, SparseTrie> storageTries,
        Func<SparseTrie, long> selector)
    {
        long total = selector(stateTrie);
        foreach (KeyValuePair<ValueHash256, SparseTrie> kv in storageTries)
        {
            total += selector(kv.Value);
        }

        return total;
    }

    public void Dispose()
    {
        StateTrie.Dispose();
        foreach (KeyValuePair<ValueHash256, SparseTrie> kv in StorageTries)
        {
            kv.Value.Dispose();
        }

        StorageTries.Clear();
        RentedBytes = 0;
        DeadBytes = 0;
    }
}

/// <summary>
/// Holds accepted main-processing sparse-trie generations within a shared memory budget. Exact
/// parent-state-root checkout is destructive so a scope exclusively mutates its candidate;
/// alternate parent generations remain available for competing candidates and shallow reorgs.
/// </summary>
/// <remarks>
/// Admission is all-or-nothing within the existing trie-cache envelope: a generation whose rented
/// arena bytes exceed <see cref="_budgetBytes"/>, or whose dead bytes exceed a quarter of its
/// rented bytes, is dropped whole rather than compacted, and the next scope rebuilds cold. The
/// sparse arena receives one quarter of <c>TrieCacheMemoryBudget</c>; <see cref="TrieNodeCache"/>
/// uses the remaining three quarters, so retained generations do not double-count the configured
/// cache envelope.
/// Only the main-processing provider owns a cache; read-only, historical, resettable, and tracing
/// providers never feed it.
/// </remarks>
internal sealed class FlatSparseTrieCache(ulong budgetBytes) : IDisposable
{
    private readonly long _budgetBytes = (long)budgetBytes;
    private readonly Lock _lock = new();
    private readonly List<RetainedGeneration> _held = [];
    private readonly Lazy<SparseTrieRootWorker> _rootWorker = new();
    private long _heldBytes;

    public SparseTrieRootWorker RootWorker => _rootWorker.Value;

    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public long Rejections { get; private set; }

    /// <summary>
    /// Hands out the newest generation anchored at <paramref name="parentStateRoot"/>, removing it
    /// from the cache; the caller then owns and mutates it. A mismatch or empty cache returns
    /// <c>null</c> (rebuild cold) and leaves every held generation in place.
    /// </summary>
    public RetainedGeneration? TryCheckout(in ValueHash256 parentStateRoot)
    {
        lock (_lock)
        {
            for (int i = _held.Count - 1; i >= 0; i--)
            {
                RetainedGeneration generation = _held[i];
                if (generation.StateRoot == parentStateRoot)
                {
                    _held.RemoveAt(i);
                    _heldBytes -= generation.RentedBytes;
                    Hits++;
                    return generation;
                }
            }

            Misses++;
            return null;
        }
    }

    /// <summary>
    /// Offers a committed generation for retention. Accepted only within the envelope and under the
    /// dead-byte fragmentation limit; otherwise it is disposed. A newer generation for the same
    /// root replaces the older one; oldest alternate roots are evicted until the total fits.
    /// </summary>
    public void Admit(RetainedGeneration generation)
    {
        long rented = generation.RentedBytes;
        List<RetainedGeneration>? evicted = null;
        bool rejected = rented > _budgetBytes || generation.DeadBytes > rented / 4;
        lock (_lock)
        {
            int existingIndex = _held.IndexOf(generation);
            if (existingIndex >= 0)
            {
                _held.RemoveAt(existingIndex);
                _held.Add(generation);
                return;
            }

            if (rejected)
            {
                Rejections++;
            }
            else
            {
                for (int i = _held.Count - 1; i >= 0; i--)
                {
                    RetainedGeneration held = _held[i];
                    if (held.StateRoot == generation.StateRoot)
                    {
                        _held.RemoveAt(i);
                        _heldBytes -= held.RentedBytes;
                        (evicted ??= []).Add(held);
                    }
                }

                _held.Add(generation);
                _heldBytes += rented;

                while (_heldBytes > _budgetBytes)
                {
                    RetainedGeneration oldest = _held[0];
                    _held.RemoveAt(0);
                    _heldBytes -= oldest.RentedBytes;
                    (evicted ??= []).Add(oldest);
                }
            }
        }

        if (rejected)
        {
            generation.Dispose();
            return;
        }

        if (evicted is not null)
        {
            foreach (RetainedGeneration held in evicted)
            {
                held.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (_rootWorker.IsValueCreated) _rootWorker.Value.Dispose();

        RetainedGeneration[] held;
        lock (_lock)
        {
            held = [.. _held];
            _held.Clear();
            _heldBytes = 0;
        }

        foreach (RetainedGeneration generation in held)
        {
            generation.Dispose();
        }
    }
}
