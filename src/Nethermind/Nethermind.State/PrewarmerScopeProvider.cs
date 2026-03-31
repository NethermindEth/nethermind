// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock) => new ScopeWrapper(baseProvider.BeginScope(baseBlock), preBlockCaches, populatePreBlockCache);

    public PreBlockCaches? Caches => preBlockCaches;
    public bool IsWarmWorldState => !populatePreBlockCache;

    private sealed class ScopeWrapper : IWorldStateScopeProvider.IScope
    {
        private readonly IWorldStateScopeProvider.IScope baseScope;
        private readonly SeqlockCache<AddressAsKey, Account> preBlockCache;
        private readonly SeqlockCache<StorageCell, byte[]> storageCache;
        private readonly bool populatePreBlockCache;
        private static readonly SeqlockCache<AddressAsKey, Account>.ValueFactory<ScopeWrapper> _getFromBaseTree = static (in AddressAsKey address, ScopeWrapper self) => self.GetFromBaseTree(in address);
        private readonly IMetricObserver _metricObserver = Metrics.PrewarmerGetTime;
        private readonly bool _measureMetric = Metrics.DetailedMetricsEnabled;
        private readonly PrewarmerGetTimeLabels _labels;

        public ScopeWrapper(IWorldStateScopeProvider.IScope baseScope, PreBlockCaches preBlockCaches, bool populatePreBlockCache)
        {
            this.baseScope = baseScope;
            preBlockCache = preBlockCaches.StateCache;
            storageCache = preBlockCaches.StorageCache;
            this.populatePreBlockCache = populatePreBlockCache;
            _labels = populatePreBlockCache ? PrewarmerGetTimeLabels.Prewarmer : PrewarmerGetTimeLabels.NonPrewarmer;
        }

        private long _writeBatchTime = 0;
        private CancellationTokenSource? _balCts;

        public void Dispose()
        {
            _balCts?.Cancel();
            _balCts?.Dispose();
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
                populatePreBlockCache);
        }

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum)
        {
            // Cancel BAL background read — write batches are processor-intensive and already parallelized
            _balCts?.Cancel();

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

        public void HintBal(BlockAccessList bal)
        {
            if (!populatePreBlockCache)
            {
                return;
            }

            _balCts?.Cancel();
            _balCts = new CancellationTokenSource();
            CacheSink cacheSink = new(preBlockCache, storageCache);
            CancellationToken token = _balCts.Token;
            Task.Run(() => baseScope.ReadBalAsync(bal, cacheSink, token));
        }

        public Task ReadBalAsync(BlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink sink, CancellationToken cancellationToken)
        {
            return baseScope.ReadBalAsync(bal, sink, cancellationToken);
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
            {
                storageCache.Set(in storageCell, value);
            }
        }

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
        private readonly bool populatePreBlockCache;
        private static readonly SeqlockCache<StorageCell, byte[]>.ValueFactory<StorageTreeWrapper> _loadFromTreeStorage = static (in StorageCell cell, StorageTreeWrapper self) => self.LoadFromTreeStorage(in cell);
        private readonly IMetricObserver _metricObserver = Db.Metrics.PrewarmerGetTime;
        private readonly bool _measureMetric = Db.Metrics.DetailedMetricsEnabled;
        private readonly PrewarmerGetTimeLabels _labels;

        public StorageTreeWrapper(
            IWorldStateScopeProvider.IStorageTree baseStorageTree,
            SeqlockCache<StorageCell, byte[]> preBlockCache,
            Address address,
            bool populatePreBlockCache)
        {
            this.baseStorageTree = baseStorageTree;
            this.preBlockCache = preBlockCache;
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
}
