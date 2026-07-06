// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Metric;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.State;

internal class PrewarmerGetTimeLabels(bool isPrewarmer)
{
    public static PrewarmerGetTimeLabels Prewarmer { get; } = new(true);
    public static PrewarmerGetTimeLabels NonPrewarmer { get; } = new(false);

    public PrewarmerGetTimeLabel Commit { get; } = new("commit", isPrewarmer);
    public PrewarmerGetTimeLabel WriteBatchToScopeDisposeTime { get; } = new("write_batch_to_dispose", isPrewarmer);
    public PrewarmerGetTimeLabel UpdateRootHash { get; } = new("update_root_hash", isPrewarmer);
    public PrewarmerGetTimeLabel AddressHit { get; } = new("address_hit", isPrewarmer);
    public PrewarmerGetTimeLabel AddressMiss { get; } = new("address_miss", isPrewarmer);
    public PrewarmerGetTimeLabel SlotGetHit { get; } = new("slot_get_hit", isPrewarmer);
    public PrewarmerGetTimeLabel SlotGetMiss { get; } = new("slot_get_miss", isPrewarmer);
    public PrewarmerGetTimeLabel WriteBatchLifetime { get; } = new("write_batch_lifetime", isPrewarmer);
}

/// <summary>
/// Decorates a scope provider with the shared <see cref="PreBlockCaches"/>. A miss always backfills;
/// relies on the driver clearing the caches between blocks (see <c>BranchProcessor</c>).
/// </summary>
/// <param name="isPrewarmer">
/// True for read-only populator envs (prewarmer, parallel-worker parent readers); false for the
/// read-write main world state. Only effect: on a cache hit a consumer seeds the scope-local cache
/// via <c>HintGet</c> (for its later commit); a populator does not.
/// </param>
public class PrewarmerScopeProvider(
    IWorldStateScopeProvider baseProvider,
    PreBlockCaches preBlockCaches,
    ILogManager logManager,
    bool isPrewarmer = true
) : IWorldStateScopeProvider, IPreBlockCaches
{
    public bool HasRoot(BlockHeader? baseBlock) => baseProvider.HasRoot(baseBlock);

    public bool SupportsConcurrentScopes => baseProvider.SupportsConcurrentScopes;

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock) => new ScopeWrapper(baseProvider, baseBlock, preBlockCaches, logManager, isPrewarmer);

    public PreBlockCaches? Caches => preBlockCaches;
    public bool IsWarmWorldState => !isPrewarmer;

    private sealed class ScopeWrapper(IWorldStateScopeProvider baseProvider, BlockHeader? baseBlock, PreBlockCaches preBlockCaches, ILogManager logManager, bool isPrewarmer) : IWorldStateScopeProvider.IScope
    {
        private readonly IWorldStateScopeProvider.IScope baseScope = baseProvider.BeginScope(baseBlock);
        private readonly SeqlockCache<AddressAsKey, Account> preBlockCache = preBlockCaches.StateCache;
        private readonly SeqlockCache<StorageCell, byte[]> storageCache = preBlockCaches.StorageCache;
        private readonly bool isPrewarmer = isPrewarmer;
        private readonly IMetricObserver _metricObserver = Metrics.PrewarmerGetTime;
        private readonly bool _measureMetric = Metrics.DetailedMetricsEnabled;
        private readonly PrewarmerGetTimeLabels _labels = isPrewarmer ? PrewarmerGetTimeLabels.Prewarmer : PrewarmerGetTimeLabels.NonPrewarmer;
        private readonly ILogger _logger = logManager.GetClassLogger<ScopeWrapper>();
        private long _writeBatchTime = 0;

        // The prefetcher needs an isolated read scope over the same parent; only providers whose
        // scopes can coexist (flat's pooled snapshot bundles) support that. The trie store's scope
        // is a global gate that must not be nested mid-block.
        private readonly bool _stridePrefetchEnabled = !isPrewarmer && baseProvider.SupportsConcurrentScopes;

        // Per contract per block; bounded so a block touching many contracts cannot accumulate
        // reader threads.
        private const int MaxStridePrefetchers = 4;

        // Reader threads issue blocking, latency-bound storage reads, so we run more than one per
        // core (2×CPU) to hide individual RocksDB fetch latency, capped at 32. The budget is shared
        // across the concurrently engaged prefetchers rather than granted per prefetcher, so a block
        // striding several contracts stays within one bounded thread set instead of 2×CPU threads
        // per contract.
        private static readonly int PrefetcherReaderConcurrency =
            Math.Max(1, Math.Min(2 * Environment.ProcessorCount, 32) / MaxStridePrefetchers);

        private readonly ConcurrentDictionary<AddressAsKey, StorageStridePrefetcher> _stridePrefetchers = new();
        private readonly CancellationTokenSource _prefetchCts = new();
        private readonly Lock _prefetchScopeLock = new();
        private IWorldStateScopeProvider.IScope? _prefetchScope;

        public void Dispose()
        {
            if (_measureMetric && _writeBatchTime != 0)
            {
                _metricObserver.Observe(Stopwatch.GetTimestamp() - _writeBatchTime, _labels.WriteBatchToScopeDisposeTime);
            }

            // Joins reader threads and releases their private scope.
            StopStridePrefetchers();
            _prefetchCts.Dispose();

            baseScope.Dispose();
        }

        public IWorldStateScopeProvider.ICodeDb CodeDb => baseScope.CodeDb;

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) => new StorageTreeWrapper(
                baseScope.CreateStorageTree(address),
                storageCache,
                address,
                isPrewarmer,
                _stridePrefetchEnabled ? GetOrCreateStridePrefetcher(address) : null);

        private StorageStridePrefetcher? GetOrCreateStridePrefetcher(Address address)
        {
            // Past the scope's first block (token cancelled at flush/commit) a prefetcher could
            // never engage; skip the detector entirely instead of feeding dead instances.
            if (_prefetchCts.IsCancellationRequested) return null;

            AddressAsKey key = address;
            if (_stridePrefetchers.TryGetValue(key, out StorageStridePrefetcher? existing)) return existing;

            // Only prefetchers still reading count against the cap. A broken one has stopped issuing
            // reads but stays in the map so its (exited) readers are still joined before the shared
            // scope is disposed; excluding it here keeps a broken slot from locking out a later
            // striding contract for the rest of the block. Scanning is bounded (≤ MaxStridePrefetchers
            // live entries plus a few broken ones) and only runs once the fast Count check trips.
            if (_stridePrefetchers.Count >= MaxStridePrefetchers && CountActiveStridePrefetchers() >= MaxStridePrefetchers)
            {
                return null;
            }

            // The readers must NOT touch this wrapper's base scope: its storage trees are memoized
            // per address, so they would share the live tree the executing thread reads and (at the
            // block-end flush) writes through, bypassing the reader-exclusion gates the backend
            // applies to its own background readers. A separate scope over the same parent gives
            // them an isolated, parent-state-only view; it is opened lazily on engagement so blocks
            // without a striding contract pay nothing.
            return _stridePrefetchers.GetOrAdd(
                key,
                k => new StorageStridePrefetcher(
                    () => GetOrCreatePrefetchScope().CreateStorageTree(k.Value),
                    storageCache,
                    k.Value,
                    _prefetchCts.Token,
                    PrefetcherReaderConcurrency));
        }

        private int CountActiveStridePrefetchers()
        {
            int active = 0;
            foreach (KeyValuePair<AddressAsKey, StorageStridePrefetcher> kv in _stridePrefetchers)
            {
                if (!kv.Value.IsBroken) active++;
            }
            return active;
        }

        private IWorldStateScopeProvider.IScope GetOrCreatePrefetchScope()
        {
            lock (_prefetchScopeLock)
            {
                return _prefetchScope ??= baseProvider.BeginScope(baseBlock);
            }
        }

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum)
        {
            // The batch is about to land this block's writes in the live scope, after which
            // parent-state prefetches are no longer useful; stop the readers here, mirroring how
            // the flat scope cancels its own background warmers around write batches.
            StopStridePrefetchers();

            if (!_measureMetric)
            {
                return baseScope.StartWriteBatch(estimatedAccountNum);
            }

            _writeBatchTime = Stopwatch.GetTimestamp();
            long sw = Stopwatch.GetTimestamp();
            return new WriteBatchLifetimeMeasurer(
                baseScope.StartWriteBatch(estimatedAccountNum),
                _metricObserver,
                sw,
                isPrewarmer);
        }

        public void Commit(long blockNumber)
        {
            // Prefetched values are only valid for this block's parent state; a reader surviving
            // into the next block would repopulate the freshly cleared cache with stale values.
            // Join here, strictly inside the block lifecycle.
            StopStridePrefetchers();

            if (!_measureMetric)
            {
                baseScope.Commit(blockNumber);
                return;
            }

            long sw = Stopwatch.GetTimestamp();
            baseScope.Commit(blockNumber);
            _metricObserver.Observe(Stopwatch.GetTimestamp() - sw, _labels.Commit);
        }

        private void StopStridePrefetchers()
        {
            // Unconditional: the scope's parent anchor is only valid for the first block it
            // processes, and sync batches push many blocks through one scope. Once anything has
            // flushed or committed here, later blocks must not engage against the stale anchor —
            // even when no prefetcher was created yet (a storage-free first block would otherwise
            // leave the token live). Cancelling synchronously here is what makes stragglers refuse
            // to repopulate the cache after the block moves on; only the reader join is deferred.
            _prefetchCts.Cancel();

            List<Task>? readers = null;
            if (!_stridePrefetchers.IsEmpty)
            {
                foreach (KeyValuePair<AddressAsKey, StorageStridePrefetcher> kv in _stridePrefetchers)
                {
                    Task[] prefetcherReaders = kv.Value.StopAndGetReaders();
                    if (prefetcherReaders.Length > 0)
                    {
                        (readers ??= []).AddRange(prefetcherReaders);
                    }
                }
                _stridePrefetchers.Clear();
            }

            IWorldStateScopeProvider.IScope? scope;
            lock (_prefetchScopeLock)
            {
                scope = _prefetchScope;
                _prefetchScope = null;
            }

            if (scope is null) return; // Nothing engaged: no private scope was ever opened.

            if (readers is null)
            {
                // Readers already exited (or none were live); disposing the scope cannot race them.
                scope.Dispose();
                return;
            }

            // Join the readers and release their private scope on a background continuation. A
            // synchronous join would stall block-end on the tail latency of an in-flight,
            // uncancellable storage read — exactly on the striding blocks this targets. The token is
            // already cancelled, so no straggler can publish into the next block's cache; deferring
            // only delays disposing the readers' isolated scope until they have all returned.
            IWorldStateScopeProvider.IScope scopeToDispose = scope;
            Task.WhenAll(readers).ContinueWith(
                static (_, state) =>
                {
                    try
                    {
                        ((IWorldStateScopeProvider.IScope)state!).Dispose();
                    }
                    catch
                    {
                        // Best-effort: the isolated scope is reached here only after all its readers
                        // returned. A disposal that races provider/harness teardown must not surface
                        // as a faulted, unobserved task.
                    }
                },
                scopeToDispose,
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }

        public Hash256 RootHash => baseScope.RootHash;

        public void UpdateRootHash()
        {
            if (!_measureMetric)
            {
                baseScope.UpdateRootHash();
                return;
            }

            long sw = Stopwatch.GetTimestamp();
            baseScope.UpdateRootHash();
            _metricObserver.Observe(Stopwatch.GetTimestamp() - sw, _labels.UpdateRootHash);
        }

        public Account? Get(Address address)
        {
            AddressAsKey addressAsKey = address;
            long sw = _measureMetric ? Stopwatch.GetTimestamp() : 0;
            if (preBlockCache.TryGetValue(in addressAsKey, out Account? account))
            {
                if (_measureMetric) _metricObserver.Observe(Stopwatch.GetTimestamp() - sw, _labels.AddressHit);
                // Consumers seed the scope-local cache on a hit for their later commit; populators don't.
                if (!isPrewarmer) baseScope.HintGet(address, account);
                Metrics.IncrementStateTreeCacheHits();
            }
            else
            {
                account = GetFromBaseTree(in addressAsKey);
                // Backfill so other readers reuse this resolve; SeqlockCache.Set is safe under concurrent writers.
                preBlockCache.Set(in addressAsKey, account);
                if (_measureMetric) _metricObserver.Observe(Stopwatch.GetTimestamp() - sw, _labels.AddressMiss);
            }
            return account;
        }

        public void HintGet(Address address, Account? account) => baseScope.HintGet(address, account);

        public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null)
        {
            sink ??= new CacheSink(preBlockCache, storageCache);
            return baseScope.HintBal(bal, sink);
        }

        private sealed class CacheSink(
            SeqlockCache<AddressAsKey, Account> stateCache,
            SeqlockCache<StorageCell, byte[]> storageCache
        ) : IWorldStateScopeProvider.IAsyncBalReaderSink
        {
            public void OnAccountRead(Address address, Account? account)
            {
                AddressAsKey key = address;
                stateCache.Set(in key, account);
            }

            public void OnStorageRead(in StorageCell storageCell, byte[] value)
                => storageCache.Set(in storageCell, value);

            public bool StillNeeded(Address address, out Account? account)
            {
                AddressAsKey key = address;
                return !stateCache.TryGetValue(in key, out account);
            }

            public bool StillNeeded(in StorageCell storageCell)
                => !storageCache.TryGetValue(in storageCell, out _);
        }

        private Account? GetFromBaseTree(in AddressAsKey address) => baseScope.Get(address);
    }

    private sealed class StorageTreeWrapper(
        IWorldStateScopeProvider.IStorageTree baseStorageTree,
        SeqlockCache<StorageCell, byte[]> preBlockCache,
        Address address,
        bool isPrewarmer,
        StorageStridePrefetcher? stridePrefetcher = null) : IWorldStateScopeProvider.IStorageTree
    {
        private readonly IWorldStateScopeProvider.IStorageTree baseStorageTree = baseStorageTree;
        private readonly SeqlockCache<StorageCell, byte[]> preBlockCache = preBlockCache;
        private readonly Address address = address;
        private readonly bool isPrewarmer = isPrewarmer;
        private readonly IMetricObserver _metricObserver = Db.Metrics.PrewarmerGetTime;
        private readonly bool _measureMetric = Db.Metrics.DetailedMetricsEnabled;
        private readonly PrewarmerGetTimeLabels _labels = isPrewarmer ? PrewarmerGetTimeLabels.Prewarmer : PrewarmerGetTimeLabels.NonPrewarmer;

        public Hash256 RootHash => baseStorageTree.RootHash;

        public byte[] Get(in UInt256 index)
        {
            stridePrefetcher?.OnRead(in index);

            StorageCell storageCell = new(address, in index); // TODO: Make the dictionary use UInt256 directly
            long sw = _measureMetric ? Stopwatch.GetTimestamp() : 0;
            if (preBlockCache.TryGetValue(in storageCell, out byte[] value))
            {
                if (_measureMetric) _metricObserver.Observe(Stopwatch.GetTimestamp() - sw, _labels.SlotGetHit);
                Db.Metrics.IncrementStorageTreeCache();
            }
            else
            {
                value = LoadFromTreeStorage(in storageCell);
                // Backfill so other readers reuse this resolve; SeqlockCache.Set is safe under concurrent writers.
                preBlockCache.Set(in storageCell, value);
                if (_measureMetric) _metricObserver.Observe(Stopwatch.GetTimestamp() - sw, _labels.SlotGetMiss);
            }
            return value;
        }

        public void HintSet(in UInt256 index, byte[]? value) => baseStorageTree.HintSet(in index, value);

        private byte[] LoadFromTreeStorage(in StorageCell storageCell)
        {
            Db.Metrics.IncrementStorageTreeReads();

            return !storageCell.IsHash
                ? baseStorageTree.Get(storageCell.Index)
                : baseStorageTree.Get(storageCell.Hash);
        }

        public byte[] Get(in ValueHash256 hash) =>
            // Not a critical path. so we just forward for simplicity
            baseStorageTree.Get(in hash);
    }

    private class WriteBatchLifetimeMeasurer(IWorldStateScopeProvider.IWorldStateWriteBatch baseWriteBatch, IMetricObserver metricObserver, long startTime, bool isPrewarmer) : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        private readonly PrewarmerGetTimeLabels _labels = isPrewarmer ? PrewarmerGetTimeLabels.Prewarmer : PrewarmerGetTimeLabels.NonPrewarmer;

        public void Dispose()
        {
            baseWriteBatch.Dispose();
            metricObserver.Observe(Stopwatch.GetTimestamp() - startTime, _labels.WriteBatchLifetime);
        }

        public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated
        {
            add => baseWriteBatch.OnAccountUpdated += value;
            remove => baseWriteBatch.OnAccountUpdated -= value;
        }

        public void Set(Address key, Account? account) => baseWriteBatch.Set(key, account);

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries) => baseWriteBatch.CreateStorageWriteBatch(key, estimatedEntries);
    }
}
