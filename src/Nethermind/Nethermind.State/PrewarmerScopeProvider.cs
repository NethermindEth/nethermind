// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
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

public class PrewarmerScopeProvider(
    IWorldStateScopeProvider baseProvider,
    PreBlockCaches preBlockCaches,
    ILogManager logManager,
    bool populatePreBlockCache = true,
    CrossBlockCaches? crossBlockCaches = null
) : IWorldStateScopeProvider, IPreBlockCaches
{
    public bool HasRoot(BlockHeader? baseBlock) => baseProvider.HasRoot(baseBlock);

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock) => new ScopeWrapper(baseProvider.BeginScope(baseBlock), preBlockCaches, logManager, populatePreBlockCache, crossBlockCaches, baseBlock);

    public PreBlockCaches? Caches => preBlockCaches;
    public bool IsWarmWorldState => !populatePreBlockCache;

    private sealed class ScopeWrapper : IWorldStateScopeProvider.IScope
    {
        private readonly IWorldStateScopeProvider.IScope baseScope;
        private readonly SeqlockCache<AddressAsKey, Account> preBlockCache;
        private readonly SeqlockCache<StorageCell, byte[]> storageCache;
        private readonly CrossBlockCaches? crossBlockCaches;
        private readonly bool populatePreBlockCache;
        private readonly IMetricObserver _metricObserver = Metrics.PrewarmerGetTime;
        private readonly bool _measureMetric = Metrics.DetailedMetricsEnabled;
        private readonly PrewarmerGetTimeLabels _labels;
        private readonly ILogger _logger;
        private long _writeBatchTime = 0;
        private bool _committed;
        private volatile bool _pendingStorageClear;

        public ScopeWrapper(IWorldStateScopeProvider.IScope baseScope, PreBlockCaches preBlockCaches, ILogManager logManager, bool populatePreBlockCache, CrossBlockCaches? crossBlockCaches, BlockHeader? baseBlock)
        {
            this.baseScope = baseScope;
            preBlockCache = preBlockCaches.StateCache;
            storageCache = preBlockCaches.StorageCache;
            this.crossBlockCaches = crossBlockCaches;
            this.populatePreBlockCache = populatePreBlockCache;
            _labels = populatePreBlockCache ? PrewarmerGetTimeLabels.Prewarmer : PrewarmerGetTimeLabels.NonPrewarmer;
            _logger = logManager.GetClassLogger<ScopeWrapper>();

            // The cross-block caches are only consulted/maintained on the authoritative (main
            // block-processing) scope, identified by populatePreBlockCache == false. On the canonical
            // chain the base block number equals the last committed block, so entries persist; on a
            // reorg they differ and the stale entries must be dropped before reuse.
            if (!populatePreBlockCache && crossBlockCaches is not null)
            {
                long lastCommitted = crossBlockCaches.LastCommittedBlockNumber;
                if (baseBlock is not null && baseBlock.Number != lastCommitted)
                {
                    crossBlockCaches.Clear();
                }
            }
        }

        public void Dispose()
        {
            // A scope disposed without a successful Commit (e.g. an invalid block) may have written
            // speculative entries straight to the cross-block cache; drop them so they cannot leak
            // into the next block's reads.
            if (!_committed && crossBlockCaches is not null)
            {
                crossBlockCaches.Clear();
            }

            if (_measureMetric && _writeBatchTime != 0)
            {
                _metricObserver.Observe(Stopwatch.GetTimestamp() - _writeBatchTime, _labels.WriteBatchToScopeDisposeTime);
            }
            baseScope.Dispose();
        }

        public IWorldStateScopeProvider.ICodeDb CodeDb => baseScope.CodeDb;

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) => new StorageTreeWrapper(
                baseScope.CreateStorageTree(address),
                storageCache,
                address,
                populatePreBlockCache,
                crossBlockCaches?.StorageCache);

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum)
        {
            IWorldStateScopeProvider.IWorldStateWriteBatch batch = baseScope.StartWriteBatch(estimatedAccountNum);

            // Write-through: intercept storage (and account) writes so the cross-block cache reflects
            // the new committed values instead of serving stale ones on the next block.
            if (crossBlockCaches is not null)
            {
                batch = new CacheUpdatingWriteBatch(batch, this, crossBlockCaches);
            }

            if (!_measureMetric)
            {
                return batch;
            }

            _writeBatchTime = Stopwatch.GetTimestamp();
            long sw = Stopwatch.GetTimestamp();
            return new WriteBatchLifetimeMeasurer(
                batch,
                _metricObserver,
                sw,
                populatePreBlockCache);
        }

        internal void BufferStorageClear() => _pendingStorageClear = true;

        public void Commit(long blockNumber)
        {
            if (!_measureMetric)
            {
                baseScope.Commit(blockNumber);
                FinalizeCommit(blockNumber);
                return;
            }

            long sw = Stopwatch.GetTimestamp();
            baseScope.Commit(blockNumber);
            FinalizeCommit(blockNumber);
            _metricObserver.Observe(Stopwatch.GetTimestamp() - sw, _labels.Commit);
        }

        private void FinalizeCommit(long blockNumber)
        {
            if (crossBlockCaches is null) return;

            // CREATE/SELFDESTRUCT that wiped an account's storage cannot be reconciled slot-by-slot,
            // so clear the storage cache wholesale (O(1) epoch bump).
            if (_pendingStorageClear)
            {
                crossBlockCaches.StorageCache.Clear();
            }

            _committed = true;
            crossBlockCaches.LastCommittedBlockNumber = blockNumber;
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
            if (populatePreBlockCache)
            {
                if (preBlockCache.TryGetValue(in addressAsKey, out Account? account))
                {
                    if (_measureMetric) _metricObserver.Observe(Stopwatch.GetTimestamp() - sw, _labels.AddressHit);
                    Metrics.IncrementStateTreeCacheHits();
                }
                else
                {
                    account = GetFromBaseTree(in addressAsKey);
                    preBlockCache.Set(in addressAsKey, account);
                    if (_measureMetric) _metricObserver.Observe(Stopwatch.GetTimestamp() - sw, _labels.AddressMiss);
                }
                return account;
            }
            else
            {
                if (preBlockCache.TryGetValue(in addressAsKey, out Account? account))
                {
                    if (_measureMetric) _metricObserver.Observe(Stopwatch.GetTimestamp() - sw, _labels.AddressHit);
                    baseScope.HintGet(address, account);
                    Metrics.IncrementStateTreeCacheHits();
                }
                else
                {
                    account = GetFromBaseTree(in addressAsKey);
                    if (_measureMetric) _metricObserver.Observe(Stopwatch.GetTimestamp() - sw, _labels.AddressMiss);
                }
                return account;
            }
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
        bool populatePreBlockCache,
        SeqlockCache<StorageCell, byte[]>? crossBlockStorageCache) : IWorldStateScopeProvider.IStorageTree
    {
        private readonly IWorldStateScopeProvider.IStorageTree baseStorageTree = baseStorageTree;
        private readonly SeqlockCache<StorageCell, byte[]> preBlockCache = preBlockCache;
        private readonly SeqlockCache<StorageCell, byte[]>? crossBlockStorageCache = crossBlockStorageCache;
        private readonly Address address = address;
        private readonly bool populatePreBlockCache = populatePreBlockCache;
        private readonly IMetricObserver _metricObserver = Db.Metrics.PrewarmerGetTime;
        private readonly bool _measureMetric = Db.Metrics.DetailedMetricsEnabled;
        private readonly PrewarmerGetTimeLabels _labels = populatePreBlockCache ? PrewarmerGetTimeLabels.Prewarmer : PrewarmerGetTimeLabels.NonPrewarmer;

        public Hash256 RootHash => baseStorageTree.RootHash;

        public byte[] Get(in UInt256 index)
        {
            StorageCell storageCell = new(address, in index); // TODO: Make the dictionary use UInt256 directly
            long sw = _measureMetric ? Stopwatch.GetTimestamp() : 0;
            if (populatePreBlockCache)
            {
                if (preBlockCache.TryGetValue(in storageCell, out byte[] value))
                {
                    if (_measureMetric) _metricObserver.Observe(Stopwatch.GetTimestamp() - sw, _labels.SlotGetHit);
                    Db.Metrics.IncrementStorageTreeCache();
                }
                else
                {
                    value = LoadFromTreeStorage(in storageCell);
                    preBlockCache.Set(in storageCell, value);
                    if (_measureMetric) _metricObserver.Observe(Stopwatch.GetTimestamp() - sw, _labels.SlotGetMiss);
                }
                return value;
            }
            else
            {
                if (preBlockCache.TryGetValue(in storageCell, out byte[] value))
                {
                    if (_measureMetric) _metricObserver.Observe(Stopwatch.GetTimestamp() - sw, _labels.SlotGetHit);
                    Db.Metrics.IncrementStorageTreeCache();
                }
                else if (crossBlockStorageCache is not null && crossBlockStorageCache.TryGetValue(in storageCell, out value))
                {
                    // Served from the cross-block cache — the underlying flat-state/trie read (the
                    // dominant SLOAD cost) is avoided entirely.
                    if (_measureMetric) _metricObserver.Observe(Stopwatch.GetTimestamp() - sw, _labels.SlotGetHit);
                    Db.Metrics.IncrementStorageTreeCache();
                }
                else
                {
                    value = LoadFromTreeStorage(in storageCell);
                    // Seed the cross-block cache from this read so a read-only (SLOAD-only) slot is a
                    // hit on the next block.
                    crossBlockStorageCache?.Set(in storageCell, value);
                    if (_measureMetric) _metricObserver.Observe(Stopwatch.GetTimestamp() - sw, _labels.SlotGetMiss);
                }
                return value;
            }
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

    private class WriteBatchLifetimeMeasurer(IWorldStateScopeProvider.IWorldStateWriteBatch baseWriteBatch, IMetricObserver metricObserver, long startTime, bool populatePreBlockCache) : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        private readonly PrewarmerGetTimeLabels _labels = populatePreBlockCache ? PrewarmerGetTimeLabels.Prewarmer : PrewarmerGetTimeLabels.NonPrewarmer;

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

    /// <summary>
    /// Wraps a write batch so committed storage values are written through to the cross-block cache,
    /// keeping it coherent with the trie rather than serving stale reads on the next block.
    /// </summary>
    private sealed class CacheUpdatingWriteBatch(
        IWorldStateScopeProvider.IWorldStateWriteBatch baseBatch,
        ScopeWrapper scope,
        CrossBlockCaches crossBlockCaches) : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        public void Dispose() => baseBatch.Dispose();

        public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated
        {
            add => baseBatch.OnAccountUpdated += value;
            remove => baseBatch.OnAccountUpdated -= value;
        }

        public void Set(Address key, Account? account) => baseBatch.Set(key, account);

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries)
            => new CacheUpdatingStorageWriteBatch(baseBatch.CreateStorageWriteBatch(key, estimatedEntries), scope, crossBlockCaches.StorageCache, key);
    }

    private sealed class CacheUpdatingStorageWriteBatch(
        IWorldStateScopeProvider.IStorageWriteBatch baseBatch,
        ScopeWrapper scope,
        SeqlockCache<StorageCell, byte[]> crossBlockStorageCache,
        Address address) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        public void Set(in UInt256 index, byte[] value)
        {
            baseBatch.Set(in index, value);
            crossBlockStorageCache.Set(new StorageCell(address, in index), value);
        }

        public void Clear()
        {
            // Self-destruct / account wipe: the slot-level cache entries for this address cannot be
            // reconciled cheaply, so defer a whole-cache clear to commit time.
            baseBatch.Clear();
            scope.BufferStorageClear();
        }

        public void Dispose() => baseBatch.Dispose();
    }
}
