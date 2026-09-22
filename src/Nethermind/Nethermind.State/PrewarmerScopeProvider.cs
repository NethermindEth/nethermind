// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Metric;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using ICodeCache = Nethermind.Evm.ICodeCache;

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
/// <param name="codeCache">Code it holds is not read ahead from a block access list, as execution needs no read for it.</param>
/// <param name="prefetchCode">Whether a consumer scope reads the code a block access list names ahead of execution.</param>
public class PrewarmerScopeProvider(
    IWorldStateScopeProvider baseProvider,
    IPrewarmerState prewarmerState,
    ILogManager logManager,
    ICodeCache? codeCache = null,
    bool prefetchCode = false
) : IWorldStateScopeProvider
{
    private readonly PreBlockCaches preBlockCaches = prewarmerState.Caches;
    private readonly bool isPrewarmer = prewarmerState.IsPrewarmer;
    private readonly ILogger logger = logManager.GetClassLogger<PrewarmerScopeProvider>();

    public bool HasRoot(BlockHeader? baseBlock) => baseProvider.HasRoot(baseBlock);

    public bool HasStateForTargetBlock(BlockHeader targetBlock) => baseProvider.HasStateForTargetBlock(targetBlock);

    public bool TryBeginScopeAtTarget(BlockHeader targetBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
    {
        if (!baseProvider.TryBeginScopeAtTarget(targetBlock, metrics, out IWorldStateScopeProvider.IScope? baseScope))
        {
            scope = null;
            return false;
        }

        scope = WrapScope(baseScope, metrics, baseScope.RootHash);
        return true;
    }

    public bool TryBeginScope(BlockHeader? baseBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
    {
        if (!baseProvider.TryBeginScope(baseBlock, metrics, out IWorldStateScopeProvider.IScope? baseScope))
        {
            scope = null;
            return false;
        }

        scope = WrapScope(baseScope, metrics, baseBlock?.StateRoot);
        return true;
    }

    private IWorldStateScopeProvider.IScope WrapScope(IWorldStateScopeProvider.IScope scope, LocalMetrics metrics, Hash256? stateRoot)
    {
        if (!isPrewarmer)
        {
            try
            {
                // Opening joins any speculative session, so the check below and the scope's reads see no other writer.
                preBlockCaches.BeginConsumerScope();
                preBlockCaches.MainScope = scope;
                // The consumer reads the state at the opened root through the caches, which may still describe another state.
                preBlockCaches.EnsureNotStaleFor(stateRoot, logger);
            }
            catch
            {
                preBlockCaches.MainScope = null;
                try
                {
                    scope.Dispose();
                }
                finally
                {
                    preBlockCaches.EndConsumerScope();
                }
                throw;
            }
        }
        PreBlockCaches.StorageReadCapture? storageReadCapture = isPrewarmer ? preBlockCaches.CurrentStorageReadCapture : null;
        return new ScopeWrapper(scope, preBlockCaches, logManager, isPrewarmer, storageReadCapture, metrics, stateRoot, codeCache, prefetchCode);
    }

    private sealed class ScopeWrapper(
        IWorldStateScopeProvider.IScope baseScope,
        PreBlockCaches preBlockCaches,
        ILogManager logManager,
        bool isPrewarmer,
        PreBlockCaches.StorageReadCapture? storageReadCapture,
        LocalMetrics metrics,
        Hash256? baseStateRoot,
        ICodeCache? codeCache,
        bool prefetchCode) : IWorldStateScopeProvider.IScope
    {
        private readonly IWorldStateScopeProvider.IScope baseScope = baseScope;
        public bool StorageRootsAreAuthoritative => baseScope.StorageRootsAreAuthoritative;
        private readonly PreBlockCaches preBlockCaches = preBlockCaches;
        private readonly SeqlockCache<AddressAsKey, Account> preBlockCache = preBlockCaches.StateCache;
        private readonly SeqlockCache<StorageCell, UInt256> storageCache = preBlockCaches.StorageCache;
        private readonly bool isPrewarmer = isPrewarmer;
        private readonly IWorldStateScopeProvider.IScope? mainScope = isPrewarmer ? preBlockCaches.MainScope : null;
        private readonly LocalMetrics _metrics = metrics;
        private readonly IMetricObserver _metricObserver = Metrics.PrewarmerGetTime;
        private readonly bool _measureMetric = Metrics.DetailedMetricsEnabled;
        private readonly PrewarmerGetTimeLabels _labels = isPrewarmer ? PrewarmerGetTimeLabels.Prewarmer : PrewarmerGetTimeLabels.NonPrewarmer;
        private readonly ILogger _logger = logManager.GetClassLogger<ScopeWrapper>();
        private long _writeBatchTime = 0;
        // Root of the state the next commit starts from: the base block's, then each committed root in turn.
        private Hash256? _committedStateRoot = baseStateRoot;
        private readonly PrefetchedCodeDb _codeDb = new(baseScope.CodeDb, preBlockCaches);

        public void Dispose()
        {
            if (isPrewarmer)
            {
                ObserveWriteBatchToDispose();
                baseScope.Dispose();
                return;
            }

            // Unregister before teardown so no new warm hints target a disposing scope.
            preBlockCaches.MainScope = null;
            // The block is over: code no one took is dropped, and queued code is not read.
            preBlockCaches.CodePrefetcher?.Stop();
            try
            {
                ObserveWriteBatchToDispose();
                baseScope.Dispose();
            }
            finally
            {
                preBlockCaches.CodePrefetcher = null;
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

        public IWorldStateScopeProvider.ICodeDb CodeDb => _codeDb;

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address)
        {
            IWorldStateScopeProvider.IStorageTree baseTree = baseScope.CreateStorageTree(address);
            // Asked once per tree: a tree serves one block, and the set only changes in a write-back between blocks.
            bool bypassCache = preBlockCaches.BypassesStorageCache(address);
            return storageReadCapture is not null
                ? new CapturingStorageTreeWrapper(baseTree, storageReadCapture, storageCache, address, bypassCache)
                : new StorageTreeWrapper(baseTree, storageCache, address, isPrewarmer, _metrics, bypassCache);
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

        // Populator hints target the block's consumer scope (whose commit walks the hinted paths);
        // consumer hints go straight to the backend. Capturing (discovery) scopes execute on placeholder
        // values, so their hinted addresses and slots can be fictitious — never forward them.
        public void HintWarmAccount(Address address)
        {
            if (storageReadCapture is not null) return;
            (isPrewarmer ? mainScope : baseScope)?.HintWarmAccount(address);
        }

        public void HintWarmSlot(Address address, in UInt256 index)
        {
            if (storageReadCapture is not null) return;
            (isPrewarmer ? mainScope : baseScope)?.HintWarmSlot(address, in index);
        }

        // Only the consumer's commits are the block's changes; a populator's are speculative.
        public void HintSetAccount(Address address, Account? account)
        {
            if (isPrewarmer || storageReadCapture is not null) return;
            baseScope.HintSetAccount(address, account);
        }

        public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null)
        {
            CodePrefetcher? code = null;
            if (prefetchCode && !isPrewarmer)
            {
                // The block's parent readers take from it too, so it is shared through the caches.
                code = new CodePrefetcher(baseScope.CodeDb, codeCache, logManager: logManager);
                preBlockCaches.CodePrefetcher?.Stop();
                preBlockCaches.CodePrefetcher = code;
            }

            sink ??= new CacheSink(preBlockCaches, preBlockCache, storageCache, code);
            return baseScope.HintBal(bal, sink);
        }

        private sealed class CacheSink(
            PreBlockCaches caches,
            SeqlockCache<AddressAsKey, Account> stateCache,
            SeqlockCache<StorageCell, UInt256> storageCache,
            CodePrefetcher? code
        ) : IWorldStateScopeProvider.IAsyncBalReaderSink
        {
            public void OnAccountRead(Address address, Account? account)
            {
                AddressAsKey key = address;
                stateCache.Set(in key, account);
                if (account is { HasCode: true }) code?.Enqueue(account.CodeHash.ValueHash256);
            }

            public void OnStorageRead(in StorageCell storageCell, in UInt256 value)
            {
                // A wiped contract's slots are never read from the cache, so caching one would only take room.
                if (caches.BypassesStorageCache(storageCell.Address)) return;
                storageCache.Set(in storageCell, in value);
            }

            public bool StillNeeded(Address address, out Account? account)
            {
                AddressAsKey key = address;
                if (!stateCache.TryGetValue(in key, out account)) return true;

                // A cached account is never read, so its code is queued here instead.
                if (account is { HasCode: true }) code?.Enqueue(account.CodeHash.ValueHash256);
                return false;
            }

            // A cached slot of a wiped contract may be stale, so it does not count as already read.
            public bool StillNeeded(in StorageCell storageCell)
                => caches.BypassesStorageCache(storageCell.Address) || !storageCache.TryGetValue(in storageCell, out _);
        }

        private Account? GetFromBaseTree(in AddressAsKey address) => baseScope.Get(address);
    }

    /// <summary>Serves code the block's access list read ahead, then reads the rest from the store.</summary>
    private sealed class PrefetchedCodeDb(IWorldStateScopeProvider.ICodeDb codeDb, PreBlockCaches preBlockCaches)
        : IWorldStateScopeProvider.ICodeDb
    {
        public ReadOnlyMemory<byte> GetCode(in ValueHash256 codeHash)
        {
            ReadOnlyMemory<byte> code = preBlockCaches.CodePrefetcher is { } prefetcher ? prefetcher.Take(in codeHash) : default;
            return code.IsNull() ? codeDb.GetCode(in codeHash) : code;
        }

        public IWorldStateScopeProvider.ICodeSetter BeginCodeWrite() => codeDb.BeginCodeWrite();

        public bool ContainsCode(in ValueHash256 codeHash) => codeDb.ContainsCode(in codeHash);

        public void MarkCodePersisted(in ValueHash256 codeHash) => codeDb.MarkCodePersisted(in codeHash);
    }

    private sealed class StorageTreeWrapper(
        IWorldStateScopeProvider.IStorageTree baseStorageTree,
        SeqlockCache<StorageCell, UInt256> preBlockCache,
        Address address,
        bool isPrewarmer,
        LocalMetrics metrics,
        bool bypassCache) : IWorldStateScopeProvider.IStorageTree
    {
        private readonly IWorldStateScopeProvider.IStorageTree baseStorageTree = baseStorageTree;
        private readonly SeqlockCache<StorageCell, UInt256> preBlockCache = preBlockCache;
        private readonly Address address = address;
        private readonly bool isPrewarmer = isPrewarmer;
        private readonly LocalMetrics _metrics = metrics;
        private readonly IMetricObserver _metricObserver = Db.Metrics.PrewarmerGetTime;
        private readonly bool _measureMetric = Db.Metrics.DetailedMetricsEnabled;
        private readonly PrewarmerGetTimeLabels _labels = isPrewarmer ? PrewarmerGetTimeLabels.Prewarmer : PrewarmerGetTimeLabels.NonPrewarmer;

        public Hash256 RootHash => baseStorageTree.RootHash;

        public void Get(in UInt256 index, out UInt256 value)
        {
            StorageCell storageCell = new(address, in index); // TODO: Make the dictionary use UInt256 directly
            if (bypassCache)
            {
                // The contract wiped its storage after these slots were cached; whatever is cached for it may be stale.
                LoadFromTreeStorage(in storageCell, out value);
                return;
            }

            long sw = _measureMetric ? Stopwatch.GetTimestamp() : 0;
            if (preBlockCache.TryGetValue(in storageCell, out value))
            {
                if (_measureMetric) _metricObserver.Observe(Stopwatch.GetTimestamp() - sw, _labels.SlotGetHit);
                _metrics.IncrementStorageTreeCache();
                if (!isPrewarmer) _metrics.IncrementPreBlockStorageHits();
            }
            else
            {
                LoadFromTreeStorage(in storageCell, out value);
                // Backfill so other readers reuse this resolve; SeqlockCache.Set is safe under concurrent writers.
                preBlockCache.Set(in storageCell, in value);
                if (_measureMetric) _metricObserver.Observe(Stopwatch.GetTimestamp() - sw, _labels.SlotGetMiss);
            }
        }

        public void HintSet(in UInt256 index) => baseStorageTree.HintSet(in index);

        public void HintSet(in UInt256 index, in UInt256 value)
        {
            if (isPrewarmer) baseStorageTree.HintSet(in index);
            else baseStorageTree.HintSet(in index, in value);
        }

        public void HintClear() => baseStorageTree.HintClear();

        private void LoadFromTreeStorage(in StorageCell storageCell, out UInt256 value)
        {
            // PreBlock misses only (consumer scope): StorageTreeReads is already counted once per
            // first-in-block touch by PersistentStorageProvider; counting it here again double-counted
            // fully-cold reads. Populator probes are excluded — they miss by design while filling.
            if (!isPrewarmer) _metrics.IncrementPreBlockStorageMisses();

            baseStorageTree.Get(storageCell.Index, out value);
        }
    }

    private sealed class CapturingStorageTreeWrapper(
        IWorldStateScopeProvider.IStorageTree baseStorageTree,
        PreBlockCaches.StorageReadCapture storageReadCapture,
        SeqlockCache<StorageCell, UInt256> preBlockCache,
        Address address,
        bool bypassCache) : IWorldStateScopeProvider.IStorageTree
    {
        public Hash256 RootHash => baseStorageTree.RootHash;

        public void Get(in UInt256 index, out UInt256 value)
        {
            StorageCell storageCell = new(address, in index);
            if (!bypassCache && preBlockCache.TryGetValue(in storageCell, out value))
            {
                return;
            }

            storageReadCapture.Record(in storageCell);
            // Nonzero keeps common existence checks and bounded loops progressing to reveal later reads.
            value = UInt256.One;
        }

        public void HintSet(in UInt256 index) => baseStorageTree.HintSet(in index);

        public void HintClear() => baseStorageTree.HintClear();
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
