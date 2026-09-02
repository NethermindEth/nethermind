// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Collections.Pooled;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Threading;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Evm.State;
using Nethermind.Core.Eip2930;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Trie;

namespace Nethermind.Consensus.Processing;

public sealed class BlockCachePreWarmer : IBlockCachePreWarmer
{
    private const int MinTransactionsForReactiveWarming = 3;

    private readonly int _concurrencyLevel;
    // Speculative warming runs in the idle gap alongside RPC, so it is capped below the reactive level to leave cores free.
    private readonly int _speculativeConcurrencyLevel;
    private readonly bool _parallelExecutionBatchRead;
    private readonly ObjectPool<IReadOnlyTxProcessorSource> _envPool;
    private readonly ILogger _logger;
    private readonly PreBlockCaches _preBlockCaches;
    private readonly NodeStorageCache _nodeStorageCache;
    private readonly bool _parallelExecutionEnabled;

    private const int MaxDiscoveryCandidates = 16;
    private const int MaxDiscoveryRounds = 6;
    internal const int MaxDiscoveredCells = 8192;
    // Iterative discovery is substantially costlier than ordinary warmup; reserve it for exceptional transactions.
    private const ulong StorageDiscoveryGasThreshold = 10_000_000;

    private static readonly IComparer<StorageCell> _cellAddressComparer =
        Comparer<StorageCell>.Create(static (left, right) => left.Address.CompareTo(right.Address));

    private int _mainThreadTxIndex = -1;
    internal int MainThreadTxIndex => Volatile.Read(ref _mainThreadTxIndex);

    // A session is always joined (under _speculativeLock) before the reactive path touches the shared caches.
    private readonly Lock _speculativeLock = new();
    private CancellationTokenSource? _speculativeCts;
    private Task _speculativeTask = Task.CompletedTask;
    private long _speculativeGeneration = long.MinValue;

    // Non-null writes come only from the speculative loop; every other writer nulls it after joining that loop,
    // which is what makes the marker and its shared tx-hash set safe to read without further sync.
    private WarmMarker? _warmMarker;

    private readonly PooledSet<Hash256> _warmedTxHashes = [];
    private readonly IHasAccessList[] _systemAccessLists;

    public BlockCachePreWarmer(
        PrewarmerEnvFactory envFactory,
        IBlocksConfig blocksConfig,
        NodeStorageCache nodeStorageCache,
        PreBlockCaches preBlockCaches,
        ILogManager logManager,
        IHasAccessList[]? systemAccessLists = null
    ) : this(
        new ReadOnlyTxProcessingEnvPooledObjectPolicy(envFactory, preBlockCaches),
        Environment.ProcessorCount * 2,
        blocksConfig.PreWarmStateConcurrency,
        blocksConfig.ParallelExecutionBatchRead,
        nodeStorageCache,
        preBlockCaches,
        logManager,
        blocksConfig.MempoolPreWarmConcurrency,
        systemAccessLists) => _parallelExecutionEnabled = blocksConfig.ParallelExecution;

    internal BlockCachePreWarmer(
        IPooledObjectPolicy<IReadOnlyTxProcessorSource> poolPolicy,
        int minPoolSize,
        int concurrency,
        bool parallelExecutionBatchRead,
        NodeStorageCache nodeStorageCache,
        PreBlockCaches preBlockCaches,
        ILogManager logManager,
        int speculativeConcurrency = 0,
        IHasAccessList[]? systemAccessLists = null)
    {
        _systemAccessLists = systemAccessLists ?? [];
        _concurrencyLevel = concurrency == 0 ? Math.Min(Environment.ProcessorCount - 1, 16) : concurrency;
        _speculativeConcurrencyLevel = speculativeConcurrency == 0 ? Math.Max(1, _concurrencyLevel / 2) : speculativeConcurrency;
        _parallelExecutionBatchRead = parallelExecutionBatchRead;
        // minPoolSize is a floor: the address warmer, transaction warmup, and storage discovery rent
        // concurrently, each up to _concurrencyLevel, so retention is sized for all three renters.
        _envPool = new DefaultObjectPoolProvider { MaximumRetained = Math.Max(minPoolSize, _concurrencyLevel * 3 + 1) }.Create(poolPolicy);
        _logger = logManager.GetClassLogger<BlockCachePreWarmer>();
        _preBlockCaches = preBlockCaches;
        _nodeStorageCache = nodeStorageCache;
        // A consumer scope and a speculative session never coexist: the session is joined the moment a consumer opens.
        if (_preBlockCaches is not null) _preBlockCaches.ConsumerScopeOpened += CancelAndJoinSpeculative;
    }

    public Task PreWarmCaches(Block suggestedBlock, BlockHeader? parent, IReleaseSpec spec, CancellationToken cancellationToken = default)
    {
        // Join ahead of the gate: the session's spec comes from a synthetic next-block header, so it can enable warming
        // for a spec this block disables (a fork boundary), and no pass may run into execution.
        if (_preBlockCaches is null)
        {
            CancelAndJoinSpeculative();
            return Task.CompletedTask;
        }

        bool carried;
        lock (_speculativeLock)
        {
            CancelAndJoinSpeculativeLocked();
            // A spec that disables warming still needs the keep-or-clear decision: joining stops a session from writing
            // further, but the caches describe the state they were filled from, which need not be this block's parent.
            carried = _preBlockCaches.PrepareFor(parent?.StateRoot);
        }

        bool skipReactiveWarming = !ShouldPreWarm(spec) || ShouldSkipReactiveWarming(suggestedBlock, spec);
        // The marker's tx set only means anything while the entries it describes are still in the caches.
        ISet<Hash256>? speculativelyWarmed =
            TryConsumeWarmMarker(suggestedBlock.ParentHash, spec, out ISet<Hash256>? warmed) && carried ? warmed : null;
        if (carried)
        {
            // The caches hold this parent's state, so keep RLP caching on for execution.
            _nodeStorageCache.Enabled = true;
        }
        else
        {
            _nodeStorageCache.ClearCaches();
            // Without retained caches or a reactive pass, leave RLP caching disabled for execution.
            if (skipReactiveWarming) return Task.CompletedTask;
            _nodeStorageCache.Enabled = true;
        }

        if (skipReactiveWarming) return Task.CompletedTask;
        return WarmCaches(suggestedBlock, parent, spec, speculativelyWarmed, cancellationToken, _systemAccessLists);
    }

