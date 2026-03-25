// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
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
        private static readonly SeqlockCache<AddressAsKey, Account>.ValueFactory<ScopeWrapper> _getFromBaseTree = static (in AddressAsKey address, ScopeWrapper self) => self.GetFromBaseTree(in address);
        private readonly IMetricObserver _metricObserver = Metrics.PrewarmerGetTime;
        private readonly bool _measureMetric = Metrics.DetailedMetricsEnabled;
        private readonly PrewarmerGetTimeLabels _labels;

        // Pending cross-block storage cache updates, promoted only on successful CommitTree.
        // ConcurrentQueue because storage batches may run in parallel (UpdateRootHashesMultiThread).
        private ConcurrentQueue<(StorageCell Key, byte[] Value)>? _pendingStorageUpdates;
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

            // Buffer writes for the cross-block caches. Promotion happens in Commit()
            // (called from CommitTree after successful block processing), so a failed block
            // never pollutes the cross-block caches.
            if (crossBlockCaches is not null)
            {
                batch = new CacheUpdatingWriteBatch(batch, this);
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

        internal void BufferStorageUpdate(in StorageCell cell, byte[] value)
        {
            ConcurrentQueue<(StorageCell, byte[])> queue = LazyInitializer.EnsureInitialized(ref _pendingStorageUpdates)!;
            queue.Enqueue((cell, value));
        }

        internal void BufferStorageClear()
        {
            _pendingStorageClear = true;
        }

        public void Commit(long blockNumber)
        {
            if (!_measureMetric)
            {
                baseScope.Commit(blockNumber);
                PromoteToCrossBlockCaches(blockNumber);
                return;
            }

            long sw = Stopwatch.GetTimestamp();
            baseScope.Commit(blockNumber);
            PromoteToCrossBlockCaches(blockNumber);
            _metricObserver.Observe(Stopwatch.GetTimestamp() - sw, _labels.Commit);
        }

        /// <summary>
        /// Flushes buffered writes to the cross-block caches. Called only after successful
        /// CommitTree, so speculative entries from failed/invalid blocks are never promoted.
        /// If any storage Clear() was seen (CREATE/SELFDESTRUCT), the storage cache is
        /// epoch-bumped first, then all committed values are re-written so the cache ends
        /// up with only correct post-block values.
        /// </summary>
        private void PromoteToCrossBlockCaches(long blockNumber)
        {
            if (crossBlockCaches is null) return;

            if (_pendingStorageClear)
            {
                // A CREATE or SELFDESTRUCT cleared storage for at least one address.
                // Epoch-bump invalidates all cross-block storage entries, then we
                // re-seed with the correct committed values from this block.
                crossBlockCaches.StorageCache.Clear();
            }

            if (_pendingStorageUpdates is not null)
            {
                SeqlockCache<StorageCell, byte[]> storageCacheRef = crossBlockCaches.StorageCache;
                while (_pendingStorageUpdates.TryDequeue(out (StorageCell Key, byte[] Value) entry))
                {
                    storageCacheRef.Set(in entry.Key, entry.Value);
                }
            }

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
                long priorReads = Metrics.ThreadLocalStateTreeReads;
                Account? account = preBlockCache.GetOrAdd(in addressAsKey, this, _getFromBaseTree);

                if (Metrics.ThreadLocalStateTreeReads == priorReads)
                {
                    if (_measureMetric) _metricObserver.Observe(Stopwatch.GetTimestamp() - sw, _labels.AddressHit);
                    Metrics.IncrementStateTreeCacheHits();
                }
                else
                {
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
        private readonly SeqlockCache<StorageCell, byte[]>? crossBlockStorageCache;
        private readonly Address address;
        private readonly bool populatePreBlockCache;
        private static readonly SeqlockCache<StorageCell, byte[]>.ValueFactory<StorageTreeWrapper> _loadFromTreeStorage = static (in StorageCell cell, StorageTreeWrapper self) => self.LoadFromTreeStorage(in cell);
        private readonly IMetricObserver _metricObserver = Db.Metrics.PrewarmerGetTime;
        private readonly bool _measureMetric = Db.Metrics.DetailedMetricsEnabled;
        private readonly PrewarmerGetTimeLabels _labels;

        public StorageTreeWrapper(
            IWorldStateScopeProvider.IStorageTree baseStorageTree,
            SeqlockCache<StorageCell, byte[]> preBlockCache,
            Address address,
            bool populatePreBlockCache,
            SeqlockCache<StorageCell, byte[]>? crossBlockStorageCache)
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
                long priorReads = Db.Metrics.ThreadLocalStorageTreeReads;

                byte[] value = preBlockCache.GetOrAdd(in storageCell, this, _loadFromTreeStorage);

                if (Db.Metrics.ThreadLocalStorageTreeReads == priorReads)
                {
                    if (_measureMetric) _metricObserver.Observe(Stopwatch.GetTimestamp() - sw, _labels.SlotGetHit);
                    // Read from Concurrent Cache
                    Db.Metrics.IncrementStorageTreeCache();
                }
                else
                {
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
                    if (_measureMetric) _metricObserver.Observe(Stopwatch.GetTimestamp() - sw, _labels.SlotGetHit);
                    baseStorageTree.HintGet(in index, value);
                    Db.Metrics.IncrementStorageTreeCache();
                }
                else
                {
                    value = LoadFromTreeStorage(in storageCell);
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
    /// Wraps write batch to buffer storage updates for cross-block cache promotion.
    /// Writes are NOT applied to the cross-block caches immediately — they are buffered on the
    /// <see cref="ScopeWrapper"/> and promoted only during <see cref="ScopeWrapper.Commit"/>,
    /// which is called from CommitTree after successful block processing.
    /// </summary>
    private sealed class CacheUpdatingWriteBatch(
        IWorldStateScopeProvider.IWorldStateWriteBatch baseBatch,
        ScopeWrapper scope) : IWorldStateScopeProvider.IWorldStateWriteBatch
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
            return new CacheUpdatingStorageWriteBatch(baseBatchStorage, scope, key);
        }
    }

    /// <summary>
    /// Wraps storage write batch to buffer storage slot updates for cross-block cache promotion.
    /// On <see cref="Clear"/> (CREATE/SELFDESTRUCT), sets a flag so the promotion step
    /// epoch-bumps the cross-block storage cache before re-seeding with correct values.
    /// </summary>
    private sealed class CacheUpdatingStorageWriteBatch(
        IWorldStateScopeProvider.IStorageWriteBatch baseBatch,
        ScopeWrapper scope,
        Address address) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        public void Set(in UInt256 index, byte[] value)
        {
            baseBatch.Set(in index, value);
            StorageCell cell = new(address, in index);
            scope.BufferStorageUpdate(in cell, value);
        }

        public void Clear()
        {
            baseBatch.Clear();
            // Signal that a storage clear happened. The promotion step will
            // epoch-bump the cross-block storage cache, then re-seed with
            // the correct committed values from the buffer.
            scope.BufferStorageClear();
        }

        public void Dispose() => baseBatch.Dispose();
    }
}
