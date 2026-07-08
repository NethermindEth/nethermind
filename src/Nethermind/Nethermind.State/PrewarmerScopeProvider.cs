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
/// read-write main world state. On a cache hit a consumer seeds the scope-local cache via
/// <c>HintGet</c> (for its later commit); a populator does not. A populator additionally pushes trie
/// warm-up hints for first-resolved accounts to <see cref="PreBlockCaches.TrieHintSink"/>.
/// </param>
/// <param name="registerTrieHintSink">
/// When true (consumer only), each scope registers its base scope as the block's
/// <see cref="IPrewarmTrieHintSink"/> so populator envs can warm the commit-path trie ahead of time.
/// </param>
public class PrewarmerScopeProvider(
    IWorldStateScopeProvider baseProvider,
    PreBlockCaches preBlockCaches,
    ILogManager logManager,
    bool isPrewarmer = true,
    bool registerTrieHintSink = false
) : IWorldStateScopeProvider, IPreBlockCaches
{
    public bool HasRoot(BlockHeader? baseBlock) => baseProvider.HasRoot(baseBlock);

    public bool SupportsConcurrentScopes => baseProvider.SupportsConcurrentScopes;

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock, LocalMetrics metrics)
    {
        IWorldStateScopeProvider.IScope scope = baseProvider.BeginScope(baseBlock, metrics);
        if (!isPrewarmer && registerTrieHintSink)
        {
            // Null for backends that do not support trie warm-up hints (e.g. non-flat layouts).
            preBlockCaches.TrieHintSink = scope as IPrewarmTrieHintSink;
        }
        return new ScopeWrapper(baseProvider, baseBlock, scope, preBlockCaches, logManager, isPrewarmer, registerTrieHintSink, metrics);
    }

    public PreBlockCaches? Caches => preBlockCaches;
    public bool IsWarmWorldState => !isPrewarmer;

    private sealed class ScopeWrapper(
        IWorldStateScopeProvider baseProvider,
        BlockHeader? baseBlock,
        IWorldStateScopeProvider.IScope baseScope,
        PreBlockCaches preBlockCaches,
        ILogManager logManager,
        bool isPrewarmer,
        bool registerTrieHintSink,
        LocalMetrics metrics) : IWorldStateScopeProvider.IScope
    {
        private readonly IWorldStateScopeProvider baseProvider = baseProvider;
        private readonly BlockHeader? baseBlock = baseBlock;
        private readonly IWorldStateScopeProvider.IScope baseScope = baseScope;
        private readonly PreBlockCaches preBlockCaches = preBlockCaches;
        private readonly SeqlockCache<AddressAsKey, Account> preBlockCache = preBlockCaches.StateCache;
        private readonly SeqlockCache<StorageCell, byte[]> storageCache = preBlockCaches.StorageCache;
        private readonly bool isPrewarmer = isPrewarmer;
        private readonly LocalMetrics _metrics = metrics;
        private readonly IMetricObserver _metricObserver = Metrics.PrewarmerGetTime;
        private readonly bool _measureMetric = Metrics.DetailedMetricsEnabled;
        private readonly PrewarmerGetTimeLabels _labels = isPrewarmer ? PrewarmerGetTimeLabels.Prewarmer : PrewarmerGetTimeLabels.NonPrewarmer;
        private readonly ILogger _logger = logManager.GetClassLogger<ScopeWrapper>();
        private long _writeBatchTime = 0;

        // Per contract per block; bounded so a block touching many contracts cannot accumulate
        // reader threads.
        private const int MaxStridePrefetchers = 4;

        // Keep read-ahead pressure conservative: the p99 target is tail latency, and aggressive
        // prefetching can compete with the executing thread for RocksDB I/O on dense blocks.
        private const int PrefetcherReaderConcurrency = 1;

        private readonly bool _stridePrefetchEnabled = !isPrewarmer && baseProvider.SupportsConcurrentScopes;
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
            // Unregister before the base scope is torn down so no new warm hints target a disposing scope.
            if (!isPrewarmer && registerTrieHintSink) preBlockCaches.TrieHintSink = null;

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
                _metrics,
                _stridePrefetchEnabled ? GetOrCreateStridePrefetcher(address) : null);

        private StorageStridePrefetcher? GetOrCreateStridePrefetcher(Address address)
        {
            if (_prefetchCts.IsCancellationRequested) return null;

            AddressAsKey key = address;
            if (_stridePrefetchers.TryGetValue(key, out StorageStridePrefetcher? existing)) return existing;

            if (_stridePrefetchers.Count >= MaxStridePrefetchers && CountActiveStridePrefetchers() >= MaxStridePrefetchers)
            {
                return null;
            }

            // Readers must use a private parent-anchored scope, not the live scope. The live flat
            // scope is main-thread-owned and starts serving in-block values once write batches land;
            // prefetching must populate only parent-state cache entries for this block.
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
                return _prefetchScope ??= baseProvider.BeginScope(baseBlock, new LocalMetrics());
            }
        }

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum)
        {
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

        public void Commit(ulong blockNumber)
        {
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
            // Prefetched values are only valid for this block's parent state. Cancelling synchronously
            // prevents a straggler from repopulating a cache after the block has moved on; joining
            // readers and disposing their private scope can happen off the block-processing thread.
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

            if (scope is null) return;

            if (readers is null)
            {
                scope.Dispose();
                return;
            }

            Task.WhenAll(readers).ContinueWith(
                static (completed, state) =>
                {
                    (IWorldStateScopeProvider.IScope Scope, ILogger Logger) args =
                        ((IWorldStateScopeProvider.IScope Scope, ILogger Logger))state!;
                    try
                    {
                        _ = completed.Exception;
                        args.Scope.Dispose();
                    }
                    catch (Exception ex)
                    {
                        if (args.Logger.IsDebug) args.Logger.Debug($"Stride prefetch scope disposal failed: {ex}");
                    }
                },
                (scope, _logger),
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
                _metrics.IncrementStateTreeCacheHits();
            }
            else
            {
                account = GetFromBaseTree(in addressAsKey);
                // Backfill so other readers reuse this resolve; SeqlockCache.Set is safe under concurrent writers.
                preBlockCache.Set(in addressAsKey, account);
                // First resolve of this account in the block: give the main scope a head start on
                // warming its account-trie path for the final commit (deduplicated by the sink).
                if (isPrewarmer) preBlockCaches.TrieHintSink?.HintAccountWarm(address);
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
        LocalMetrics metrics,
        StorageStridePrefetcher? stridePrefetcher = null) : IWorldStateScopeProvider.IStorageTree
    {
        private readonly IWorldStateScopeProvider.IStorageTree baseStorageTree = baseStorageTree;
        private readonly SeqlockCache<StorageCell, byte[]> preBlockCache = preBlockCache;
        private readonly Address address = address;
        private readonly bool isPrewarmer = isPrewarmer;
        private readonly LocalMetrics _metrics = metrics;
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
                _metrics.IncrementStorageTreeCache();
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
            _metrics.IncrementStorageTreeReads();

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
