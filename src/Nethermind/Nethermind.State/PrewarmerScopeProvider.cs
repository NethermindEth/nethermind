// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Metric;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;

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
    bool populatePreBlockCache = true,
    CrossBlockCaches? crossBlockCaches = null
) : IWorldStateScopeProvider, IPreBlockCaches
{
    public bool HasRoot(BlockHeader? baseBlock) => baseProvider.HasRoot(baseBlock);

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock) => new ScopeWrapper(baseProvider.BeginScope(baseBlock), preBlockCaches, populatePreBlockCache, crossBlockCaches, baseBlock);

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
        private bool _committed;
        private volatile bool _pendingStorageClear;

        public ScopeWrapper(IWorldStateScopeProvider.IScope baseScope, PreBlockCaches preBlockCaches, bool populatePreBlockCache, CrossBlockCaches? crossBlockCaches, BlockHeader? baseBlock)
        {
            this.baseScope = baseScope;
            preBlockCache = preBlockCaches.StateCache;
            storageCache = preBlockCaches.StorageCache;
            this.crossBlockCaches = crossBlockCaches;
            this.populatePreBlockCache = populatePreBlockCache;
            _labels = populatePreBlockCache ? PrewarmerGetTimeLabels.Prewarmer : PrewarmerGetTimeLabels.NonPrewarmer;

            // Only clear cross-block storage cache on reorg (discontinuity). On the canonical chain,
            // baseBlock.Number == LastCommittedBlockNumber, so caches persist across blocks.
            // During a reorg, baseBlock.Number < LastCommittedBlockNumber, so we clear stale entries.
            if (!populatePreBlockCache && crossBlockCaches is not null)
            {
                long lastCommitted = crossBlockCaches.LastCommittedBlockNumber;
                if (baseBlock is not null && baseBlock.Number != lastCommitted)
                {
                    crossBlockCaches.StorageCache.Clear();
                }
            }
        }

        private long _writeBatchTime = 0;

        public void Dispose()
        {
            // If commit didn't happen (e.g., block was invalid), epoch-bump to invalidate
            // any entries written directly to the cross-block cache during this scope.
            if (!_committed && crossBlockCaches is not null)
            {
                crossBlockCaches.StorageCache.Clear();
            }

            if (_measureMetric && _writeBatchTime != 0)
            {
                _metricObserver.Observe(Stopwatch.GetTimestamp() - _writeBatchTime, _labels.WriteBatchToScopeDisposeTime);
            }
            baseScope.Dispose();
        }

        public IWorldStateScopeProvider.ICodeDb CodeDb => baseScope.CodeDb;

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address)
        {
            return new StorageTreeWrapper(
                baseScope.CreateStorageTree(address),
                storageCache,
                address,
                populatePreBlockCache,
                crossBlockCaches?.StorageCache);
        }

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum)
        {
            IWorldStateScopeProvider.IWorldStateWriteBatch batch = baseScope.StartWriteBatch(estimatedAccountNum);

            // Write-through: intercept storage writes to keep cross-block cache up to date.
            if (crossBlockCaches is not null)
            {
                batch = new CacheUpdatingWriteBatch(batch, this, crossBlockCaches.StorageCache);
            }

            if (!_measureMetric) return batch;

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

            // CREATE/SELFDESTRUCT cleared storage — epoch-bump invalidates all entries.
            if (_pendingStorageClear)
                crossBlockCaches.StorageCache.Clear();

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

        private Account? GetFromBaseTree(in AddressAsKey address)
        {
            return baseScope.Get(address);
        }
    }

    private sealed class StorageTreeWrapper : IWorldStateScopeProvider.IStorageTree
    {
        private readonly IWorldStateScopeProvider.IStorageTree baseStorageTree;
        private readonly SeqlockCache<StorageCell, byte[]> preBlockCache;
        private readonly SeqlockCache<StorageCell, byte[], LargeCacheSets>? crossBlockStorageCache;
        private readonly Address address;
        private readonly bool populatePreBlockCache;
        private readonly IMetricObserver _metricObserver = Db.Metrics.PrewarmerGetTime;
        private readonly bool _measureMetric = Db.Metrics.DetailedMetricsEnabled;
        private readonly PrewarmerGetTimeLabels _labels;

        public StorageTreeWrapper(
            IWorldStateScopeProvider.IStorageTree baseStorageTree,
            SeqlockCache<StorageCell, byte[]> preBlockCache,
            Address address,
            bool populatePreBlockCache,
            SeqlockCache<StorageCell, byte[], LargeCacheSets>? crossBlockStorageCache)
        {
            this.baseStorageTree = baseStorageTree;
            this.preBlockCache = preBlockCache;
            this.crossBlockStorageCache = crossBlockStorageCache;
            this.address = address;
            this.populatePreBlockCache = populatePreBlockCache;
            _labels = populatePreBlockCache ? PrewarmerGetTimeLabels.Prewarmer : PrewarmerGetTimeLabels.NonPrewarmer;
        }

        public Hash256 RootHash => baseStorageTree.RootHash;

        public byte[] Get(in UInt256 index)
        {
            StorageCell storageCell = new StorageCell(address, in index); // TODO: Make the dictionary use UInt256 directly
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
                    baseStorageTree.HintGet(in index, value);
                    Db.Metrics.IncrementStorageTreeCache();
                }
                else if (crossBlockStorageCache is not null && crossBlockStorageCache.TryGetValue(in storageCell, out value))
                {
                    // Skip HintGet — the value came from the cross-block cache, not the trie.
                    // HintGet triggers WarmUpSlot (bloom filter + XxHash64 + potential trie path
                    // warming), which is wasted work since the value is already cached.
                    if (_measureMetric) _metricObserver.Observe(Stopwatch.GetTimestamp() - sw, _labels.SlotGetHit);
                    Db.Metrics.IncrementStorageTreeCache();
                }
                else
                {
                    value = LoadFromTreeStorage(in storageCell);
                    // Seed cross-block cache from trie reads — hot slots that are read but
                    // not written (SLOAD-only) are captured for subsequent blocks.
                    crossBlockStorageCache?.Set(in storageCell, value);
                    if (_measureMetric) _metricObserver.Observe(Stopwatch.GetTimestamp() - sw, _labels.SlotGetMiss);
                }
                return value;
            }
        }

        public void HintGet(in UInt256 index, byte[]? value) => baseStorageTree.HintGet(in index, value);

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
    /// Wraps write batch to write-through storage updates to the cross-block cache.
    /// </summary>
    private sealed class CacheUpdatingWriteBatch(
        IWorldStateScopeProvider.IWorldStateWriteBatch baseBatch,
        ScopeWrapper scope,
        SeqlockCache<StorageCell, byte[], LargeCacheSets> crossBlockStorageCache) : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        public void Dispose() => baseBatch.Dispose();

        public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated
        {
            add => baseBatch.OnAccountUpdated += value;
            remove => baseBatch.OnAccountUpdated -= value;
        }

        public void Set(Address key, Account? account) => baseBatch.Set(key, account);

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries)
        {
            IWorldStateScopeProvider.IStorageWriteBatch baseBatchStorage = baseBatch.CreateStorageWriteBatch(key, estimatedEntries);
            return new CacheUpdatingStorageWriteBatch(baseBatchStorage, scope, crossBlockStorageCache, key);
        }
    }

    private sealed class CacheUpdatingStorageWriteBatch(
        IWorldStateScopeProvider.IStorageWriteBatch baseBatch,
        ScopeWrapper scope,
        SeqlockCache<StorageCell, byte[], LargeCacheSets> crossBlockStorageCache,
        Address address) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        public void Set(in UInt256 index, byte[] value)
        {
            baseBatch.Set(in index, value);
            crossBlockStorageCache.Set(new StorageCell(address, in index), value);
        }

        public void Clear()
        {
            baseBatch.Clear();
            scope.BufferStorageClear();
        }

        public void Dispose() => baseBatch.Dispose();
    }

}
