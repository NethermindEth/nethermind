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

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock) => new ScopeWrapper(baseProvider.BeginScope(baseBlock), preBlockCaches, logManager, isPrewarmer);

    public PreBlockCaches? Caches => preBlockCaches;
    public bool IsWarmWorldState => !isPrewarmer;

    private sealed class ScopeWrapper(IWorldStateScopeProvider.IScope baseScope, PreBlockCaches preBlockCaches, ILogManager logManager, bool isPrewarmer) : IWorldStateScopeProvider.IScope
    {
        private readonly IWorldStateScopeProvider.IScope baseScope = baseScope;
        private readonly SeqlockCache<AddressAsKey, Account> preBlockCache = preBlockCaches.StateCache;
        private readonly SeqlockCache<StorageCell, byte[]> storageCache = preBlockCaches.StorageCache;
        private readonly bool isPrewarmer = isPrewarmer;
        private readonly IMetricObserver _metricObserver = Metrics.PrewarmerGetTime;
        private readonly bool _measureMetric = Metrics.DetailedMetricsEnabled;
        private readonly PrewarmerGetTimeLabels _labels = isPrewarmer ? PrewarmerGetTimeLabels.Prewarmer : PrewarmerGetTimeLabels.NonPrewarmer;
        private readonly ILogger _logger = logManager.GetClassLogger<ScopeWrapper>();
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

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) => new StorageTreeWrapper(
                baseScope.CreateStorageTree(address),
                storageCache,
                address,
                isPrewarmer);

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum)
        {
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
        bool isPrewarmer) : IWorldStateScopeProvider.IStorageTree
    {
        private readonly IWorldStateScopeProvider.IStorageTree baseStorageTree = baseStorageTree;
        private readonly SeqlockCache<StorageCell, byte[]> preBlockCache = preBlockCache;
        private readonly Address address = address;
        private readonly bool isPrewarmer = isPrewarmer;
        private readonly IMetricObserver _metricObserver = Db.Metrics.PrewarmerGetTime;
        private readonly bool _measureMetric = Db.Metrics.DetailedMetricsEnabled;
        private readonly PrewarmerGetTimeLabels _labels = isPrewarmer ? PrewarmerGetTimeLabels.Prewarmer : PrewarmerGetTimeLabels.NonPrewarmer;
        private UInt256 _lastIndex;
        private byte[]? _lastValue;
        private bool _hasLastValue;

        public Hash256 RootHash => baseStorageTree.RootHash;

        public byte[] Get(in UInt256 index)
        {
            if (_hasLastValue && _lastIndex.Equals(index))
            {
                return _lastValue!;
            }

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

            _lastIndex = index;
            _lastValue = value;
            _hasLastValue = true;
            return value;
        }

        public void HintSet(in UInt256 index, byte[]? value)
        {
            if (_hasLastValue && _lastIndex.Equals(index))
            {
                _lastValue = value ?? StorageTree.ZeroBytes;
            }

            baseStorageTree.HintSet(in index, value);
        }

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
