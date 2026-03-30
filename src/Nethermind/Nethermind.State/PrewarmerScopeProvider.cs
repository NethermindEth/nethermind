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
    bool populatePreBlockCache = true
) : IWorldStateScopeProvider, IPreBlockCaches
{
    public bool HasRoot(BlockHeader? baseBlock) => baseProvider.HasRoot(baseBlock);

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock)
    {
        if (baseBlock is null || !preBlockCaches.IsValidForParent(baseBlock.Number, baseBlock.Hash))
        {
            preBlockCaches.InvalidateCaches();
        }

        preBlockCaches.ResetBlockFlags();

        return new ScopeWrapper(baseProvider.BeginScope(baseBlock), preBlockCaches, populatePreBlockCache);
    }

    public PreBlockCaches? Caches => preBlockCaches;
    public bool IsWarmWorldState => !populatePreBlockCache;

    private sealed class ScopeWrapper : IWorldStateScopeProvider.IScope
    {
        private readonly IWorldStateScopeProvider.IScope baseScope;
        private readonly PreBlockCaches preBlockCaches;
        private readonly SeqlockCache<AddressAsKey, Account> preBlockCache;
        private readonly SeqlockCache<StorageCell, byte[]> storageCache;
        private readonly bool populatePreBlockCache;
        private readonly IMetricObserver _metricObserver = Metrics.PrewarmerGetTime;
        private readonly bool _measureMetric = Metrics.DetailedMetricsEnabled;
        private readonly PrewarmerGetTimeLabels _labels;

        public ScopeWrapper(IWorldStateScopeProvider.IScope baseScope, PreBlockCaches preBlockCaches, bool populatePreBlockCache)
        {
            this.baseScope = baseScope;
            this.preBlockCaches = preBlockCaches;
            preBlockCache = preBlockCaches.StateCache;
            storageCache = preBlockCaches.StorageCache;
            this.populatePreBlockCache = populatePreBlockCache;
            _labels = populatePreBlockCache ? PrewarmerGetTimeLabels.Prewarmer : PrewarmerGetTimeLabels.NonPrewarmer;
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
                _labels);
        }

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum)
        {
            IWorldStateScopeProvider.IWorldStateWriteBatch baseWriteBatch = baseScope.StartWriteBatch(estimatedAccountNum);

            if (!_measureMetric && populatePreBlockCache)
            {
                return baseWriteBatch;
            }

            long startTime = 0;
            if (_measureMetric)
            {
                startTime = Stopwatch.GetTimestamp();
                _writeBatchTime = startTime;
            }

            if (!populatePreBlockCache)
            {
                return new CachePopulatingWriteBatch(
                    baseWriteBatch,
                    preBlockCaches,
                    _measureMetric ? _metricObserver : null,
                    startTime,
                    _labels);
            }

            return new WriteBatchLifetimeMeasurer(
                baseWriteBatch,
                _metricObserver,
                startTime,
                populatePreBlockCache);
        }

        public void Commit(long blockNumber)
        {
            if (!_measureMetric)
            {
                baseScope.Commit(blockNumber);
                return;
            }

            long sw = Stopwatch.GetTimestamp();
            baseScope.Commit(blockNumber);
            _metricObserver.Observe(Stopwatch.GetTimestamp() - sw, _labels.Commit);
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
                baseScope.HintGet(address, account);
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
        private readonly Address address;
        private static readonly SeqlockCache<StorageCell, byte[]>.ValueFactory<StorageTreeWrapper> _loadFromTreeStorage = static (in StorageCell cell, StorageTreeWrapper self) => self.LoadFromTreeStorage(in cell);
        private readonly IMetricObserver _metricObserver = Db.Metrics.PrewarmerGetTime;
        private readonly bool _measureMetric = Db.Metrics.DetailedMetricsEnabled;
        private readonly PrewarmerGetTimeLabels _labels;

        public StorageTreeWrapper(
            IWorldStateScopeProvider.IStorageTree baseStorageTree,
            SeqlockCache<StorageCell, byte[]> preBlockCache,
            Address address,
            PrewarmerGetTimeLabels labels)
        {
            this.baseStorageTree = baseStorageTree;
            this.preBlockCache = preBlockCache;
            this.address = address;
            _labels = labels;
        }

        public Hash256 RootHash => baseStorageTree.RootHash;

        public byte[] Get(in UInt256 index)
        {
            StorageCell storageCell = new StorageCell(address, in index); // TODO: Make the dictionary use UInt256 directly
            long sw = _measureMetric ? Stopwatch.GetTimestamp() : 0;

            long priorReads = Db.Metrics.ThreadLocalStorageTreeReads;
            byte[] value = preBlockCache.GetOrAdd(in storageCell, this, _loadFromTreeStorage);

            if (Db.Metrics.ThreadLocalStorageTreeReads == priorReads)
            {
                if (_measureMetric) _metricObserver.Observe(Stopwatch.GetTimestamp() - sw, _labels.SlotGetHit);
                baseStorageTree.HintGet(in index, value);
                Db.Metrics.IncrementStorageTreeCache();
            }
            else
            {
                if (_measureMetric) _metricObserver.Observe(Stopwatch.GetTimestamp() - sw, _labels.SlotGetMiss);
            }
            return value;
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

    private sealed class CachePopulatingWriteBatch : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        private readonly IWorldStateScopeProvider.IWorldStateWriteBatch _baseWriteBatch;
        private readonly PreBlockCaches _preBlockCaches;
        private readonly IMetricObserver? _metricObserver;
        private readonly long _startTime;
        private readonly PrewarmerGetTimeLabels _labels;

        public CachePopulatingWriteBatch(
            IWorldStateScopeProvider.IWorldStateWriteBatch baseWriteBatch,
            PreBlockCaches preBlockCaches,
            IMetricObserver? metricObserver,
            long startTime,
            PrewarmerGetTimeLabels labels)
        {
            _baseWriteBatch = baseWriteBatch;
            _preBlockCaches = preBlockCaches;
            _metricObserver = metricObserver;
            _startTime = startTime;
            _labels = labels;

            // Subscribe to capture accounts with final storage roots (set during base Dispose)
            baseWriteBatch.OnAccountUpdated += OnBaseAccountUpdated;
        }

        private void OnBaseAccountUpdated(object? sender, IWorldStateScopeProvider.AccountUpdated e)
        {
            // This fires with the correct storage root, overriding any stale entry from Set()
            _preBlockCaches.EnqueueStateWrite(e.Address, e.Account);
        }

        public void Dispose()
        {
            // Base Dispose fires OnAccountUpdated with corrected storage roots, then writes to trie
            _baseWriteBatch.Dispose();
            _baseWriteBatch.OnAccountUpdated -= OnBaseAccountUpdated;
            _metricObserver?.Observe(Stopwatch.GetTimestamp() - _startTime, _labels.WriteBatchLifetime);
        }

        public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated
        {
            add => _baseWriteBatch.OnAccountUpdated += value;
            remove => _baseWriteBatch.OnAccountUpdated -= value;
        }

        public void Set(Address key, Account? account)
        {
            _baseWriteBatch.Set(key, account);
            _preBlockCaches.EnqueueStateWrite(key, account);
        }

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries)
            => new CachePopulatingStorageWriteBatch(
                _baseWriteBatch.CreateStorageWriteBatch(key, estimatedEntries),
                _preBlockCaches,
                key);
    }

    private sealed class CachePopulatingStorageWriteBatch(
        IWorldStateScopeProvider.IStorageWriteBatch baseStorageWriteBatch,
        PreBlockCaches preBlockCaches,
        Address address) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        public void Dispose() => baseStorageWriteBatch.Dispose();

        public void Set(in UInt256 index, byte[] value)
        {
            baseStorageWriteBatch.Set(in index, value);
            preBlockCaches.EnqueueStorageWrite(new StorageCell(address, in index), value);
        }

        public void Clear()
        {
            baseStorageWriteBatch.Clear();
            preBlockCaches.NoteStorageClear();
        }
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
}
