// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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

    // The stride prefetcher opens a second, read-only scope over the same parent; a decorator that
    // forgot to forward this would silently report the conservative false and disable it.
    public bool SupportsConcurrentScopes => baseProvider.SupportsConcurrentScopes;

    public bool HasRoot(BlockHeader? baseBlock) => baseProvider.HasRoot(baseBlock);

    public bool HasStateForTargetBlock(BlockHeader targetBlock) => baseProvider.HasStateForTargetBlock(targetBlock);

    public bool TryBeginScopeAtTarget(BlockHeader targetBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
    {
        if (!baseProvider.TryBeginScopeAtTarget(targetBlock, metrics, out IWorldStateScopeProvider.IScope? baseScope))
        {
            scope = null;
            return false;
        }

        scope = WrapScope(baseScope, metrics, baseScope.RootHash, PrefetchScopeAnchor.AtTarget(targetBlock));
        return true;
    }

    public bool TryBeginScope(BlockHeader? baseBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
    {
        if (!baseProvider.TryBeginScope(baseBlock, metrics, out IWorldStateScopeProvider.IScope? baseScope))
        {
            scope = null;
            return false;
        }

        scope = WrapScope(baseScope, metrics, baseBlock?.StateRoot, PrefetchScopeAnchor.AtBase(baseBlock));
        return true;
    }

    private IWorldStateScopeProvider.IScope WrapScope(IWorldStateScopeProvider.IScope scope, LocalMetrics metrics, Hash256? stateRoot, PrefetchScopeAnchor prefetchAnchor)
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
        return new ScopeWrapper(baseProvider, prefetchAnchor, scope, preBlockCaches, logManager, isPrewarmer, storageReadCapture, metrics, stateRoot);
    }

    /// <summary>
    /// How the stride prefetcher reopens the state this scope was opened on: at the same target block (the provider
    /// resolves its parent again) or at the same explicit base block.
    /// </summary>
    private readonly record struct PrefetchScopeAnchor(BlockHeader? Block, bool IsTarget)
    {
        public static PrefetchScopeAnchor AtTarget(BlockHeader targetBlock) => new(targetBlock, true);
        public static PrefetchScopeAnchor AtBase(BlockHeader? baseBlock) => new(baseBlock, false);

        /// <exception cref="StateNotRetainedException">The state is no longer available.</exception>
        public IWorldStateScopeProvider.IScope Open(IWorldStateScopeProvider provider, LocalMetrics metrics) =>
            IsTarget ? provider.BeginScopeAtTarget(Block!, metrics) : provider.BeginScope(Block, metrics);
    }

    private sealed class ScopeWrapper(
        IWorldStateScopeProvider baseProvider,
        PrefetchScopeAnchor prefetchAnchor,
        IWorldStateScopeProvider.IScope baseScope,
        PreBlockCaches preBlockCaches,
        ILogManager logManager,
        bool isPrewarmer,
        PreBlockCaches.StorageReadCapture? storageReadCapture,
        LocalMetrics metrics,
        Hash256? baseStateRoot) : IWorldStateScopeProvider.IScope
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

        // The prefetcher needs an isolated read scope over the same parent; only providers whose
        // scopes can coexist (flat's pooled snapshot bundles) support that. The trie store's scope
        // is a global gate that must not be nested mid-block.
        private readonly bool _stridePrefetchEnabled = !isPrewarmer && baseProvider.SupportsConcurrentScopes;

        // Per contract per block; bounded so a block touching many contracts cannot accumulate
        // reader threads.
        private const int MaxStridePrefetchers = 4;

        // Total engagements allowed per scope. The concurrency cap alone bounds nothing over a whole
        // block: every break frees a slot, so a block crafted from contracts that each stride briefly
        // and then stop could otherwise engage without limit, each engagement creating reader threads
        // and issuing up to a full lookahead window of speculative reads. Bounding engagements also
        // bounds _stridePrefetchers, which keeps a broken entry until its readers are joined.
        private const int MaxStridePrefetcherEngagements = 2 * MaxStridePrefetchers;

        // Detectors are cheap - a few fields fed by the consumer's own reads, no threads and no scope - so they
        // are bounded far above the reader-slot cap. They need their own bound only because the map keeps an
        // entry per storage-touching contract until block end.
        private const int MaxStridePrefetcherDetectors = 512;

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
        private int _stridePrefetcherEngagements;

        public void Dispose()
        {
            // Seals the readers out of the shared cache; joining them and releasing their private
            // scope happens in the background.
            StopStridePrefetchers();
            _prefetchCts.Dispose();

            if (isPrewarmer)
            {
                ObserveWriteBatchToDispose();
                baseScope.Dispose();
                return;
            }

            // Unregister before teardown so no new warm hints target a disposing scope.
            preBlockCaches.MainScope = null;
            try
            {
                ObserveWriteBatchToDispose();
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

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address)
        {
            IWorldStateScopeProvider.IStorageTree baseTree = baseScope.CreateStorageTree(address);
            return storageReadCapture is not null
                ? new CapturingStorageTreeWrapper(baseTree, storageReadCapture, storageCache, address)
                : new StorageTreeWrapper(baseTree, storageCache, address, isPrewarmer, _metrics,
                    _stridePrefetchEnabled ? GetOrCreateStridePrefetcher(address) : null);
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

        public void Commit(ulong blockNumber)
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

        private StorageStridePrefetcher? GetOrCreateStridePrefetcher(Address address)
        {
            // Past the scope's first block (token cancelled at flush/commit) a prefetcher could
            // never engage; skip the detector entirely instead of feeding dead instances.
            if (_prefetchCts.IsCancellationRequested) return null;

            AddressAsKey key = address;
            if (_stridePrefetchers.TryGetValue(key, out StorageStridePrefetcher? existing)) return existing;

            // With the block's engagement budget spent no new contract can engage, so adding detectors
            // would only grow the map — and the scan below — for nothing.
            if (Volatile.Read(ref _stridePrefetcherEngagements) >= MaxStridePrefetcherEngagements) return null;

            if (_stridePrefetchers.Count >= MaxStridePrefetcherDetectors)
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
                    () => CreatePrefetchStorageTree(k.Value),
                    storageCache,
                    k.Value,
                    _prefetchCts.Token,
                    PrefetcherReaderConcurrency,
                    TryReserveStridePrefetcherEngagement));
        }

        private StorageStridePrefetcher.EngagementResult TryReserveStridePrefetcherEngagement()
        {
            // Engagement is requested by the block-processing thread, so the holder count and total
            // engagement budget are checked without another lock. A detector may be created eagerly,
            // but it cannot claim a reader slot until it actually engages.
            if (Volatile.Read(ref _stridePrefetcherEngagements) >= MaxStridePrefetcherEngagements)
            {
                return StorageStridePrefetcher.EngagementResult.Exhausted;
            }

            if (CountReaderSlotHolders() >= MaxStridePrefetchers)
            {
                return StorageStridePrefetcher.EngagementResult.RetryLater;
            }

            Interlocked.Increment(ref _stridePrefetcherEngagements);
            return StorageStridePrefetcher.EngagementResult.Granted;
        }

        private int CountReaderSlotHolders()
        {
            int holders = 0;
            foreach (KeyValuePair<AddressAsKey, StorageStridePrefetcher> kv in _stridePrefetchers)
            {
                if (kv.Value.HoldsReaderSlot) holders++;
            }
            return holders;
        }

        /// <summary>Opens the prefetch readers' shared scope on first use and creates a storage tree on it.</summary>
        /// <remarks>
        /// Reached only from an engaging prefetcher's own thread, never from the block-processing
        /// thread: opening a scope can block (the flat backend retries a snapshot-bundle gather to a
        /// deadline) and engagement is triggered from inside an EVM storage read.
        /// <para>
        /// The lock covers both the lazy open and <c>CreateStorageTree</c>: prefetchers share one scope
        /// and a scope memoizes its storage trees in a non-concurrent dictionary. Nothing on the
        /// block-processing thread takes it — the teardown continuation runs in the background — so it
        /// can never hold up block processing.
        /// </para>
        /// </remarks>
        private IWorldStateScopeProvider.IStorageTree CreatePrefetchStorageTree(Address address)
        {
            lock (_prefetchScopeLock)
            {
                // A private, never-flushed LocalMetrics: the block's own instance is single-threaded
                // by contract, while this scope is shared by the concurrent prefetch readers.
                // Throws when the parent state is gone; the prefetcher treats that as no prefetch for the contract.
                _prefetchScope ??= prefetchAnchor.Open(baseProvider, new LocalMetrics());
                return _prefetchScope.CreateStorageTree(address);
            }
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

            if (_stridePrefetchers.IsEmpty) return;

            List<Task>? readers = null;
            foreach (KeyValuePair<AddressAsKey, StorageStridePrefetcher> kv in _stridePrefetchers)
            {
                Task[] prefetcherReaders = kv.Value.StopAndGetReaders();
                if (prefetcherReaders.Length > 0)
                {
                    (readers ??= []).AddRange(prefetcherReaders);
                }
            }
            _stridePrefetchers.Clear();

            // Nothing engaged, so no shared scope was opened either: only an engaged prefetcher's own
            // thread opens one, and engaging always registers that thread here.
            if (readers is null) return;

            // Join the readers and release their shared scope on a background continuation. A
            // synchronous join would stall block-end on the tail latency of an in-flight,
            // uncancellable storage read — exactly on the striding blocks this targets. Publishing is
            // already sealed (StopAndGetReaders drained the publish latch), so no straggler can reach
            // the next block's cache; deferring only delays disposing the readers' isolated scope
            // until they have all returned.
            Task.WhenAll(readers).ContinueWith(
                static (joined, state) =>
                {
                    // Readers swallow their own failures, so a fault here is unexpected; observe it
                    // rather than let it surface as an unobserved task exception.
                    _ = joined.Exception;

                    ScopeWrapper self = (ScopeWrapper)state!;
                    IWorldStateScopeProvider.IScope? scope;
                    lock (self._prefetchScopeLock)
                    {
                        scope = self._prefetchScope;
                        self._prefetchScope = null;
                    }

                    try
                    {
                        scope?.Dispose();
                    }
                    catch (Exception e)
                    {
                        // The scope is reached here only after all its readers returned; a disposal
                        // racing provider/harness teardown must not fault this continuation.
                        if (self._logger.IsDebug) self._logger.Debug($"Failed to dispose the stride prefetch scope. {e}");
                    }
                },
                this,
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
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
        public void HintWarmAccount(in ValueAddress address)
        {
            if (storageReadCapture is not null) return;
            (isPrewarmer ? mainScope : baseScope)?.HintWarmAccount(in address);
        }

        public void HintWarmSlot(in ValueAddress address, in UInt256 index)
        {
            if (storageReadCapture is not null) return;
            (isPrewarmer ? mainScope : baseScope)?.HintWarmSlot(in address, in index);
        }

        public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null)
        {
            sink ??= new CacheSink(preBlockCache, storageCache);
            return baseScope.HintBal(bal, sink);
        }

        private sealed class CacheSink(
            SeqlockCache<AddressAsKey, Account> stateCache,
            SeqlockCache<StorageCell, UInt256> storageCache
        ) : IWorldStateScopeProvider.IAsyncBalReaderSink
        {
            public void OnAccountRead(Address address, Account? account)
            {
                AddressAsKey key = address;
                stateCache.Set(in key, account);
            }

            public void OnStorageRead(in StorageCell storageCell, in UInt256 value)
                => storageCache.Set(in storageCell, in value);

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
        SeqlockCache<StorageCell, UInt256> preBlockCache,
        Address address,
        bool isPrewarmer,
        LocalMetrics metrics,
        StorageStridePrefetcher? stridePrefetcher = null) : IWorldStateScopeProvider.IStorageTree
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
            stridePrefetcher?.OnRead(in index);

            StorageCell storageCell = new(address, in index); // TODO: Make the dictionary use UInt256 directly
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
        Address address) : IWorldStateScopeProvider.IStorageTree
    {
        public Hash256 RootHash => baseStorageTree.RootHash;

        public void Get(in UInt256 index, out UInt256 value)
        {
            StorageCell storageCell = new(address, in index);
            if (preBlockCache.TryGetValue(in storageCell, out value))
            {
                return;
            }

            storageReadCapture.Record(in storageCell);
            // Nonzero keeps common existence checks and bounded loops progressing to reveal later reads.
            value = UInt256.One;
        }

        public void HintSet(in UInt256 index) => baseStorageTree.HintSet(in index);
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