    private Task WarmCaches(Block suggestedBlock, BlockHeader? parent, IReleaseSpec spec, ISet<Hash256>? speculativelyWarmed, CancellationToken cancellationToken, ReadOnlySpan<IHasAccessList> systemAccessLists)
    {
        if (parent is null || _concurrencyLevel <= 1 || cancellationToken.IsCancellationRequested) return Task.CompletedTask;

        (BlockState blockState, ParallelOptions parallelOptions, AddressWarmer addressWarmer) = PrepareWarm(suggestedBlock, parent, spec, speculativelyWarmed, _concurrencyLevel, cancellationToken, systemAccessLists);
        // A block access list already enumerates the block's reads; discovery adds nothing.
        List<(int Index, Transaction Tx)>? discoveryCandidates = addressWarmer.HasBal
            ? null
            : SelectDiscoveryCandidates(suggestedBlock, speculativelyWarmed);
        // Run address warmer ahead of transactions warmer, but queue to ThreadPool so it doesn't block the txs
        ThreadPool.UnsafeQueueUserWorkItem(addressWarmer, preferLocal: false);
        // Do not pass the cancellation token to the task, we don't want exceptions to be thrown in the main processing thread
        Task normalWarmTask = Task.Run(() => PreWarmCachesParallel(blockState, suggestedBlock, parent, spec, parallelOptions, addressWarmer, cancellationToken));

        if (discoveryCandidates is null) return normalWarmTask;

        Task discoveryTask = Task.Run(() => DiscoverAndWarmStorageSafely(discoveryCandidates, suggestedBlock, parent, spec, cancellationToken));
        return Task.WhenAll(normalWarmTask, discoveryTask);
    }

