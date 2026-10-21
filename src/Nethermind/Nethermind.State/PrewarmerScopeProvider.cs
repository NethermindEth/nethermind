// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
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
    private readonly CommittedStorageWrites? committedWrites = prewarmerState.CommittedWrites;
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
                // Opening stops any speculative session, so the check below and the scope's reads see no other writer.
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
        // The prefetcher needs an isolated read scope over the same parent; only providers whose
        // scopes can coexist (flat's pooled snapshot bundles) support that. The trie store's scope
        // is a global gate that must not be nested mid-block. Populator scopes never prefetch, and
        // the prewarmer builds one per worker per pass, so they skip the bookkeeping entirely.
        StridePrefetchers? stridePrefetchers = !isPrewarmer && baseProvider.SupportsConcurrentScopes
            ? new StridePrefetchers(baseProvider, prefetchAnchor, preBlockCaches.StorageCache, logger)
            : null;
        return new ScopeWrapper(scope, preBlockCaches, logManager, isPrewarmer, storageReadCapture, metrics, stateRoot, codeCache, prefetchCode, committedWrites, stridePrefetchers);
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
        IWorldStateScopeProvider.IScope baseScope,
        PreBlockCaches preBlockCaches,
        ILogManager logManager,
        bool isPrewarmer,
        PreBlockCaches.StorageReadCapture? storageReadCapture,
        LocalMetrics metrics,
        Hash256? baseStateRoot,
        ICodeCache? codeCache,
        bool prefetchCode,
        CommittedStorageWrites? committedWrites,
        StridePrefetchers? stridePrefetchers) : IWorldStateScopeProvider.IScope
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
        // Set once the block's state came from a BAL, holding its final values for the write-back. The caches still
        // describe the pre-block state the parallel workers read, so this scope must neither read nor backfill them
        // until the write-back moves them forward.
        private ArrayPoolList<AppliedAccount>? _appliedBalAccounts;
        private readonly PrefetchedCodeDb _codeDb = new(baseScope.CodeDb, preBlockCaches);

        public void Dispose()
        {
            // Seals the readers out of the shared cache; joining them and releasing their private
            // scope happens in the background.
            stridePrefetchers?.Dispose();
            _appliedBalAccounts?.Dispose();
            _appliedBalAccounts = null;
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
            if (_appliedBalAccounts is not null) return baseTree;
            // Asked once per tree: a tree serves one block, and the set only changes in a write-back between blocks.
            bool bypassCache = preBlockCaches.BypassesStorageCache(address);
            return storageReadCapture is not null
                ? new CapturingStorageTreeWrapper(baseTree, storageReadCapture, storageCache, address, bypassCache)
                : new StorageTreeWrapper(baseTree, preBlockCaches, address, isPrewarmer, _metrics, bypassCache, committedWrites,
                    // A bypassed contract reads past the cache, so warming the cache for it would buy nothing.
                    bypassCache ? null : stridePrefetchers?.GetOrCreate(address));
        }

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum)
        {
            // The batch is about to land this block's writes in the live scope, after which
            // parent-state prefetches are no longer useful; stop the readers here, mirroring how
            // the flat scope cancels its own background warmers around write batches.
            stridePrefetchers?.Stop();

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
            stridePrefetchers?.Stop();

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
            ArrayPoolList<AppliedAccount>? appliedBalAccounts = _appliedBalAccounts;
            _appliedBalAccounts = null;
            bool handedOver = false;
            try
            {
                if (isPrewarmer) return;

                Hash256 stateRoot = baseScope.RootHash;
                // An unchanged root means the block changed nothing, or the scope computes no roots (a trieless one) and its
                // committed values would be tagged with the pre-block root: either way there is nothing to bring forward.
                if (stateRoot == _committedStateRoot) return;

                Hash256? baseStateRoot = _committedStateRoot;
                _committedStateRoot = stateRoot;
                if (appliedBalAccounts is not null)
                {
                    // The world state never saw the BAL's writes, so its snapshot is preceded by the BAL's final values.
                    Func<IWorldStateScopeProvider.IBlockChangeSnapshot> takeWorldStateSnapshot = takeSnapshot;
                    takeSnapshot = () =>
                    {
                        IWorldStateScopeProvider.IBlockChangeSnapshot worldStateSnapshot = takeWorldStateSnapshot();
                        handedOver = true;
                        return new AppliedBalChangeSnapshot(appliedBalAccounts, worldStateSnapshot);
                    };
                }

                preBlockCaches.WriteBackInBackground(baseStateRoot, stateRoot, takeSnapshot, _logger);
            }
            finally
            {
                if (!handedOver) appliedBalAccounts?.Dispose();
            }
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
            if (_appliedBalAccounts is not null) return baseScope.Get(address);

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
                if (!isPrewarmer) preBlockCaches.ReportConsumerColdRead();
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

        public void ApplyBal(ReadOnlyBlockAccessList bal, CancellationToken cancellationToken)
        {
            ArrayPoolList<AppliedAccount> accounts = new(bal.AccountChanges.Count);
            try
            {
                foreach (ReadOnlyAccountChanges accountChanges in bal.AccountChanges)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    if (!accountChanges.HasStateChanges) continue;

                    // The caches still hold the pre-block state the apply reads, so they spare it the backend reads. An
                    // account committed before the apply was already hinted with its committed value, which this cannot replace.
                    AddressAsKey key = accountChanges.Address;
                    bool cached = preBlockCache.TryGetValue(in key, out Account? preBlockAccount);
                    if (cached) baseScope.HintGet(accountChanges.Address, preBlockAccount);

                    // Whether a removal took storage along is known only when cached.
                    bool mayRemoveStorage = !cached || preBlockAccount is { HasStorage: true } || !baseScope.StorageRootsAreAuthoritative;
                    // A revived account keeps only the slots written after its removal; the scope's own read keeps this exact.
                    BalAccountDelta? revival = BalAccountDelta.CanEmpty(accountChanges)
                        && BalAccountDelta.Compute(baseScope.Get(accountChanges.Address), accountChanges) is { WipesStorage: true, Account: not null } delta
                        ? delta
                        : null;
                    accounts.Add(new AppliedAccount(accountChanges, mayRemoveStorage, revival));
                }

                baseScope.ApplyBal(bal, cancellationToken);

                // Read here, alongside the transactions, rather than on the commit path.
                foreach (ref AppliedAccount appliedAccount in accounts.AsSpan())
                {
                    appliedAccount.Account = baseScope.Get(appliedAccount.Changes.Address);
                }
            }
            catch
            {
                accounts.Dispose();
                throw;
            }

            _appliedBalAccounts?.Dispose();
            _appliedBalAccounts = accounts;
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

    /// <summary>
    /// The storage stride prefetchers of one consumer scope: a detector per storage-touching contract, the budget
    /// they engage against, and the isolated read scope their readers share.
    /// </summary>
    private sealed class StridePrefetchers(
        IWorldStateScopeProvider baseProvider,
        PrefetchScopeAnchor prefetchAnchor,
        SeqlockCache<StorageCell, UInt256> storageCache,
        ILogger logger) : IDisposable
    {
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

        private readonly ILogger _logger = logger;
        private readonly ConcurrentDictionary<AddressAsKey, StorageStridePrefetcher> _stridePrefetchers = new();
        private readonly CancellationTokenSource _prefetchCts = new();
        private readonly Lock _prefetchScopeLock = new();
        private IWorldStateScopeProvider.IScope? _prefetchScope;
        private int _stridePrefetcherEngagements;

        public StorageStridePrefetcher? GetOrCreate(Address address)
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

        public void Stop()
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

                    StridePrefetchers self = (StridePrefetchers)state!;
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

        /// <summary>Stops the prefetchers and releases the token source their readers observe.</summary>
        public void Dispose()
        {
            Stop();
            _prefetchCts.Dispose();
        }
    }

    /// <summary>An account a BAL changed, with its final value once applied.</summary>
    private struct AppliedAccount(ReadOnlyAccountChanges changes, bool mayRemoveStorage, BalAccountDelta? revival)
    {
        public readonly ReadOnlyAccountChanges Changes = changes;
        public readonly bool MayRemoveStorage = mayRemoveStorage;
        /// <summary>Set when the BAL removed the account and then recreated it, wiping its earlier storage.</summary>
        public readonly BalAccountDelta? Revival = revival;
        public Account? Account;
    }

    /// <inheritdoc cref="IWorldStateScopeProvider.IBlockChangeSnapshot"/>
    private sealed class AppliedBalChangeSnapshot(
        ArrayPoolList<AppliedAccount> accounts,
        IWorldStateScopeProvider.IBlockChangeSnapshot worldStateChanges) : IWorldStateScopeProvider.IBlockChangeSnapshot
    {
        public void WriteTo(IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch)
        {
            foreach (AppliedAccount appliedAccount in accounts.AsSpan())
            {
                ReadOnlyAccountChanges changes = appliedAccount.Changes;
                Account? account = appliedAccount.Account;
                writeBatch.Set(changes.Address, account);
                if (!writeBatch.AcceptsStorageWrites) continue;

                if (account is null)
                {
                    if (!appliedAccount.MayRemoveStorage) continue;

                    using IWorldStateScopeProvider.IStorageWriteBatch clearBatch = writeBatch.CreateStorageWriteBatch(changes.Address, 0);
                    clearBatch.Clear();
                    continue;
                }

                if (appliedAccount.Revival is { } revival)
                {
                    using IWorldStateScopeProvider.IStorageWriteBatch revivedBatch = writeBatch.CreateStorageWriteBatch(changes.Address, changes.StorageChanges.Length);
                    revivedBatch.Clear();
                    foreach (ReadOnlySlotChanges slotChanges in changes.StorageChanges)
                    {
                        if (revival.TryGetFinalValue(slotChanges, out UInt256 value)) revivedBatch.Set(slotChanges.Key, value);
                    }
                    continue;
                }

                if (changes.StorageChanges.Length == 0) continue;

                using IWorldStateScopeProvider.IStorageWriteBatch storageWriteBatch = writeBatch.CreateStorageWriteBatch(changes.Address, changes.StorageChanges.Length);
                foreach (ReadOnlySlotChanges slotChanges in changes.StorageChanges)
                {
                    if (slotChanges.Changes.Length > 0) storageWriteBatch.Set(slotChanges.Key, slotChanges.Changes[^1].Value);
                }
            }

            worldStateChanges.WriteTo(writeBatch);
        }

        public void Dispose()
        {
            try
            {
                accounts.Dispose();
            }
            finally
            {
                worldStateChanges.Dispose();
            }
        }
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
        PreBlockCaches preBlockCaches,
        Address address,
        bool isPrewarmer,
        LocalMetrics metrics,
        bool bypassCache,
        CommittedStorageWrites? committedWrites,
        StorageStridePrefetcher? stridePrefetcher = null) : IWorldStateScopeProvider.IStorageTree
    {
        private readonly IWorldStateScopeProvider.IStorageTree baseStorageTree = baseStorageTree;
        private readonly PreBlockCaches preBlockCaches = preBlockCaches;
        private readonly SeqlockCache<StorageCell, UInt256> preBlockCache = preBlockCaches.StorageCache;
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
            if (isPrewarmer)
            {
                baseStorageTree.HintSet(in index);
                return;
            }

            committedWrites?.Add(address, in index, in value);
            baseStorageTree.HintSet(in index, in value);
        }

        // Keeps the miss path inlined into Get: without the attribute the JIT inlines only the bypass call.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void LoadFromTreeStorage(in StorageCell storageCell, out UInt256 value)
        {
            // PreBlock misses only (consumer scope): StorageTreeReads is already counted once per
            // first-in-block touch by PersistentStorageProvider; counting it here again double-counted
            // fully-cold reads. Populator probes are excluded — they miss by design while filling.
            if (!isPrewarmer)
            {
                _metrics.IncrementPreBlockStorageMisses();
                preBlockCaches.ReportConsumerColdRead();
            }
            else
            {
                ColdReadWatch.Read();
            }

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
                storageReadCapture.RecordRead(in storageCell);
                return;
            }

            storageReadCapture.Record(in storageCell);
            // Nonzero keeps common existence checks and bounded loops progressing to reveal later reads.
            value = storageReadCapture.Placeholder;
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
