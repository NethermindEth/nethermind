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
/// Decorates a scope provider with the shared <see cref="PreBlockCaches"/>. A miss always backfills. When the
/// consumer commits a block, the world state writes the block's final values back into the account and storage
/// caches, so they carry over to the next block; the driver (<c>BlockCachePreWarmer</c>) keeps or clears them
/// before any populator writes.
/// </summary>
/// <param name="prewarmerState">
/// Carries the shared caches and <see cref="IPrewarmerState.IsPrewarmer"/>. On a cache hit a consumer seeds the
/// scope-local cache via <c>HintGet</c> (for its later commit); a populator does not. A consumer scope registers
/// itself as the block's <see cref="PreBlockCaches.MainScope"/>; a populator pushes trie warm-up hints into it.
/// </param>
public class PrewarmerScopeProvider(
    IWorldStateScopeProvider baseProvider,
    IPrewarmerState prewarmerState,
    ILogManager logManager
) : IWorldStateScopeProvider
{
    private readonly PreBlockCaches preBlockCaches = prewarmerState.Caches;
    private readonly bool isPrewarmer = prewarmerState.IsPrewarmer;
    private readonly ILogger logger = logManager.GetClassLogger<PrewarmerScopeProvider>();

    public bool HasRoot(BlockHeader? baseBlock) => baseProvider.HasRoot(baseBlock);

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock, LocalMetrics metrics)
    {
        PreBlockCaches.StorageReadCapture? storageReadCapture = isPrewarmer ? preBlockCaches.CurrentStorageReadCapture : null;
        IWorldStateScopeProvider.ITrieWarmupSession? trieWarmupSession = null;
        IWorldStateScopeProvider.IScope? scope = null;
        bool consumerScopeOpened = false;
        bool registeredMainScope = false;
        try
        {
            scope = baseProvider.BeginScope(baseBlock, metrics);
            if (isPrewarmer)
            {
                if (storageReadCapture is null)
                {
                    lock (preBlockCaches)
                    {
                        trieWarmupSession = preBlockCaches.MainScope?.CreateTrieWarmupSession();
                    }
                }
            }
            else
            {
                // Opening joins any speculative session, so the check below and the scope's reads see no other writer.
                consumerScopeOpened = true;
                preBlockCaches.BeginConsumerScope();
                lock (preBlockCaches)
                {
                    preBlockCaches.MainScope = scope;
                    registeredMainScope = true;
                }
                // The consumer reads the state at baseBlock through the caches, which may still describe another state.
                preBlockCaches.EnsureNotStaleFor(baseBlock?.StateRoot, logger);
            }

            ScopeWrapper wrapper = new(scope, preBlockCaches, logManager, isPrewarmer, trieWarmupSession, storageReadCapture, metrics, baseBlock?.StateRoot);
            scope = null;
            trieWarmupSession = null;
            consumerScopeOpened = false;
            return wrapper;
        }
        finally
        {
            if (registeredMainScope)
            {
                lock (preBlockCaches)
                {
                    if (ReferenceEquals(preBlockCaches.MainScope, scope)) preBlockCaches.MainScope = null;
                }
            }

            try
            {
                scope?.Dispose();
            }
            finally
            {
                try
                {
                    trieWarmupSession?.Dispose();
                }
                finally
                {
                    if (consumerScopeOpened) preBlockCaches.EndConsumerScope();
                }
            }
        }
    }

    private sealed class ScopeWrapper(
        IWorldStateScopeProvider.IScope baseScope,
        PreBlockCaches preBlockCaches,
        ILogManager logManager,
        bool isPrewarmer,
        IWorldStateScopeProvider.ITrieWarmupSession? trieWarmupSession,
        PreBlockCaches.StorageReadCapture? storageReadCapture,
        LocalMetrics metrics,
        Hash256? baseStateRoot) : IWorldStateScopeProvider.IScope
    {
        private readonly IWorldStateScopeProvider.IScope baseScope = baseScope;
        private readonly PreBlockCaches preBlockCaches = preBlockCaches;
        private readonly SeqlockCache<AddressAsKey, Account> preBlockCache = preBlockCaches.StateCache;
        private readonly SeqlockCache<StorageCell, byte[]> storageCache = preBlockCaches.StorageCache;
        private readonly bool isPrewarmer = isPrewarmer;
        private readonly IWorldStateScopeProvider.ITrieWarmupSession? trieWarmupSession = trieWarmupSession;
        private readonly LocalMetrics _metrics = metrics;
        private readonly IMetricObserver _metricObserver = Metrics.PrewarmerGetTime;
        private readonly bool _measureMetric = Metrics.DetailedMetricsEnabled;
        private readonly PrewarmerGetTimeLabels _labels = isPrewarmer ? PrewarmerGetTimeLabels.Prewarmer : PrewarmerGetTimeLabels.NonPrewarmer;
        private readonly ILogger _logger = logManager.GetClassLogger<ScopeWrapper>();
        private long _writeBatchTime = 0;
        private int _isDisposed;
        // Root of the state the next commit starts from: the base block's, then each committed root in turn.
        private Hash256? _committedStateRoot = baseStateRoot;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;

            ObserveWriteBatchToDispose();
            if (isPrewarmer)
            {
                try
                {
                    trieWarmupSession?.Dispose();
                }
                finally
                {
                    baseScope.Dispose();
                }
                return;
            }

            // Unregister before teardown so no new warm hints target a disposing scope.
            lock (preBlockCaches)
            {
                if (ReferenceEquals(preBlockCaches.MainScope, baseScope)) preBlockCaches.MainScope = null;
            }

            try
            {
                baseScope.Dispose();
            }
            finally
            {
                // Only now are the scope's background readers (HintBal) drained, so only now may a session take over the caches.
                int stillOpen = preBlockCaches.EndConsumerScope();
                Debug.Assert(stillOpen >= 0, "a consumer scope was closed more often than it was opened");
            }
        }

        private void ObserveWriteBatchToDispose()
        {
            if (_measureMetric && _writeBatchTime != 0)
            {
                _metricObserver.Observe(Stopwatch.GetTimestamp() - _writeBatchTime, _labels.WriteBatchToScopeDisposeTime);
            }
        }

        public IWorldStateScopeProvider.ICodeDb CodeDb => baseScope.CodeDb;

        public IWorldStateScopeProvider.ITrieWarmupSession CreateTrieWarmupSession() =>
            baseScope.CreateTrieWarmupSession();

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address)
        {
            IWorldStateScopeProvider.IStorageTree baseTree = baseScope.CreateStorageTree(address);
            return storageReadCapture is not null
                ? new CapturingStorageTreeWrapper(baseTree, storageReadCapture, storageCache, address)
                : new StorageTreeWrapper(baseTree, storageCache, address, isPrewarmer, _metrics);
        }

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

        public void Commit(ulong blockNumber)
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

        // Only the consumer's commits become state, and they are what the caches must reflect for the next block.
        public void WriteBackCommittedState(Func<IWorldStateScopeProvider.IBlockChangeSnapshot> takeSnapshot)
        {
            if (isPrewarmer) return;

            Hash256 stateRoot = baseScope.RootHash;
            // An unchanged root means the block changed nothing, or the scope computes no roots (a trieless one) and its
            // committed values would be tagged with the pre-block root: either way there is nothing to bring forward.
            if (stateRoot == _committedStateRoot) return;

            Hash256? baseStateRoot = _committedStateRoot;
            _committedStateRoot = stateRoot;
            preBlockCaches.WriteBackInBackground(baseStateRoot, stateRoot, takeSnapshot, _logger);
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
                // Pre-block counters are consumer-only: populators miss by design while filling the cache,
                // so counting their probes would drag the exported coverage ratio below the true value.
                if (!isPrewarmer)
                {
                    baseScope.HintGet(address, account);
                    _metrics.IncrementPreBlockAccountHits();
                }

                _metrics.IncrementStateTreeCacheHits();
            }
            else
            {
                account = GetFromBaseTree(in addressAsKey);
                // Backfill so other readers reuse this resolve; SeqlockCache.Set is safe under concurrent writers.
                preBlockCache.Set(in addressAsKey, account);
                if (!isPrewarmer) _metrics.IncrementPreBlockAccountMisses();
                if (_measureMetric) _metricObserver.Observe(Stopwatch.GetTimestamp() - sw, _labels.AddressMiss);
            }
            return account;
        }

        public void HintGet(Address address, Account? account) => baseScope.HintGet(address, account);

        // Capturing (discovery) scopes execute on placeholder values, so their hinted addresses and slots can be
        // fictitious. Populator hints otherwise target the independently owned session for this build's base state.
        public void HintWarmAccount(in ValueAddress address)
        {
            if (storageReadCapture is not null) return;
            if (isPrewarmer)
                trieWarmupSession?.HintWarmAccount(in address);
            else
                baseScope.HintWarmAccount(in address);
        }

        public void HintWarmSlot(in ValueAddress address, in UInt256 index)
        {
            if (storageReadCapture is not null) return;
            if (isPrewarmer)
                trieWarmupSession?.HintWarmSlot(in address, in index);
            else
                baseScope.HintWarmSlot(in address, in index);
        }

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
        LocalMetrics metrics) : IWorldStateScopeProvider.IStorageTree
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
            StorageCell storageCell = new(address, in index); // TODO: Make the dictionary use UInt256 directly
            long sw = _measureMetric ? Stopwatch.GetTimestamp() : 0;
            if (preBlockCache.TryGetValue(in storageCell, out byte[] value))
            {
                if (_measureMetric) _metricObserver.Observe(Stopwatch.GetTimestamp() - sw, _labels.SlotGetHit);
                _metrics.IncrementStorageTreeCache();
                if (!isPrewarmer) _metrics.IncrementPreBlockStorageHits();
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
            // PreBlock misses only (consumer scope): StorageTreeReads is already counted once per
            // first-in-block touch by PersistentStorageProvider; counting it here again double-counted
            // fully-cold reads. Populator probes are excluded — they miss by design while filling.
            if (!isPrewarmer) _metrics.IncrementPreBlockStorageMisses();

            return baseStorageTree.Get(storageCell.Index);
        }
    }

    private sealed class CapturingStorageTreeWrapper(
        IWorldStateScopeProvider.IStorageTree baseStorageTree,
        PreBlockCaches.StorageReadCapture storageReadCapture,
        SeqlockCache<StorageCell, byte[]> preBlockCache,
        Address address) : IWorldStateScopeProvider.IStorageTree
    {
        private static readonly byte[] SpeculativeStorageValue = [1];

        public Hash256 RootHash => baseStorageTree.RootHash;

        public byte[] Get(in UInt256 index)
        {
            StorageCell storageCell = new(address, in index);
            if (preBlockCache.TryGetValue(in storageCell, out byte[] value))
            {
                return value;
            }

            storageReadCapture.Record(in storageCell);
            // Nonzero keeps common existence checks and bounded loops progressing to reveal later reads.
            return SpeculativeStorageValue;
        }

        public void HintSet(in UInt256 index, byte[]? value) => baseStorageTree.HintSet(in index, value);
    }

    private class WriteBatchLifetimeMeasurer(IWorldStateScopeProvider.IWorldStateWriteBatch baseWriteBatch, IMetricObserver metricObserver, long startTime, bool isPrewarmer) : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        private readonly PrewarmerGetTimeLabels _labels = isPrewarmer ? PrewarmerGetTimeLabels.Prewarmer : PrewarmerGetTimeLabels.NonPrewarmer;

        public bool AcceptsStorageWrites => baseWriteBatch.AcceptsStorageWrites;

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