    private void DiscoverAndWarmStorageSafely(List<(int Index, Transaction Tx)> candidates, Block block, BlockHeader parent, IReleaseSpec spec, CancellationToken cancellationToken)
    {
        try
        {
            DiscoverAndWarmStorage(candidates, block, parent, spec, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.DebugWarn($"Error discovering storage reads for block {block.Number}. {ex}");
        }
    }

    internal static List<(int Index, Transaction Tx)>? SelectDiscoveryCandidates(Block block, ISet<Hash256>? speculativelyWarmed)
    {
        List<(int Index, Transaction Tx)>? candidates = null;

        Transaction[] transactions = block.Transactions;
        for (int i = 0; i < transactions.Length; i++)
        {
            Transaction tx = transactions[i];
            if (tx.GasLimit <= StorageDiscoveryGasThreshold || tx.SenderAddress is null || tx.To is null) continue;
            if (speculativelyWarmed is not null && tx.Hash is Hash256 hash && speculativelyWarmed.Contains(hash)) continue;

            (candidates ??= new(MaxDiscoveryCandidates)).Add((i, tx));
            if (candidates.Count == MaxDiscoveryCandidates) break;
        }

        return candidates;
    }

    internal void DiscoverAndWarmStorage(List<(int Index, Transaction Tx)> candidates, Block block, BlockHeader parent, IReleaseSpec spec, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return;

        using PooledSet<StorageCell> allDiscoveredCells = [];
        using PooledSet<StorageCell> roundCells = [];
        Lock roundCellsLock = new();
        // Copy: candidate lists are round-local state from here on.
        List<(int Index, Transaction Tx)> currentCandidates = [.. candidates];
        List<(int Index, Transaction Tx)> nextRoundCandidates = new(candidates.Count);
        List<(int Index, Transaction Tx)> admitted = new(candidates.Count);
        List<(int Index, Transaction Tx)> deferred = new(candidates.Count);

        for (int round = 0; round < MaxDiscoveryRounds && currentCandidates.Count > 0; round++)
        {
            roundCells.Clear();
            nextRoundCandidates.Clear();
            admitted.Clear();
            deferred.Clear();
            SplitByRoundGasBudget(currentCandidates, block.GasLimit, admitted, deferred);
            if (admitted.Count == 0) return;

            int cellBudget = MaxDiscoveredCells - allDiscoveredCells.Count;
            DiscoveryRound roundState = new(block, parent, spec, cellBudget, new StrongBox<int>(cellBudget), roundCells, roundCellsLock, nextRoundCandidates);
            ParallelOptions parallelOptions = new()
            {
                MaxDegreeOfParallelism = Math.Min(_concurrencyLevel, admitted.Count),
                CancellationToken = cancellationToken
            };

            try
            {
                Parallel.ForEach(admitted, parallelOptions, candidate =>
                    DiscoverTransactionStorageReads(candidate, roundState));
            }
            catch (OperationCanceledException)
            {
                return;
            }

            roundCells.ExceptWith(allDiscoveredCells);
            if (cancellationToken.IsCancellationRequested) return;

            if (roundCells.Count > 0)
            {
                allDiscoveredCells.UnionWith(roundCells);
                if (!WarmDiscoveredStorage(parent, roundCells, cancellationToken)) return;
                if (allDiscoveredCells.Count >= MaxDiscoveredCells) return;
            }
            else if (deferred.Count == 0 || nextRoundCandidates.Count == admitted.Count)
            {
                // No progress and no budget freed for the deferred — the next round would repeat this one.
                return;
            }

            // Survivors: productive candidates plus the ones this round's budget deferred, back in block order.
            nextRoundCandidates.AddRange(deferred);
            nextRoundCandidates.Sort(static (a, b) => a.Index.CompareTo(b.Index));
            (currentCandidates, nextRoundCandidates) = (nextRoundCandidates, currentCandidates);
        }
    }

    /// <summary>Splits candidates into the greedy first-fit set (in block order) whose declared gas fits one block re-execution, and the deferred rest.</summary>
    /// <remarks>
    /// Declared limits are unvalidated under SkipValidation and placeholder values can steer execution into
    /// paths real execution never takes, so each round's speculative work is capped at re-executing the block
    /// once. Splitting anew each round is deterministic and lets budget freed by dropped candidates be
    /// reclaimed by deferred ones. A persistently productive low-index candidate keeps its admission every
    /// round, deferring later ones for the whole discovery window — deliberate: finishing one read chain
    /// beats time-slicing several.
    /// </remarks>
    internal static void SplitByRoundGasBudget(
        List<(int Index, Transaction Tx)> candidates,
        ulong blockGasLimit,
        List<(int Index, Transaction Tx)> admitted,
        List<(int Index, Transaction Tx)> deferred)
    {
        long remaining = (long)Math.Min(blockGasLimit, (ulong)long.MaxValue);
        foreach ((int Index, Transaction Tx) candidate in candidates)
        {
            long cost = (long)Math.Min(candidate.Tx.GasLimit, (ulong)long.MaxValue);
            if (cost <= remaining)
            {
                remaining -= cost;
                admitted.Add(candidate);
            }
            else
            {
                deferred.Add(candidate);
            }
        }
    }

    /// <summary>Shared state of one discovery round.</summary>
    private sealed class DiscoveryRound(
        Block block,
        BlockHeader parent,
        IReleaseSpec spec,
        int cellBudget,
        StrongBox<int> remainingCaptureCells,
        PooledSet<StorageCell> cells,
        Lock cellsLock,
        List<(int Index, Transaction Tx)> nextRoundCandidates)
    {
        public readonly Block Block = block;
        public readonly BlockHeader Parent = parent;
        public readonly IReleaseSpec Spec = spec;
        public readonly int CellBudget = cellBudget;
        public readonly StrongBox<int> RemainingCaptureCells = remainingCaptureCells;
        public readonly PooledSet<StorageCell> Cells = cells;
        public readonly Lock CellsLock = cellsLock;
        public readonly List<(int Index, Transaction Tx)> NextRoundCandidates = nextRoundCandidates;
    }

    private void DiscoverTransactionStorageReads((int Index, Transaction Tx) candidate, DiscoveryRound round)
    {
        // Already started by the main thread — warming it now is redundant and contends; skip.
        if (MainThreadTxIndex >= candidate.Index) return;

        Transaction tx = candidate.Tx;
        IReadOnlyTxProcessorSource env = _envPool.Get();
        try
        {
            using PreBlockCaches.StorageReadCapture capture = _preBlockCaches.BeginStorageReadCapture(round.RemainingCaptureCells);
            using IReadOnlyTxProcessingScope scope = env.Build(round.Parent);
            scope.TransactionProcessor.SetBlockExecutionContext(new BlockExecutionContext(round.Block.Header, round.Spec));

            try
            {
                IWorldState worldState = scope.WorldState;
                Address senderAddress = tx.SenderAddress!;
                if (!worldState.AccountExists(senderAddress))
                {
                    worldState.CreateAccountIfNotExists(senderAddress, UInt256.Zero);
                }

                // Access-list cells are not warmed here: the normal warm task covers them, and reading
                // them under the capture would re-record already-covered cells as discovered.
                scope.TransactionProcessor.Warmup(tx, NullTxTracer.Instance);
            }
            catch (Exception ex) when (ex is EvmException or OverflowException)
            {
                if (_logger.IsTrace) _logger.Trace($"Discovery execution of {tx.Hash} stopped on {ex.GetType().Name}");
            }

            if (capture.Cells.Count == 0) return;

            using (round.CellsLock.EnterScope())
            {
                round.NextRoundCandidates.Add(candidate);
                int added = 0;
                bool roundFull = false;
                foreach (StorageCell cell in capture.Cells)
                {
                    if (round.Cells.Count >= round.CellBudget)
                    {
                        roundFull = true;
                        break;
                    }

                    if (round.Cells.Add(cell)) added++;
                }

                // Refund cells that did not extend the round (duplicates across captures), so overlapping
                // candidates of one heavy contract don't multiply-charge the shared budget — but not when
                // the round is saturated, or concurrent captures would keep recording cells only to be discarded.
                int unused = capture.Cells.Count - added;
                if (unused > 0 && !roundFull) Interlocked.Add(ref round.RemainingCaptureCells.Value, unused);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.DebugError($"Error discovering storage reads for {tx.Hash}", ex);
        }
        finally
        {
            _envPool.Return(env);
        }
    }

    private bool WarmDiscoveredStorage(BlockHeader parent, PooledSet<StorageCell> discoveredCells, CancellationToken cancellationToken)
    {
        int cellCount = discoveredCells.Count;
        StorageCell[] cells = ArrayPool<StorageCell>.Shared.Rent(cellCount);
        try
        {
            discoveredCells.CopyTo(cells, 0);
            // Sorting by address keeps a contract's slots adjacent, so most partitions resolve one account and storage root.
            Array.Sort(cells, 0, cellCount, _cellAddressComparer);
            ParallelOptions parallelOptions = new()
            {
                MaxDegreeOfParallelism = Math.Min(_concurrencyLevel, cellCount),
                CancellationToken = cancellationToken
            };

            // Wide ranges so one scope build serves many reads rather than a handful.
            int rangeSize = Math.Max(16, cellCount / (parallelOptions.MaxDegreeOfParallelism * 4));

            // Reads through a prewarmer scope populate PreBlockCaches, so plain parallel reads are the warm-up.
            Parallel.ForEach(Partitioner.Create(0, cellCount, rangeSize), parallelOptions, range =>
            {
                IReadOnlyTxProcessorSource env = _envPool.Get();
                try
                {
                    using IReadOnlyTxProcessingScope scope = env.Build(parent);
                    IWorldState worldState = scope.WorldState;
                    int unreadable = 0;
                    for (int i = range.Item1; i < range.Item2; i++)
                    {
                        if (((i - range.Item1) & 0x3F) == 0 && cancellationToken.IsCancellationRequested) return;
                        try
                        {
                            worldState.Get(in cells[i]);
                        }
                        catch (MissingTrieNodeException)
                        {
                            unreadable++;
                        }
                    }

                    if (unreadable > 0 && _logger.IsTrace) _logger.Trace($"Skipped {unreadable} discovered cells with missing trie nodes");
                }
                finally
                {
                    _envPool.Return(env);
                }
            });
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.DebugError("Error warming discovered storage reads", ex);
            return false;
        }
        finally
        {
            ArrayPool<StorageCell>.Shared.Return(cells, clearArray: true);
        }
    }

    private void WarmDeltaSync(Block delta, BlockHeader head, IReleaseSpec spec, CancellationToken token)
    {
        (BlockState blockState, ParallelOptions parallelOptions, AddressWarmer addressWarmer) = PrepareWarm(delta, head, spec, speculativelyWarmed: null, _speculativeConcurrencyLevel, token, systemAccessLists: default);
        ThreadPool.UnsafeQueueUserWorkItem(addressWarmer, preferLocal: false);
        PreWarmCachesParallel(blockState, delta, head, spec, parallelOptions, addressWarmer, token);
    }

    private (BlockState BlockState, ParallelOptions ParallelOptions, AddressWarmer AddressWarmer) PrepareWarm(Block block, BlockHeader parent, IReleaseSpec spec, ISet<Hash256>? speculativelyWarmed, int maxDegreeOfParallelism, CancellationToken token, ReadOnlySpan<IHasAccessList> systemAccessLists)
    {
        BlockState blockState = new(this, block, parent, spec, speculativelyWarmed);
        // Safe for the speculative caller: it never overlaps main execution (joined before ProcessOne).
        Volatile.Write(ref _mainThreadTxIndex, -1);
        ParallelOptions parallelOptions = new() { MaxDegreeOfParallelism = maxDegreeOfParallelism, CancellationToken = token };
        // BAL makes speculative tx execution redundant — when BAL-based read warming is in use, drive warmup
        // directly off the block's access list.
        ReadOnlyBlockAccessList? bal = IsBalReadWarmingEnabled(spec) ? block.BlockAccessList : null;
        AddressWarmer addressWarmer = new(parallelOptions, block, parent, spec, systemAccessLists, this, bal);
        return (blockState, parallelOptions, addressWarmer);
    }

    public Task StartSpeculativePreWarm(BlockHeader head, IReleaseSpec spec, long generation, Func<CancellationToken, Block?> nextDelta, int idlePassDelayMs, CancellationToken cancellationToken)
    {
        if (_preBlockCaches is null || !ShouldPreWarm(spec) || _concurrencyLevel <= 1) return Task.CompletedTask;
        if (head.Hash is not Hash256 headHash) return Task.CompletedTask;

        lock (_speculativeLock)
        {
            // An equal-or-newer session already started (out-of-order work item); don't clobber it.
            if (generation <= _speculativeGeneration) return _speculativeTask;
            // A consumer is open: the caches describe its parent, and a late session for another head must not
            // repurpose them underneath it. Its head's own successor will start a session of its own.
            if (_preBlockCaches.ConsumerScopeOpen) return Task.CompletedTask;
            _speculativeGeneration = generation;

            CancelAndJoinSpeculativeLocked();

            CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _speculativeCts = cts;
            CancellationToken token = cts.Token;

            ClearWarmMarker();
            _warmedTxHashes.Clear();
            _preBlockCaches.PrepareFor(head.StateRoot);
            _nodeStorageCache.ClearCaches();
            _nodeStorageCache.Enabled = true;

            return _speculativeTask = Task.Run(() => RunSpeculativeLoop(headHash, head, spec, nextDelta, idlePassDelayMs, token));
        }
    }

    private void RunSpeculativeLoop(Hash256 headHash, BlockHeader head, IReleaseSpec spec, Func<CancellationToken, Block?> nextDelta, int idlePassDelayMs, CancellationToken token)
    {
        // _warmedTxHashes is reused across sessions (cleared at session start); only the small marker is per-session.
        WarmMarker marker = new(headHash, spec, _warmedTxHashes);
        try
        {
            int delay = Math.Max(1, idlePassDelayMs);
            while (!token.IsCancellationRequested)
            {
                Block? delta = nextDelta(token);
                if (token.IsCancellationRequested) break;

                if (delta is not null && delta.Transactions.Length > 0)
                {
                    WarmDeltaSync(delta, head, spec, token);
                    // Don't record a delta cancelled mid-warm, or the reactive pass would skip a half-warmed sender.
                    if (token.IsCancellationRequested) break;
                    foreach (Transaction tx in delta.Transactions)
                    {
                        if (tx.Hash is Hash256 hash) _warmedTxHashes.Add(hash);
                    }
                    Volatile.Write(ref _warmMarker, marker);
                }

                // Rate-limit every pass so a churning mempool can't keep tx selection continuously in flight.
                if (token.WaitHandle.WaitOne(delay)) break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.DebugWarn($"Error during speculative pre-warming. {ex}");
        }
    }

    // For tests: true once a session has published its handoff marker.
    internal bool SpeculativeMarkerPublished => Volatile.Read(ref _warmMarker) is not null;

    private void CancelAndJoinSpeculative()
    {
        lock (_speculativeLock)
        {
            CancelAndJoinSpeculativeLocked();
        }
    }

    private void CancelAndJoinSpeculativeLocked()
    {
        if (_speculativeCts is null) return;

        _speculativeCts.Cancel();
        try
        {
            _speculativeTask.GetAwaiter().GetResult();
        }
        catch
        {
            // Warming failures are already logged inside the pass; nothing actionable here.
        }
        _speculativeCts.Dispose();
        _speculativeCts = null;
        _speculativeTask = Task.CompletedTask;
    }

    private void ClearWarmMarker() => Volatile.Write(ref _warmMarker, null);

    private bool TryConsumeWarmMarker(Hash256? parentHash, IReleaseSpec spec, out ISet<Hash256>? warmedTxHashes)
    {
        WarmMarker? marker = Interlocked.Exchange(ref _warmMarker, null);
        // ReferenceEquals on the per-fork spec singleton: a mismatch only disables the handoff, never a correctness issue.
        if (marker is not null && parentHash is not null && marker.ParentHash == parentHash && ReferenceEquals(marker.Spec, spec))
        {
            warmedTxHashes = marker.WarmedTxHashes;
            return true;
        }

        warmedTxHashes = null;
        return false;
    }

    private bool ShouldPreWarm(IReleaseSpec spec)
        => !_parallelExecutionEnabled
        || !spec.BlockLevelAccessListsEnabled
        || IsBalReadWarmingEnabled(spec);

    // Tiny blocks normally don't justify reactive warming overhead. BAL read warming is cheap
    // and remains useful regardless of transaction count.
    private bool ShouldSkipReactiveWarming(Block block, IReleaseSpec spec)
        => block.Transactions.Length < MinTransactionsForReactiveWarming
        && !(IsBalReadWarmingEnabled(spec) && block.BlockAccessList is not null);

    public bool IsBalReadWarmingEnabled(IReleaseSpec spec)
        => _parallelExecutionBatchRead && spec.BlockLevelAccessListsEnabled;

    /// <summary>Reports main-thread progress (called via <see cref="PrewarmerTxAdapter"/>) so warming can skip already-started txs.</summary>
    /// <remarks>Only the single main execution thread writes, in ascending tx order, so a plain release store publishes progress to the polling warmup workers — no interlocked read-modify-write is needed.</remarks>
    public void OnBeforeTxExecution() => Volatile.Write(ref _mainThreadTxIndex, _mainThreadTxIndex + 1);

    public CacheType ClearCaches()
    {
        if (_logger.IsDebug) _logger.Debug("Clearing caches");
        CancelAndJoinSpeculative();
        ClearWarmMarker();
        // The account and storage caches carry over: the block's commit wrote its final values into them, and PrepareFor
        // keeps or clears them before the next use.
        _preBlockCaches?.ClearPrecompileCache();
        CacheType cachesCleared = _nodeStorageCache.ClearCaches() ? CacheType.Rlp : CacheType.None;
        if (_logger.IsDebug) _logger.Debug($"Cleared caches: {cachesCleared}");
        return cachesCleared;
    }

    public void Dispose()
    {
        if (_preBlockCaches is not null) _preBlockCaches.ConsumerScopeOpened -= CancelAndJoinSpeculative;
        CancelAndJoinSpeculative();
        _warmedTxHashes.Dispose();
        (_envPool as IDisposable)?.Dispose();
    }

    private void PreWarmCachesParallel(BlockState blockState, Block suggestedBlock, BlockHeader parent, IReleaseSpec spec, ParallelOptions parallelOptions, AddressWarmer addressWarmer, CancellationToken cancellationToken)
    {
        try
        {
            if (cancellationToken.IsCancellationRequested) return;

            if (_logger.IsDebug) _logger.Debug($"Started pre-warming caches for block {suggestedBlock.Number}.");

            if (!addressWarmer.HasBal)
            {
                WarmupTransactions(blockState, parallelOptions);
                WarmupWithdrawals(parallelOptions, spec, suggestedBlock, parent);
            }

            if (_logger.IsDebug) _logger.Debug($"Finished pre-warming caches for block {suggestedBlock.Number}.");
        }
        catch (Exception ex)
        {
            _logger.DebugWarn($"Error pre-warming {suggestedBlock.Number}. {ex}");
        }
        finally
        {
            // Don't complete the task until address warmer is also done.
            addressWarmer.Wait();
            addressWarmer.Dispose();
        }
    }

    private void WarmupWithdrawals(ParallelOptions parallelOptions, IReleaseSpec spec, Block block, BlockHeader? parent)
    {
        if (parallelOptions.CancellationToken.IsCancellationRequested) return;

        try
        {
            if (spec.WithdrawalsEnabled && block.Withdrawals is not null)
            {
                ParallelUnbalancedWork.For(0, block.Withdrawals.Length, parallelOptions, (EnvPool: _envPool, Block: block, Parent: parent),
                    static (i, state) =>
                    {
                        IReadOnlyTxProcessorSource env = state.EnvPool.Get();
                        try
                        {
                            using IReadOnlyTxProcessingScope scope = env.Build(state.Parent);
                            scope.WorldState.WarmUp(state.Block.Withdrawals![i].Address);
                        }
                        catch (MissingTrieNodeException)
                        {
                        }
                        finally
                        {
                            state.EnvPool.Return(env);
                        }

                        return state;
                    });
            }
        }
        catch (OperationCanceledException)
        {
            // Ignore, block completed cancel
        }
        catch (Exception ex)
        {
            _logger.DebugError("Error pre-warming withdrawal", ex);
        }
    }

    private void WarmupTransactions(BlockState blockState, ParallelOptions parallelOptions)
    {
        if (parallelOptions.CancellationToken.IsCancellationRequested) return;

        try
        {
            Block block = blockState.Block;
            if (block.Transactions.Length == 0) return;

            // Group transactions by sender: an unsplit group warms sequentially so state changes
            // (balance, storage) from tx[N] are visible to tx[N+1]; exceptionally heavy chains are
            // split into independent parent-state jobs. Warming is speculative — canonical
            // execution never consumes its writes — so the split only trades warm relevance for
            // parallelism, never correctness.
            using ArrayPoolList<WarmupJob> senderGroups =
                GroupTransactionsBySender(block, parallelOptions.MaxDegreeOfParallelism, blockState.SpeculativelyWarmed);

            try
            {
                // Parallel across jobs; sequential within an unsplit sender group. Each worker
                // rents one env for its lifetime (the helper runs init only after a worker claims
                // a job, and the finalizer on success, cancellation, and captured exceptions);
                // each job still builds and disposes its own scope, so no speculative state
                // crosses jobs.
                ParallelUnbalancedWork.For(
                    0,
                    senderGroups.Count,
                    parallelOptions,
                    () => new TxWarmupWorker(blockState, senderGroups, parallelOptions.CancellationToken),
                    static (groupIndex, worker) =>
                    {
                        BlockState blockState = worker.BlockState;
                        WarmupJob job = worker.Jobs[groupIndex];

                        // Indices are ascending, so if the main thread has started the job's last tx
                        // it has started them all; the per-tx guard would discard each one, so skip
                        // before building a scope.
                        if (blockState.PreWarmer.MainThreadTxIndex >= job.LastIndex) return worker;

                        using IReadOnlyTxProcessingScope scope = worker.Env.Build(blockState.Parent);
                        BlockExecutionContext context = new(blockState.Block.Header, blockState.Spec);
                        scope.TransactionProcessor.SetBlockExecutionContext(context);

                        foreach ((int txIndex, Transaction? tx) in job.Transactions.AsSpan())
                        {
                            if (worker.Token.IsCancellationRequested) return worker;
                            WarmupSingleTransaction(scope, tx, txIndex, blockState, worker.Token);
                        }

                        return worker;
                    },
                    static worker => worker.ReturnEnv());
            }
            finally
            {
                foreach (WarmupJob job in senderGroups.AsSpan())
                    job.Transactions.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            // Ignore, block completed cancel
        }
        catch (Exception ex)
        {
            _logger.DebugError("Error pre-warming transactions", ex);
        }
    }

    internal static ArrayPoolList<WarmupJob> GroupTransactionsBySender(Block block, int maxWorkers, ISet<Hash256>? speculativelyWarmed = null)
    {
        Dictionary<AddressAsKey, ArrayPoolList<(int, Transaction)>> groups = [];

        for (int i = 0; i < block.Transactions.Length; i++)
        {
            Transaction tx = block.Transactions[i];
            if (tx.SenderAddress is not Address sender)
            {
                // Invalid signature leaves the sender null; the block will be rejected — nothing to warm.
                continue;
            }

            ref ArrayPoolList<(int, Transaction)>? list = ref CollectionsMarshal.GetValueRefOrAddDefault(groups, sender, out _);
            (list ??= new(4)).Add((i, tx));
        }

        ArrayPoolList<WarmupJob> result = new(groups.Count);
        foreach (ArrayPoolList<(int Index, Transaction Tx)> group in groups.Values)
        {
            // The sender counters stay per original sender group; splitting below must not inflate them.
            if (speculativelyWarmed is not null)
            {
                // Whole group already warmed speculatively — emit no jobs; leave the rest to the reactive pass.
                if (AllSpeculativelyWarmed(group, speculativelyWarmed))
                {
                    group.Dispose();
                    continue;
                }
            }

            ulong groupGas = TotalGasLimit(group);

            // Splitting pays only when idle workers exist to absorb the singleton jobs; with one
            // worker it just discards same-sender state propagation for nothing. Negative follows
            // ParallelOptions.MaxDegreeOfParallelism semantics: unlimited.
            if (maxWorkers is < 0 or >= 2 && group.Count >= 2 && groupGas > SplitSenderGroupGasThreshold)
            {
                // A heavy chain warms slower than the main loop executes it; warm each tx in parallel from parent state instead.
                foreach ((int Index, Transaction Tx) item in group.AsSpan())
                {
                    if (item.Tx.Hash is Hash256 hash && speculativelyWarmed?.Contains(hash) == true)
                    {
                        // Already warmed speculatively — a singleton job for it would do no work.
                        continue;
                    }
                    result.Add(new WarmupJob(new ArrayPoolList<(int, Transaction)>(1) { item }, item.Tx.GasLimit));
                }
                group.Dispose();
            }
            else
            {
                result.Add(new WarmupJob(group, groupGas));
            }
        }

        // Hoist heavy jobs to the front (heaviest first): they take the longest to warm and gain
        // the most from lead time. The rest keep block order, which streams just ahead of the main
        // thread on transaction-dense blocks. First-index tie-breaks keep equal-estimate ordering
        // deterministic under the unstable span sort.
        result.AsSpan().Sort(static (a, b) =>
        {
            if (a.IsHoisted != b.IsHoisted) return a.IsHoisted ? -1 : 1;
            if (a.IsHoisted)
            {
                int byGas = b.GasEstimate.CompareTo(a.GasEstimate);
                if (byGas != 0) return byGas;
            }
            return a.FirstIndex.CompareTo(b.FirstIndex);
        });

        return result;
    }

    /// <summary>Total gas limit above which a multi-tx sender group is warmed per-tx in parallel instead of sequentially.</summary>
    private const ulong SplitSenderGroupGasThreshold = 4_000_000;

    private static ulong TotalGasLimit(ArrayPoolList<(int Index, Transaction Tx)> group)
    {
        ulong totalGasLimit = 0;
        foreach ((int _, Transaction tx) in group.AsSpan())
        {
            // Declared gas limits are unvalidated at prewarm time; saturate so an extreme
            // payload cannot wrap the estimate and invert the split/hoist decision.
            totalGasLimit = totalGasLimit.SaturatingAdd(tx.GasLimit);
        }

        return totalGasLimit;
    }

    /// <summary>
    /// One transaction-warming job: an unsplit sender group, or a singleton child of a split
    /// exceptional group. Scheduling values are cached at formation so sorting never rescans
    /// the transaction list. The job owns <see cref="Transactions"/>; the warm loop's outer
    /// finally disposes it.
    /// </summary>
    /// <remarks>Deliberately a plain struct, not a record struct: generated equality and
    /// with-copying would obscure ownership of the pooled transaction list.</remarks>
    internal readonly struct WarmupJob(
        ArrayPoolList<(int Index, Transaction Tx)> transactions,
        ulong gasEstimate)
    {
        public readonly ArrayPoolList<(int Index, Transaction Tx)> Transactions = transactions;
        /// <summary>Saturating aggregate of the declared gas limits (see <see cref="TotalGasLimit"/>).</summary>
        public readonly ulong GasEstimate = gasEstimate;
        /// <summary>Block position of the job's first transaction; also the hoist sort's tie-break.</summary>
        public readonly int FirstIndex = transactions[0].Index;

        public int LastIndex => Transactions[^1].Index;
        public bool IsHoisted => GasEstimate > SplitSenderGroupGasThreshold;
    }

    private static bool AllSpeculativelyWarmed(ArrayPoolList<(int Index, Transaction Tx)> group, ISet<Hash256> warmed)
    {
        foreach ((int _, Transaction tx) in group.AsSpan())
        {
            if (tx.Hash is not Hash256 hash || !warmed.Contains(hash)) return false;
        }

        return true;
    }

    private static void WarmupSingleTransaction(
        IReadOnlyTxProcessingScope scope,
        Transaction tx,
        int txIndex,
        BlockState blockState,
        CancellationToken cancellationToken)
    {
        try
        {
            // Already started by the main thread — warming it now is redundant and contends; skip.
            if (blockState.PreWarmer.MainThreadTxIndex >= txIndex) return;

            // Non-null guaranteed: GroupTransactionsBySender filters null-sender txs
            Address senderAddress = tx.SenderAddress!;
            IWorldState worldState = scope.WorldState;

            if (!worldState.AccountExists(senderAddress))
            {
                worldState.CreateAccountIfNotExists(senderAddress, UInt256.Zero);
            }

            // eip-2930; cancellation-responsive so an over-declared access list can't stall the end-of-block join.
            if (blockState.Spec.UseTxAccessLists)
            {
                worldState.WarmUp(tx.AccessList, cancellationToken);
            }

            TransactionResult result = scope.TransactionProcessor.Warmup(tx, NullTxTracer.Instance);

            if (blockState.PreWarmer._logger.IsTrace) blockState.PreWarmer._logger.Trace($"Finished pre-warming cache for tx[{txIndex}] {tx.Hash} with {result}");
        }
        catch (Exception ex) when (ex is EvmException or OverflowException)
        {
            // Ignore, regular tx processing exceptions
        }
        catch (Exception ex)
        {
            blockState.PreWarmer._logger.DebugError($"Error pre-warming cache {tx.Hash}", ex);
        }
    }

    private class AddressWarmer(ParallelOptions parallelOptions, Block block, BlockHeader parent, IReleaseSpec spec, ReadOnlySpan<IHasAccessList> systemAccessLists, BlockCachePreWarmer preWarmer, ReadOnlyBlockAccessList? bal = null)
        : IThreadPoolWorkItem, IDisposable
    {
        private readonly Block Block = block;
        private readonly BlockCachePreWarmer PreWarmer = preWarmer;
        private readonly ReadOnlyBlockAccessList? Bal = bal;
        private readonly ArrayPoolList<AccessList>? SystemTxAccessLists = GetAccessLists(block, spec, systemAccessLists);
        private readonly ManualResetEventSlim _doneEvent = new(initialState: false);

        public bool HasBal => Bal is not null;

        public void Wait() => _doneEvent.Wait();

        public void Dispose() => _doneEvent.Dispose();

        private static ArrayPoolList<AccessList>? GetAccessLists(Block block, IReleaseSpec spec, ReadOnlySpan<IHasAccessList> systemAccessLists)
        {
            if (systemAccessLists.Length == 0) return null;

            ArrayPoolList<AccessList> list = new(systemAccessLists.Length);

            foreach (IHasAccessList systemAccessList in systemAccessLists)
            {
                list.Add(systemAccessList.GetAccessList(block, spec));
            }

            return list;
        }

        void IThreadPoolWorkItem.Execute()
        {
            try
            {
                if (parallelOptions.CancellationToken.IsCancellationRequested) return;
                WarmupAddresses(parallelOptions, Block);
            }
            catch (Exception ex)
            {
                PreWarmer._logger.DebugError("Error pre-warming addresses", ex);
            }
            finally
            {
                _doneEvent.Set();
            }
        }

        private void WarmupAddresses(ParallelOptions parallelOptions, Block block)
        {
            if (parallelOptions.CancellationToken.IsCancellationRequested)
            {
                SystemTxAccessLists?.Dispose();
                return;
            }

            ObjectPool<IReadOnlyTxProcessorSource> envPool = PreWarmer._envPool;
            try
            {
                Address? beneficiary = block.Header.GasBeneficiary;
                if (SystemTxAccessLists is not null || beneficiary is not null)
                {
                    IReadOnlyTxProcessorSource env = envPool.Get();
                    try
                    {
                        using IReadOnlyTxProcessingScope scope = env.Build(parent);

                        WarmupSender(beneficiary, null, scope.WorldState);

                        if (SystemTxAccessLists is not null)
                        {
                            foreach (AccessList list in SystemTxAccessLists.AsSpan())
                            {
                                scope.WorldState.WarmUp(list);
                            }
                        }
                    }
                    finally
                    {
                        envPool.Return(env);
                        SystemTxAccessLists?.Dispose();
                    }
                }

                // BAL warmup is driven from BlockProcessor.HintBal; skip speculative warming here.
                if (Bal is null)
                {
                    WarmingState<Block> baseState = new(envPool, block, parent);
                    int txCount = block.Transactions.Length;
                    int ilCount = block.InclusionListTransactions?.Length ?? 0;

                    ParallelUnbalancedWork.For(
                        0,
                        txCount + ilCount,
                        parallelOptions,
                        baseState.InitThreadState,
                    static (i, state) =>
                    {
                        Transaction[] txs = state.Payload.Transactions;
                        // Indexes past the block txs warm inclusion-list txs — they may be promoted into the block.
                        Transaction tx = i < txs.Length ? txs[i] : state.Payload.InclusionListTransactions![i - txs.Length];
                        WarmupSender(tx.SenderAddress, tx.To, state.Scope!.WorldState);

                        return state;
                    },
                    WarmingState<Block>.FinallyAction);
                }
            }
            catch (OperationCanceledException)
            {
                // Ignore, block completed cancel
            }
        }

        private static void WarmupSender(Address? sender, Address? to, IWorldState worldState)
        {
            try
            {
                if (sender is not null)
                {
                    worldState.WarmUp(sender);
                }

                if (to is not null)
                {
                    worldState.WarmUp(to);
                }
            }
            catch (MissingTrieNodeException)
            {
            }
        }
    }

    private readonly struct WarmingState<TPayload>(ObjectPool<IReadOnlyTxProcessorSource> envPool, TPayload payload, BlockHeader parent) : IDisposable
    {
        public static Action<WarmingState<TPayload>> FinallyAction { get; } = DisposeThreadState;

        private readonly ObjectPool<IReadOnlyTxProcessorSource> EnvPool = envPool;
        private readonly IReadOnlyTxProcessorSource? Env;
        public readonly TPayload Payload = payload;
        public readonly IReadOnlyTxProcessingScope? Scope;

        private WarmingState(ObjectPool<IReadOnlyTxProcessorSource> envPool, TPayload payload, BlockHeader parent, IReadOnlyTxProcessorSource env, IReadOnlyTxProcessingScope scope) : this(envPool, payload, parent)
        {
            Env = env;
            Scope = scope;
        }

        public WarmingState<TPayload> InitThreadState()
        {
            IReadOnlyTxProcessorSource env = EnvPool.Get();
            try
            {
                return new(EnvPool, Payload, parent, env, scope: env.Build(parent));
            }
            catch
            {
                EnvPool.Return(env);
                throw;
            }
        }

        public void Dispose()
        {
            Scope?.Dispose();
            if (Env is not null)
            {
                EnvPool.Return(Env);
            }
        }

        private static void DisposeThreadState(WarmingState<TPayload> state) => state.Dispose();
    }

    /// <summary>
    /// Pool policy for <see cref="IReadOnlyTxProcessorSource"/> envs used by the prewarmer.
    /// </summary>
    internal class ReadOnlyTxProcessingEnvPooledObjectPolicy(PrewarmerEnvFactory envFactory, PreBlockCaches _preBlockCaches) : IPooledObjectPolicy<IReadOnlyTxProcessorSource>
    {
        public IReadOnlyTxProcessorSource Create() => envFactory.Create(_preBlockCaches);

        /// <remarks>
        /// Always returns true — the env is valid for reuse. The pool that owns this policy
        /// must call <see cref="IDisposable.Dispose"/> on any item it cannot retain; failing
        /// to do so leaks resources held by the env for the lifetime of the process.
        /// </remarks>
        public bool Return(IReadOnlyTxProcessorSource obj) => true;
    }

    private record BlockState(BlockCachePreWarmer PreWarmer, Block Block, BlockHeader Parent, IReleaseSpec Spec, ISet<Hash256>? SpeculativelyWarmed = null);

    /// <summary>
    /// Per-worker state for the transaction-warming loop: one env rented for the worker's
    /// lifetime and returned exactly once by the loop finalizer.
    /// </summary>
    private sealed class TxWarmupWorker(
        BlockState blockState,
        ArrayPoolList<WarmupJob> jobs,
        CancellationToken token)
    {
        public readonly BlockState BlockState = blockState;
        public readonly ArrayPoolList<WarmupJob> Jobs = jobs;
        public readonly CancellationToken Token = token;
        public readonly IReadOnlyTxProcessorSource Env = blockState.PreWarmer._envPool.Get();

        public void ReturnEnv() => BlockState.PreWarmer._envPool.Return(Env);
    }

    private sealed record WarmMarker(Hash256 ParentHash, IReleaseSpec Spec, ISet<Hash256> WarmedTxHashes);
}
