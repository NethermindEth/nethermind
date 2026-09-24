// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Collections.Pooled;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Producers;
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

    /// <summary>How long a warmup pass spins for the next sender before falling back to sleeping.</summary>
    private static readonly TimeSpan SenderArrivalWindow = TimeSpan.FromMilliseconds(1);

    private readonly int _concurrencyLevel;
    // On a CPU with performance and efficiency cores, which workers run where; null elsewhere.
    private readonly PerformanceCores.PrewarmSplit? _coreSplit;
    // Speculative warming runs in the idle gap alongside RPC, so it is capped below the reactive level to leave cores free.
    private readonly int _speculativeConcurrencyLevel;
    private readonly bool _parallelExecutionBatchRead;
    private readonly ObjectPool<IPrewarmerEnv> _envPool;
    private readonly WarmupQueue _warmupQueue;
    private readonly ISenderRecoveryTracker? _senderRecovery;
    private readonly ILogger _logger;
    private readonly PreBlockCaches _preBlockCaches;
    private readonly bool _parallelExecutionEnabled;

    private const int MaxDiscoveryCandidates = 16;
    private const int MaxDiscoveryRounds = 6;
    /// <summary>
    /// Rounds that ran ready candidates, found nothing new and were not charged because a sender was still pending.
    /// Each one drops the candidates that ran, so it shrinks the set, but it also re-executes them: this keeps the
    /// speculative work bounded at <see cref="MaxDiscoveryRounds"/> plus this many block re-executions. Senders land
    /// within a round or two, so the cap is rarely reached.
    /// </summary>
    private const int MaxUnchargedRounds = 2;
    internal const int MaxDiscoveredCells = 8192;
    // Iterative discovery is substantially costlier than ordinary warmup; reserve it for exceptional transactions.
    private const ulong StorageDiscoveryGasThreshold = 10_000_000;
    // How many idle passes may try to warm one predicted slot's system hints before the slot is left cold.
    internal const int MaxSystemWarmAttempts = 3;

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

    public BlockCachePreWarmer(
        PrewarmerEnvFactory envFactory,
        IBlocksConfig blocksConfig,
        PreBlockCaches preBlockCaches,
        ILogManager logManager,
        ISenderRecoveryTracker? senderRecovery = null
    ) : this(
        new ReadOnlyTxProcessingEnvPooledObjectPolicy(envFactory, preBlockCaches),
        Environment.ProcessorCount * 2,
        blocksConfig.PreWarmStateConcurrency,
        blocksConfig.ParallelExecutionBatchRead,
        preBlockCaches,
        logManager,
        blocksConfig.MempoolPreWarmConcurrency,
        senderRecovery)
    {
        _parallelExecutionEnabled = blocksConfig.ParallelExecution;
        // Under All nothing is pinned, and the near workers are sized around where the processing thread is pinned.
        _coreSplit = blocksConfig.PreWarmCoreSplit ? PerformanceCores.PrewarmFor(blocksConfig.ProcessingCores) : null;
    }

    internal BlockCachePreWarmer(
        IPooledObjectPolicy<IPrewarmerEnv> poolPolicy,
        int minPoolSize,
        int concurrency,
        bool parallelExecutionBatchRead,
        PreBlockCaches preBlockCaches,
        ILogManager logManager,
        int speculativeConcurrency = 0,
        ISenderRecoveryTracker? senderRecovery = null)
    {
        _senderRecovery = senderRecovery;
        _concurrencyLevel = concurrency == 0 ? Environment.ProcessorCount - 1 : concurrency;
        _speculativeConcurrencyLevel = speculativeConcurrency == 0 ? Math.Max(1, _concurrencyLevel / 2) : speculativeConcurrency;
        _parallelExecutionBatchRead = parallelExecutionBatchRead;
        // minPoolSize is a floor: the address warmer, transaction warmup, and storage discovery rent
        // concurrently, each up to _concurrencyLevel, so retention is sized for all three renters.
        _envPool = new DefaultObjectPoolProvider { MaximumRetained = Math.Max(minPoolSize, _concurrencyLevel * 3 + 1) }.Create(poolPolicy);
        _logger = logManager.GetClassLogger<BlockCachePreWarmer>();
        _preBlockCaches = preBlockCaches;
        _warmupQueue = new WarmupQueue(this);
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
            carried = _preBlockCaches.PrepareFor(parent?.StateRoot, _logger);
        }

        bool skipReactiveWarming = !ShouldPreWarm(spec) || ShouldSkipReactiveWarming(suggestedBlock, spec);
        // The marker's tx set only means anything while the entries it describes are still in the caches.
        ISet<Hash256>? speculativelyWarmed =
            TryConsumeWarmMarker(suggestedBlock.ParentHash, spec, out ISet<Hash256>? warmed) && carried ? warmed : null;
        if (skipReactiveWarming) return Task.CompletedTask;
        return WarmCaches(suggestedBlock, parent, spec, speculativelyWarmed, cancellationToken);
    }

    private Task WarmCaches(Block suggestedBlock, BlockHeader? parent, IReleaseSpec spec, ISet<Hash256>? speculativelyWarmed, CancellationToken cancellationToken)
    {
        if (parent is null || _concurrencyLevel <= 1 || cancellationToken.IsCancellationRequested) return Task.CompletedTask;

        // Asked once, up front: a recovery that ends before the block is done is still the one every wait below rests on.
        ISenderRecoveryProgress? recovery = _senderRecovery?.GetInFlight(suggestedBlock.Transactions);
        (BlockState blockState, ParallelOptions parallelOptions, AddressWarmer addressWarmer) = PrepareWarm(suggestedBlock, spec, speculativelyWarmed, recovery, _concurrencyLevel, cancellationToken, warmSystemAccessLists: true);
        // A block access list already enumerates the block's reads; discovery adds nothing.
        List<(int Index, Transaction Tx)>? discoveryCandidates = addressWarmer.HasBal
            ? null
            : SelectDiscoveryCandidates(suggestedBlock, speculativelyWarmed);
        // Run address warmer ahead of transactions warmer, but queue to ThreadPool so it doesn't block the txs
        ThreadPool.UnsafeQueueUserWorkItem(addressWarmer, preferLocal: false);
        // Do not pass the cancellation token to the task, we don't want exceptions to be thrown in the main processing thread
        bool isPreparation = suggestedBlock is BlockToProduce;
        int transactionCount = suggestedBlock.Transactions.Length;
        Task normalWarmTask = Task.Run(() => PreWarmCachesParallel(
            blockState,
            suggestedBlock,
            parallelOptions,
            addressWarmer,
            isPreparation,
            transactionCount,
            cancellationToken));

        if (discoveryCandidates is null) return normalWarmTask;

        Task discoveryTask = Task.Run(() => DiscoverAndWarmStorageSafely(discoveryCandidates, suggestedBlock, spec, recovery, cancellationToken));
        return Task.WhenAll(normalWarmTask, discoveryTask);
    }

    private void DiscoverAndWarmStorageSafely(List<(int Index, Transaction Tx)> candidates, Block block, IReleaseSpec spec, ISenderRecoveryProgress? recovery, CancellationToken cancellationToken)
    {
        try
        {
            DiscoverAndWarmStorage(candidates, block, spec, recovery, cancellationToken);
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
            // Deliberately not filtered on the sender: selection runs while recovery is still in flight, and
            // dropping a heavy transaction here would switch discovery off for it for the whole block.
            if (tx.GasLimit <= StorageDiscoveryGasThreshold || tx.To is null) continue;
            if (speculativelyWarmed is not null && tx.Hash is Hash256 hash && speculativelyWarmed.Contains(hash)) continue;

            (candidates ??= new(MaxDiscoveryCandidates)).Add((i, tx));
            if (candidates.Count == MaxDiscoveryCandidates) break;
        }

        return candidates;
    }

    internal void DiscoverAndWarmStorage(List<(int Index, Transaction Tx)> candidates, Block block, IReleaseSpec spec, ISenderRecoveryProgress? recovery, CancellationToken cancellationToken)
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
        int uncharged = 0;

        for (int round = 0; round < MaxDiscoveryRounds && currentCandidates.Count > 0; round++)
        {
            roundCells.Clear();
            nextRoundCandidates.Clear();
            admitted.Clear();
            deferred.Clear();
            SplitByRoundGasBudget(currentCandidates, block.GasLimit, admitted, deferred);
            if (admitted.Count == 0) return;

            // A candidate still without a sender does not join the round: waiting for it inside the fan-out would
            // hold every cell the others found back from warming until it landed. It waits the round out as a
            // survivor, and only when no candidate is ready at all does the round wait for the first sender, on
            // this thread and off the depth budget.
            int awaiting = DeferUnrecovered(admitted, nextRoundCandidates);
            if (admitted.Count == 0)
            {
                (int Index, Transaction Tx) first = nextRoundCandidates[0];
                if (!WaitForSender(first, recovery, cancellationToken))
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    // Reached by the main thread, or left without a sender for good: out, with everything else the
                    // main thread has reached, so the wait is never repeated for the same candidate.
                    DropUnwarmable(currentCandidates, first.Index);
                }

                round--;
                continue;
            }

            int cellBudget = MaxDiscoveredCells - allDiscoveredCells.Count;
            DiscoveryRound roundState = new(block, spec, cellBudget, new StrongBox<int>(cellBudget), roundCells, roundCellsLock, nextRoundCandidates, cancellationToken);
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

            int productive = nextRoundCandidates.Count - awaiting;
            if (roundCells.Count > 0)
            {
                allDiscoveredCells.UnionWith(roundCells);
                if (!WarmDiscoveredStorage(block.Header, roundCells, cancellationToken)) return;
                if (allDiscoveredCells.Count >= MaxDiscoveredCells) return;
            }
            else if (awaiting == 0 && (deferred.Count == 0 || productive == admitted.Count))
            {
                // No progress and no budget freed for the deferred — the next round would repeat this one.
                return;
            }
            else if (awaiting > 0)
            {
                // The candidates that ran would only repeat themselves; the ones still waiting for a sender go on,
                // and the round is not charged to the depth budget they have not used yet - up to MaxUnchargedRounds
                // times, since unlike the wait above this round did execute.
                nextRoundCandidates.RemoveRange(awaiting, productive);
                if (uncharged++ < MaxUnchargedRounds) round--;
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

    /// <summary>
    /// Moves the candidates still without a sender from <paramref name="admitted"/> to <paramref name="nextRound"/>,
    /// both kept in block order; returns how many moved.
    /// </summary>
    private static int DeferUnrecovered(List<(int Index, Transaction Tx)> admitted, List<(int Index, Transaction Tx)> nextRound)
    {
        int kept = 0;
        for (int i = 0; i < admitted.Count; i++)
        {
            (int Index, Transaction Tx) candidate = admitted[i];
            if (candidate.Tx.SenderAddress is null) nextRound.Add(candidate);
            else admitted[kept++] = candidate;
        }

        int moved = admitted.Count - kept;
        admitted.RemoveRange(kept, moved);
        return moved;
    }

    /// <summary>
    /// Removes the candidate at <paramref name="index"/> and every candidate the main thread has already reached;
    /// discovering their reads would only contend with it.
    /// </summary>
    private void DropUnwarmable(List<(int Index, Transaction Tx)> candidates, int index)
    {
        int mainThreadTxIndex = MainThreadTxIndex;
        int kept = 0;
        for (int i = 0; i < candidates.Count; i++)
        {
            (int Index, Transaction Tx) candidate = candidates[i];
            if (candidate.Index > mainThreadTxIndex && candidate.Index != index) candidates[kept++] = candidate;
        }

        candidates.RemoveRange(kept, candidates.Count - kept);
    }

    /// <summary>Shared state of one discovery round.</summary>
    private sealed class DiscoveryRound(
        Block block,
        IReleaseSpec spec,
        int cellBudget,
        StrongBox<int> remainingCaptureCells,
        PooledSet<StorageCell> cells,
        Lock cellsLock,
        List<(int Index, Transaction Tx)> nextRoundCandidates,
        CancellationToken cancellationToken)
    {
        public readonly Block Block = block;
        public readonly IReleaseSpec Spec = spec;
        public readonly int CellBudget = cellBudget;
        public readonly StrongBox<int> RemainingCaptureCells = remainingCaptureCells;
        public readonly PooledSet<StorageCell> Cells = cells;
        public readonly Lock CellsLock = cellsLock;
        public readonly List<(int Index, Transaction Tx)> NextRoundCandidates = nextRoundCandidates;
        public readonly CancellationToken CancellationToken = cancellationToken;
    }

    /// <summary>
    /// Waits for a candidate's sender; <c>false</c> once it will not land in time: the main thread has reached the
    /// candidate, the recovery finished without it, or the block is done.
    /// </summary>
    /// <remarks>
    /// Inside the spin window the sender is re-read only when the recovery's count has moved; past it the wait is on
    /// the recovery's completion, which pulses it awake, and every wake re-reads. Only the discovery thread waits
    /// here, between rounds, so the spin window costs one core for a millisecond at most.
    /// </remarks>
    private bool WaitForSender((int Index, Transaction Tx) candidate, ISenderRecoveryProgress? recovery, CancellationToken cancellationToken)
    {
        Transaction tx = candidate.Tx;
        // Nothing in flight: the sender is final.
        if (recovery is null) return tx.SenderAddress is not null;

        int seen = -1;
        long start = Stopwatch.GetTimestamp();
        SpinWait spinner = default;
        while (!cancellationToken.IsCancellationRequested && MainThreadTxIndex < candidate.Index)
        {
            bool completed = recovery.IsCompleted;
            int recovered = recovery.Recovered;
            if (recovered != seen || completed)
            {
                seen = recovered;
                if (tx.SenderAddress is not null) return true;
                if (completed) return false;
            }

            if (Stopwatch.GetElapsedTime(start) < SenderArrivalWindow)
            {
                spinner.SpinOnce(sleep1Threshold: -1);
            }
            else
            {
                // Past the window every wake re-reads the sender: it may have been filled in by a path the count
                // never sees, such as the transaction processor recovering it inline.
                recovery.WaitForCompletion(1);
                seen = -1;
            }
        }

        return false;
    }

    private void DiscoverTransactionStorageReads((int Index, Transaction Tx) candidate, DiscoveryRound round)
    {
        // Already started by the main thread — warming it now is redundant and contends; skip.
        if (MainThreadTxIndex >= candidate.Index) return;

        // The round admits only candidates whose sender has landed; see DeferUnrecovered.
        Transaction tx = candidate.Tx;
        IPrewarmerEnv env = _envPool.Get();
        try
        {
            using PreBlockCaches.StorageReadCapture capture = _preBlockCaches.BeginStorageReadCapture(round.RemainingCaptureCells);
            using IReadOnlyTxProcessingScope scope = env.BuildAtTarget(round.Block.Header);
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

    private bool WarmDiscoveredStorage(BlockHeader target, PooledSet<StorageCell> discoveredCells, CancellationToken cancellationToken)
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
                IPrewarmerEnv env = _envPool.Get();
                try
                {
                    using IReadOnlyTxProcessingScope scope = env.BuildAtTarget(target);
                    IWorldState worldState = scope.WorldState;
                    int unreadable = 0;
                    for (int i = range.Item1; i < range.Item2; i++)
                    {
                        if (((i - range.Item1) & 0x3F) == 0 && cancellationToken.IsCancellationRequested) return;
                        try
                        {
                            worldState.Get(in cells[i], out _);
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

    /// <returns>Whether the system-contract hints were warmed; false when they were requested but the pass did not reach them.</returns>
    private bool WarmDeltaSync(Block delta, IReleaseSpec spec, bool warmSystemAccessLists, CancellationToken token)
    {
        // The delta comes from the txpool, where every sender is already recovered, so there is no recovery to wait on.
        (BlockState blockState, ParallelOptions parallelOptions, AddressWarmer addressWarmer) = PrepareWarm(delta, spec, speculativelyWarmed: null, recovery: null, _speculativeConcurrencyLevel, token, warmSystemAccessLists);
        // Run inline rather than through the pool: this pass is going to block on the warmer anyway, and the block
        // that ends the gap joins this thread, so a queued item would put thread-pool dispatch latency on its path.
        ((IThreadPoolWorkItem)addressWarmer).Execute();
        PreWarmCachesParallel(
            blockState,
            delta,
            parallelOptions,
            addressWarmer,
            isPreparation: true,
            transactionCount: delta.Transactions.Length,
            cancellationToken: token);

        // PreWarmCachesParallel joins the address warmer before returning, so the flag is settled here.
        return addressWarmer.SystemAccessListsWarmed;
    }

    private (BlockState BlockState, ParallelOptions ParallelOptions, AddressWarmer AddressWarmer) PrepareWarm(Block block, IReleaseSpec spec, ISet<Hash256>? speculativelyWarmed, ISenderRecoveryProgress? recovery, int maxDegreeOfParallelism, CancellationToken token, bool warmSystemAccessLists)
    {
        BlockState blockState = new(this, block, spec, speculativelyWarmed, recovery);
        // Safe for the speculative caller: it never overlaps main execution (joined before ProcessOne).
        Volatile.Write(ref _mainThreadTxIndex, -1);
        ParallelOptions parallelOptions = new() { MaxDegreeOfParallelism = maxDegreeOfParallelism, CancellationToken = token };
        // BAL makes speculative tx execution redundant — when BAL-based read warming is in use, drive warmup
        // directly off the block's access list.
        ReadOnlyBlockAccessList? bal = IsBalReadWarmingEnabled(spec) ? block.BlockAccessList : null;
        AddressWarmer addressWarmer = new(parallelOptions, block, spec, warmSystemAccessLists, this, bal);
        return (blockState, parallelOptions, addressWarmer);
    }

    public Task StartSpeculativePreWarm(BlockHeader head, IReleaseSpec spec, long generation, Func<CancellationToken, (Block Block, IReleaseSpec Spec)?> nextDelta, int idlePassDelayMs, CancellationToken cancellationToken)
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
            _preBlockCaches.PrepareFor(head.StateRoot, _logger);
            return _speculativeTask = Task.Run(() => RunSpeculativeLoop(headHash, head, spec, nextDelta, idlePassDelayMs, token));
        }
    }

    private void RunSpeculativeLoop(Hash256 headHash, BlockHeader head, IReleaseSpec spec, Func<CancellationToken, (Block Block, IReleaseSpec Spec)?> nextDelta, int idlePassDelayMs, CancellationToken token)
    {
        // _warmedTxHashes is reused across sessions (cleared at session start); only the small marker is per-session.
        WarmMarker marker = new(headHash, spec, _warmedTxHashes);
        try
        {
            int delay = Math.Max(1, idlePassDelayMs);
            // The EIP-4788 ring-buffer cells are indexed by the predicted timestamp, so the system warm is redone only
            // when the prediction moves to another slot (a missed slot), not on every pass.
            ulong? warmedSystemTimestamp = null;
            ulong attemptedSystemTimestamp = 0;
            int systemWarmAttempts = 0;
            while (!token.IsCancellationRequested)
            {
                (Block Block, IReleaseSpec Spec)? next = nextDelta(token);
                if (token.IsCancellationRequested) break;

                if (next is (Block delta, IReleaseSpec deltaSpec))
                {
                    bool warmSystemAccessLists = warmedSystemTimestamp != delta.Timestamp;
                    // An empty delta still warms the system-contract slots and the beneficiary for the predicted block.
                    if (warmSystemAccessLists || delta.Transactions.Length > 0)
                    {
                        bool systemWarmed = WarmDeltaSync(delta, deltaSpec, warmSystemAccessLists, token);
                        // Don't record a delta cancelled mid-warm, or the reactive pass would skip a half-warmed sender.
                        if (token.IsCancellationRequested) break;
                        if (warmSystemAccessLists)
                        {
                            systemWarmAttempts = attemptedSystemTimestamp == delta.Timestamp ? systemWarmAttempts + 1 : 1;
                            attemptedSystemTimestamp = delta.Timestamp;
                            // Retire the slot only once its hints actually landed, since re-warming on the next pass is
                            // cheaper than leaving the slot cold for the rest of the gap - but give up after a few
                            // tries, so a hint that never recovers cannot charge a warm pass to every pass in the gap.
                            if (systemWarmed || systemWarmAttempts >= MaxSystemWarmAttempts) warmedSystemTimestamp = delta.Timestamp;
                        }
                        foreach (Transaction tx in delta.Transactions)
                        {
                            if (tx.Hash is Hash256 hash) _warmedTxHashes.Add(hash);
                        }
                        // A fork activating inside the gap moves the predicted spec; the marker must name the one warmed.
                        if (!ReferenceEquals(marker.Spec, deltaSpec)) marker = marker with { Spec = deltaSpec };
                        Volatile.Write(ref _warmMarker, marker);
                    }
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

    // For tests: the recovery tracker the container wired in.
    internal ISenderRecoveryTracker? SenderRecovery => _senderRecovery;

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
        // The account and storage caches carry over: the block's commit writes its final values into them, and PrepareFor
        // keeps or clears them before the next use. This continuation can overlap that write-back, so it must not touch them.
        _preBlockCaches?.ClearPrecompileCache();
        return CacheType.None;
    }

    public void Dispose()
    {
        if (_preBlockCaches is not null) _preBlockCaches.ConsumerScopeOpened -= CancelAndJoinSpeculative;
        CancelAndJoinSpeculative();
        _warmedTxHashes.Dispose();
        _warmupQueue.Dispose();
        (_envPool as IDisposable)?.Dispose();
    }

    private void PreWarmCachesParallel(
        BlockState blockState,
        Block suggestedBlock,
        ParallelOptions parallelOptions,
        AddressWarmer addressWarmer,
        bool isPreparation,
        int transactionCount,
        CancellationToken cancellationToken)
    {
        try
        {
            if (cancellationToken.IsCancellationRequested) return;

            if (_logger.IsDebug) DebugPreWarming("Started", suggestedBlock.Number, isPreparation, transactionCount);

            if (!addressWarmer.HasBal)
            {
                WarmupTransactions(blockState, parallelOptions);
            }

            if (_logger.IsDebug) DebugPreWarming("Finished", suggestedBlock.Number, isPreparation, transactionCount);
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

        [MethodImpl(MethodImplOptions.NoInlining)]
        void DebugPreWarming(string state, ulong blockNumber, bool isPreparation, int transactionCount) =>
            _logger.Debug(
                $"{state} pre-warming caches for {(isPreparation ? "preparation" : "validation")} of block {blockNumber} with {transactionCount} {(isPreparation ? "new transactions" : "transactions")}.");
    }

    private void WarmupTransactions(BlockState blockState, ParallelOptions parallelOptions)
    {
        Block block = blockState.Block;
        int txCount = block.Transactions.Length;
        if (txCount == 0 || parallelOptions.CancellationToken.IsCancellationRequested) return;

        // Senders may still be arriving while the block is already being processed (recovery runs in
        // ascending index order, see RecoverSignatures.StartRecovery). One fan-out serves the whole block:
        // workers drain the jobs grouped up front, then claim late-recovered transactions as their senders
        // land. Nothing separates the two, so a worker inside a heavy job holds nobody back, and no pass
        // regroups the block or re-rents an env for what arrived since the last one.
        int[] claimed = ArrayPool<int>.Shared.Rent(txCount);
        Array.Clear(claimed, 0, txCount);
        // The queue, its workers and the grouping scratch live with the prewarmer; a block only loads them, so past
        // the first block nothing is allocated for them. Warms of one prewarmer never overlap - the speculative
        // session is joined before a consumer opens - and if one ever did, it would get a queue of its own rather
        // than share.
        WarmupQueue queue = _warmupQueue.TryAcquire() ? _warmupQueue : new WarmupQueue(this);
        try
        {
            // Loading groups the transactions by sender: an unsplit group warms sequentially so state changes
            // (balance, storage) from tx[N] are visible to tx[N+1]; exceptionally heavy chains are split into
            // independent parent-state jobs. Warming is speculative — canonical execution never consumes its
            // writes — so the split only trades warm relevance for parallelism, never correctness. What the
            // grouping left unclaimed is still waiting for its sender; with the jobs, that is every unit of work a
            // worker could ever pick up, so no more workers than that are started: a block that groups into two
            // jobs must not queue a dozen pool items that each rent an env, find nothing and leave.
            int pending = queue.Load(blockState, claimed, txCount, parallelOptions);
            if (pending > 0)
            {
                // Each iteration is one worker that runs until the block has nothing left for it; the range only
                // bounds how many are started. A worker that runs dry is parked, and the one that stays behind
                // recruits parked workers when a wave of senders lands, so the wave is warmed with the fan-out's
                // full degree rather than one transaction at a time.
                ParallelUnbalancedWork.For(
                    0,
                    Math.Clamp(queue.Degree, 1, pending),
                    parallelOptions,
                    queue.RentWorker,
                    static (slot, worker) =>
                    {
                        worker.Drain(slot);
                        return worker;
                    },
                    static worker => worker.Park());
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
        finally
        {
            // Joins the helpers, which are pool items outside the fan-out's own join, takes the job lists back and
            // drops the block.
            queue.Unload();
            if (queue != _warmupQueue) queue.Dispose();
        }

        ArrayPool<int>.Shared.Return(claimed);
    }

    /// <summary>
    /// Claims one of <paramref name="count"/> jobs from the front or the back. <paramref name="taken"/> packs how many
    /// were taken from each end, so the two ends meet on a single exchange and never hand out the same job.
    /// </summary>
    internal static bool TryClaimJob(ref long taken, int count, bool fromBack, out int index)
    {
        while (true)
        {
            long current = Volatile.Read(ref taken);
            int front = (int)current;
            int back = (int)(current >> 32);
            // Once the jobs are gone every worker asks again on its way to the late runs; nothing changes for that.
            if (front + back >= count)
            {
                index = -1;
                return false;
            }

            long next = fromBack ? current + (1L << 32) : current + 1;
            if (Interlocked.CompareExchange(ref taken, next, current) == current)
            {
                index = fromBack ? count - 1 - back : front;
                return true;
            }
        }
    }

    private static int CountUnclaimed(ReadOnlySpan<int> claimed)
    {
        int unclaimed = 0;
        foreach (int claim in claimed)
        {
            if (claim == 0) unclaimed++;
        }

        return unclaimed;
    }

    /// <summary>For tests: the jobs, and with them their transaction lists, are the caller's to dispose; the scratch is released here.</summary>
    internal static ArrayPoolList<WarmupJob> GroupTransactionsBySender(Block block, int maxWorkers, ISet<Hash256>? speculativelyWarmed = null, int[]? claimed = null)
    {
        using GroupingScratch scratch = new();
        GroupTransactionsBySender(block, maxWorkers, speculativelyWarmed, claimed, scratch);
        ArrayPoolList<WarmupJob> jobs = new(scratch.Jobs.Count);
        jobs.AddRange(scratch.Jobs.AsSpan());
        scratch.Jobs.Clear();
        return jobs;
    }

    /// <summary>Groups into <paramref name="scratch"/>, whose lists and dictionary are reused block after block.</summary>
    private static void GroupTransactionsBySender(Block block, int maxWorkers, ISet<Hash256>? speculativelyWarmed, int[]? claimed, GroupingScratch scratch)
    {
        Dictionary<AddressAsKey, ArrayPoolList<(int Index, Transaction Tx)>> groups = scratch.Groups;
        groups.Clear();

        for (int i = 0; i < block.Transactions.Length; i++)
        {
            if (claimed is not null && claimed[i] != 0) continue;

            Transaction tx = block.Transactions[i];
            if (tx.SenderAddress is not Address sender)
            {
                // Not recovered yet (a worker claims it once its sender lands) or an invalid signature (the block will be rejected).
                continue;
            }

            if (claimed is not null) claimed[i] = 1;
            ref ArrayPoolList<(int Index, Transaction Tx)>? list = ref CollectionsMarshal.GetValueRefOrAddDefault(groups, sender, out _);
            (list ??= scratch.RentList()).Add((i, tx));
        }

        ArrayPoolList<WarmupJob> result = scratch.Jobs;
        foreach (ArrayPoolList<(int Index, Transaction Tx)> group in groups.Values)
        {
            // The sender counters stay per original sender group; splitting below must not inflate them.
            if (speculativelyWarmed is not null)
            {
                // Whole group already warmed speculatively — emit no jobs; leave the rest to the reactive pass.
                if (AllSpeculativelyWarmed(group, speculativelyWarmed))
                {
                    scratch.ReturnList(group);
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
                    ArrayPoolList<(int Index, Transaction Tx)> single = scratch.RentList();
                    single.Add(item);
                    result.Add(new WarmupJob(single, item.Tx.GasLimit));
                }
                scratch.ReturnList(group);
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
    }

    /// <summary>
    /// What grouping a block works in: the sender dictionary, the jobs, and the transaction lists behind them, all
    /// kept from block to block so grouping allocates nothing once warm.
    /// </summary>
    internal sealed class GroupingScratch : IDisposable
    {
        // Lists beyond this many are released rather than kept; a block with more senders than this is rare enough
        // that keeping their arrays rented between blocks would cost more than allocating them then.
        private const int MaxRetainedLists = 1024;

        public readonly Dictionary<AddressAsKey, ArrayPoolList<(int Index, Transaction Tx)>> Groups = [];
        public readonly ArrayPoolList<WarmupJob> Jobs = new(64);
        private readonly Stack<ArrayPoolList<(int Index, Transaction Tx)>> _lists = new();

        public ArrayPoolList<(int Index, Transaction Tx)> RentList() => _lists.TryPop(out ArrayPoolList<(int Index, Transaction Tx)>? list) ? list : new(4);

        public void ReturnList(ArrayPoolList<(int Index, Transaction Tx)> list)
        {
            if (_lists.Count >= MaxRetainedLists)
            {
                list.Dispose();
                return;
            }

            list.Clear();
            _lists.Push(list);
        }

        /// <summary>Takes every job's list back and forgets the jobs.</summary>
        public void ReturnJobs()
        {
            foreach (WarmupJob job in Jobs.AsSpan())
            {
                ReturnList(job.Transactions);
            }

            Jobs.Clear();
        }

        public void Dispose()
        {
            ReturnJobs();
            Jobs.Dispose();
            while (_lists.TryPop(out ArrayPoolList<(int Index, Transaction Tx)>? list))
            {
                list.Dispose();
            }
        }
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

            // Non-null guaranteed: GroupTransactionsBySender and WarmupQueue.TryClaimLate both skip null-sender txs
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

    private class AddressWarmer(ParallelOptions parallelOptions, Block block, IReleaseSpec spec, bool warmSystemAccessLists, BlockCachePreWarmer preWarmer, ReadOnlyBlockAccessList? bal = null)
        : IThreadPoolWorkItem, IDisposable
    {
        private readonly Block Block = block;
        private readonly IReleaseSpec Spec = spec;
        private readonly BlockCachePreWarmer PreWarmer = preWarmer;
        private readonly ReadOnlyBlockAccessList? Bal = bal;
        private readonly bool WarmWithdrawals = bal is null && spec.WithdrawalsEnabled && block.Withdrawals?.Length > 0;
        private readonly ManualResetEventSlim _doneEvent = new(initialState: false);
        private bool _systemAccessListsWarmed;

        public bool HasBal => Bal is not null;

        /// <summary>Whether the system-contract hints were evaluated and warmed; false if the pass was cancelled or faulted.</summary>
        /// <remarks>Only meaningful after <see cref="Wait"/>, which orders this read after the warming thread's write.</remarks>
        public bool SystemAccessListsWarmed => Volatile.Read(ref _systemAccessListsWarmed);

        public void Wait() => _doneEvent.Wait();

        public void Dispose() => _doneEvent.Dispose();

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
            if (parallelOptions.CancellationToken.IsCancellationRequested) return;

            ObjectPool<IPrewarmerEnv> envPool = PreWarmer._envPool;
            try
            {
                Address? beneficiary = block.Header.GasBeneficiary;
                if (warmSystemAccessLists || beneficiary is not null || WarmWithdrawals)
                {
                    IPrewarmerEnv env = envPool.Get();
                    try
                    {
                        using IReadOnlyTxProcessingScope scope = env.BuildAtTarget(block.Header);

                        WarmupSender(beneficiary, null, scope.WorldState);

                        if (WarmWithdrawals)
                        {
                            // Withdrawal recipients are applied at block end; warming them here rather than after
                            // every transaction keeps their account reads off the main thread on dense blocks.
                            // Cancellation-responsive so an oversized list can't stall the end-of-block join.
                            foreach (Withdrawal withdrawal in block.Withdrawals!)
                            {
                                if (parallelOptions.CancellationToken.IsCancellationRequested) break;
                                WarmupSender(withdrawal.Address, null, scope.WorldState);
                            }
                        }

                        if (warmSystemAccessLists)
                        {
                            // Evaluated here rather than up front: the hints read state, and the only world state with an
                            // open scope on the speculative path is this env's own.
                            if (WarmupSystemAccessLists(env.SystemAccessLists, scope.WorldState))
                            {
                                Volatile.Write(ref _systemAccessListsWarmed, true);
                            }
                        }
                    }
                    finally
                    {
                        envPool.Return(env);
                    }
                }

                // BAL warmup is driven from BlockProcessor.HintBal; skip speculative warming here.
                if (Bal is null)
                {
                    // One pass over the recipients and the senders recovered by now. A sender that lands later is not
                    // lost: the transaction fan-out claims its transaction as it arrives and reads the account when it
                    // executes it, which is the warm this pass would give it. Repeating the pass for late senders would
                    // build a scope per worker and join a fan-out per repeat to warm accounts the fan-out warms anyway.
                    // Below MinTransactionsForReactiveWarming there is no fan-out, and there a late sender costs the
                    // main thread one account read.
                    // In ranges, as WarmDiscoveredStorage does: an iteration per transaction would put one interlocked
                    // increment per transaction on the fan-out's shared index, which on a dense block is thousands of
                    // contended writes to one cache line for a few nanoseconds of work each.
                    int count = block.Transactions.Length + (block.InclusionListTransactions?.Length ?? 0);
                    int rangeSize = Math.Max(16, count / (parallelOptions.MaxDegreeOfParallelism * 4));
                    WarmingState<(Block Block, int RangeSize, int Count)> baseState = new(envPool, (block, rangeSize, count), block.Header);
                    ParallelUnbalancedWork.For(
                        0,
                        (count + rangeSize - 1) / rangeSize,
                        parallelOptions,
                        baseState.InitThreadState,
                        static (range, state) =>
                        {
                            (Block block, int rangeSize, int count) = state.Payload;
                            IWorldState worldState = state.Scope!.WorldState;
                            int end = Math.Min((range + 1) * rangeSize, count);
                            for (int i = range * rangeSize; i < end; i++)
                            {
                                Transaction tx = TransactionAt(block, i);
                                WarmupSender(tx.SenderAddress, tx.To, worldState);
                            }

                            return state;
                        },
                        WarmingState<(Block, int, int)>.FinallyAction);
                }
            }
            catch (OperationCanceledException)
            {
                // Ignore, block completed cancel
            }
        }

        /// <summary>Indexes past the block transactions address inclusion-list ones, which may be promoted into the block.</summary>
        private static Transaction TransactionAt(Block block, int i)
        {
            Transaction[] txs = block.Transactions;
            return i < txs.Length ? txs[i] : block.InclusionListTransactions![i - txs.Length];
        }

        /// <summary>Warms every system-contract hint.</summary>
        /// <returns>Whether all of them landed.</returns>
        /// <remarks>
        /// Tolerates a pruned root the same way <see cref="WarmupSender"/> does: losing a hint costs a cold read, it is
        /// not a reason to abandon the rest of the pass. The hints are plugin-supplied, so any other failure is
        /// contained here as well rather than costing the pass its transaction warming too.
        /// </remarks>
        private bool WarmupSystemAccessLists(ReadOnlySpan<IHasAccessList> systemAccessLists, IWorldState worldState)
        {
            bool warmed = true;
            CancellationToken token = parallelOptions.CancellationToken;
            foreach (IHasAccessList systemAccessList in systemAccessLists)
            {
                // The hints are plugin-supplied and read state, so bound them on the token the way the transaction
                // and withdrawal loops are: this warmer is joined on the way into block processing.
                if (token.IsCancellationRequested) return false;
                try
                {
                    if (systemAccessList.GetAccessList(Block, Spec) is AccessList list) worldState.WarmUp(list, token);
                }
                catch (MissingTrieNodeException)
                {
                    warmed = false;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    PreWarmer._logger.DebugError($"Error pre-warming the {systemAccessList.GetType().Name} access list", ex);
                    warmed = false;
                }
            }

            return warmed;
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

    private readonly struct WarmingState<TPayload>(ObjectPool<IPrewarmerEnv> envPool, TPayload payload, BlockHeader target) : IDisposable
    {
        public static Action<WarmingState<TPayload>> FinallyAction { get; } = DisposeThreadState;

        private readonly ObjectPool<IPrewarmerEnv> EnvPool = envPool;
        private readonly IPrewarmerEnv? Env;
        public readonly TPayload Payload = payload;
        public readonly IReadOnlyTxProcessingScope? Scope;

        private WarmingState(ObjectPool<IPrewarmerEnv> envPool, TPayload payload, BlockHeader target, IPrewarmerEnv env, IReadOnlyTxProcessingScope scope) : this(envPool, payload, target)
        {
            Env = env;
            Scope = scope;
        }

        public WarmingState<TPayload> InitThreadState()
        {
            IPrewarmerEnv env = EnvPool.Get();
            try
            {
                return new(EnvPool, Payload, target, env, scope: env.BuildAtTarget(target));
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
    /// Pool policy for the <see cref="IPrewarmerEnv"/> instances used by the prewarmer.
    /// </summary>
    internal class ReadOnlyTxProcessingEnvPooledObjectPolicy(PrewarmerEnvFactory envFactory, PreBlockCaches _preBlockCaches) : IPooledObjectPolicy<IPrewarmerEnv>
    {
        public IPrewarmerEnv Create() => envFactory.Create(_preBlockCaches);

        /// <remarks>
        /// Always returns true — the env is valid for reuse. The pool that owns this policy
        /// must call <see cref="IDisposable.Dispose"/> on any item it cannot retain; failing
        /// to do so leaks resources held by the env for the lifetime of the process.
        /// </remarks>
        public bool Return(IPrewarmerEnv obj) => true;
    }

    /// <param name="Recovery">The sender recovery still running for the block, or <c>null</c> when every sender is final.</param>
    private record BlockState(BlockCachePreWarmer PreWarmer, Block Block, IReleaseSpec Spec, ISet<Hash256>? SpeculativelyWarmed = null, ISenderRecoveryProgress? Recovery = null);

    /// <summary>
    /// What the transaction-warming workers share, owned by the prewarmer and loaded with one block at a time: the
    /// jobs grouped up front, taken in order; the claim table that hands each late-recovered transaction to exactly
    /// one worker; the parked workers a wave of late senders is warmed with; and the degree that bounds them all.
    /// </summary>
    private sealed class WarmupQueue : IDisposable
    {
        public readonly BlockCachePreWarmer PreWarmer;
        public readonly Func<TxWarmupWorker> RentWorker;
        private readonly GroupingScratch _scratch = new();
        private readonly Lock _parkedLock = new();
        // Monitor rather than an event: the last helper out pulses under the gate and the join checks the count
        // under it too, so there is no window for a lost wake and nothing to dispose.
        private readonly object _helpersGate = new();
        private TxWarmupWorker? _parked;
        private int _inUse;

        // The loaded block. Cleared on unload so the queue pins nothing between blocks.
        public BlockState BlockState { get; private set; } = null!;
        public CancellationToken Token { get; private set; }
        public int Degree { get; private set; }
        private Transaction[] _txs = [];
        private ISet<Hash256>? _speculativelyWarmed;
        private int[] _claimed = [];
        private int _txCount;
        // Jobs taken from the front in the low half, from the back in the high half; see TryClaimJob.
        private long _jobsTaken;
        private int _firstUnclaimed;
        private int _waiter;
        private int _active;
        private int _helpers;

        public WarmupQueue(BlockCachePreWarmer preWarmer)
        {
            PreWarmer = preWarmer;
            RentWorker = RentAttached;
        }

        /// <summary><c>true</c> when the caller now owns the queue; it is handed back by <see cref="Unload"/>.</summary>
        public bool TryAcquire() => Interlocked.CompareExchange(ref _inUse, 1, 0) == 0;

        /// <summary>Loads the block and groups its transactions; returns the jobs plus the transactions still unclaimed.</summary>
        public int Load(BlockState blockState, int[] claimed, int txCount, ParallelOptions parallelOptions)
        {
            BlockState = blockState;
            Token = parallelOptions.CancellationToken;
            int maxDegree = parallelOptions.MaxDegreeOfParallelism;
            Degree = maxDegree > 0 ? maxDegree : PreWarmer._concurrencyLevel;
            _txs = blockState.Block.Transactions;
            _speculativelyWarmed = blockState.SpeculativelyWarmed;
            _claimed = claimed;
            _txCount = txCount;
            _jobsTaken = 0;
            _firstUnclaimed = 0;
            _waiter = 0;
            _active = 0;
            _helpers = 0;
            GroupTransactionsBySender(blockState.Block, maxDegree, _speculativelyWarmed, claimed, _scratch);
            return _scratch.Jobs.Count + CountUnclaimed(claimed.AsSpan(0, txCount));
        }

        /// <summary>Joins the helpers, takes the job lists back, drops every reference to the block and hands the queue back.</summary>
        public void Unload()
        {
            WaitForHelpers();
            _scratch.ReturnJobs();
            BlockState = null!;
            Token = default;
            _txs = [];
            _speculativelyWarmed = null;
            _claimed = [];
            Volatile.Write(ref _inUse, 0);
        }

        /// <summary>
        /// Takes the next job from the front - the heavy jobs, then block order, which is what the processing thread
        /// reaches first - or, for a worker on an efficiency core, from the back, which it reaches last.
        /// </summary>
        public bool TryTakeJob(bool fromBack, out WarmupJob job)
        {
            ArrayPoolList<WarmupJob> jobs = _scratch.Jobs;
            if (TryClaimJob(ref _jobsTaken, jobs.Count, fromBack, out int index))
            {
                job = jobs[index];
                return true;
            }

            job = default;
            return false;
        }

        /// <summary>
        /// Claims the lowest transaction whose sender has landed since the jobs were grouped, then every later ready
        /// one with the same sender, so a late chain still warms in order in a single scope; <c>false</c> when
        /// nothing is ready right now. <paramref name="readyElsewhere"/> is how many runs of other senders were ready
        /// and passed over on the way, which is how many more workers the wave could use. A ready transaction the
        /// main thread has already started is claimed too, but not returned: warming it would only contend with the
        /// main thread, and left unclaimed it would read as an arrival on every scan that nothing could ever act on.
        /// </summary>
        /// <remarks>
        /// A claim is never released, so plain reads are enough on this scan: a stale zero costs one failed exchange,
        /// and the exchange is what decides who owns the transaction. <see cref="WaitForArrival"/> reads the table
        /// volatile instead, because there the read is what ends a spin. The main thread's index is read once: it
        /// only ever grows, and <see cref="TxWarmupWorker.Warm"/> re-reads it before building a scope.
        /// </remarks>
        public bool TryClaimLate(ArrayPoolList<(int Index, Transaction Tx)> run, out int readyElsewhere)
        {
            Transaction[] txs = _txs;
            int[] claimed = _claimed;
            int mainThreadTxIndex = PreWarmer.MainThreadTxIndex;
            Address? sender = null;
            Address? passedOver = null;
            readyElsewhere = 0;
            for (int i = FirstUnclaimed(); i < _txCount; i++)
            {
                if (claimed[i] != 0) continue;
                Transaction tx = txs[i];
                Address? txSender = tx.SenderAddress;
                if (txSender is null) continue;

                bool overtaken = i <= mainThreadTxIndex;
                if (!overtaken && sender is not null && !txSender.Equals(sender))
                {
                    // Adjacent transactions of one sender are the one run a claim would take.
                    if (!txSender.Equals(passedOver))
                    {
                        passedOver = txSender;
                        readyElsewhere++;
                    }

                    continue;
                }

                if (Interlocked.CompareExchange(ref claimed[i], 1, 0) != 0) continue;
                if (overtaken) continue;
                // Already warmed speculatively — a job for it would do no work.
                if (tx.Hash is Hash256 hash && _speculativelyWarmed?.Contains(hash) == true) continue;

                sender ??= txSender;
                run.Add((i, tx));
            }

            return run.Count > 0;
        }

        /// <summary>The one worker that stays behind for stragglers; the rest are parked.</summary>
        public bool TryBecomeWaiter() => Interlocked.CompareExchange(ref _waiter, 1, 0) == 0;

        /// <summary>A worker of the fan-out is draining; it counts against the degree until it leaves.</summary>
        public void Enter() => Interlocked.Increment(ref _active);

        public void Leave() => Interlocked.Decrement(ref _active);

        /// <summary>
        /// Queues a helper for each run that is ready with no worker free for it, never beyond the degree the fan-out
        /// was given. The fan-out's own workers are parked as soon as they run dry and its range is spent once they
        /// have, so this is what gives a wave of late senders its parallelism back. A helper is a parked worker when
        /// there is one, rents its env on its own thread rather than the recruiter's, and is parked again as soon as
        /// nothing is ready, so no thread waits on the pool that recovery needs.
        /// </summary>
        /// <remarks>
        /// The block joins helpers on <c>_helpers</c>, so the count is only raised once the helper exists and is
        /// lowered again if it cannot be queued: a count left high would hold the join, and with it the processing
        /// thread at the top of the next block, forever.
        /// </remarks>
        public void Recruit(int wanted)
        {
            while (wanted-- > 0)
            {
                // A helper queued for a block that is already done would only be dispatched behind the pool's other
                // work to find its token cancelled, and the block's join would wait for that; the join sits on the
                // processing thread.
                if (Token.IsCancellationRequested) return;

                TxWarmupWorker helper = Rent();
                if (Interlocked.Increment(ref _active) > Degree)
                {
                    Interlocked.Decrement(ref _active);
                    Park(helper);
                    return;
                }

                Interlocked.Increment(ref _helpers);
                try
                {
                    ThreadPool.UnsafeQueueUserWorkItem(helper, preferLocal: false);
                }
                catch
                {
                    Park(helper);
                    HelperExited();
                    throw;
                }
            }
        }

        public void HelperExited()
        {
            Interlocked.Decrement(ref _active);
            if (Interlocked.Decrement(ref _helpers) != 0) return;

            lock (_helpersGate)
            {
                Monitor.PulseAll(_helpersGate);
            }
        }

        /// <summary>
        /// Joins the helpers, which are pool items outside the fan-out's own join. The count is checked under the gate
        /// the last helper pulses, and <see cref="Monitor.Wait(object)"/> releases it atomically, so the pulse cannot
        /// fall between the check and the wait.
        /// </summary>
        private void WaitForHelpers()
        {
            if (Volatile.Read(ref _helpers) == 0) return;

            lock (_helpersGate)
            {
                while (Volatile.Read(ref _helpers) > 0)
                {
                    Monitor.Wait(_helpersGate);
                }
            }
        }

        /// <summary>
        /// Waits until a transaction no worker has claimed has its sender; <c>false</c> once nothing more will land,
        /// the main thread has passed every unclaimed transaction, or the block is done. Every <c>true</c> is followed
        /// by a claim in <see cref="TryClaimLate"/> - of the transaction, or of the fact that the main thread reached
        /// it first - so the caller always makes progress before asking again.
        /// </summary>
        /// <remarks>
        /// Inside the spin window the table is rescanned only when the recovery's count has moved; past it the wait
        /// is on the recovery's completion, which pulses it awake, and every wake rescans. The speculative caller
        /// has no recovery in flight and never enters the loop; neither does a block whose recovery ended before it
        /// was warmed, where an unclaimed transaction can only be one with an invalid signature.
        /// </remarks>
        public bool WaitForArrival()
        {
            ISenderRecoveryProgress? recovery = BlockState.Recovery;
            if (recovery is null) return false;

            CancellationToken cancellationToken = Token;
            Transaction[] txs = _txs;
            int[] claimed = _claimed;
            int seen = -1;
            int lastPending = int.MaxValue;
            long start = Stopwatch.GetTimestamp();
            SpinWait spinner = default;
            while (!cancellationToken.IsCancellationRequested)
            {
                bool completed = recovery.IsCompleted;
                int recovered = recovery.Recovered;
                if (recovered != seen || completed)
                {
                    seen = recovered;
                    lastPending = -1;
                    for (int i = FirstUnclaimed(); i < _txCount; i++)
                    {
                        if (Volatile.Read(ref claimed[i]) != 0) continue;
                        if (txs[i].SenderAddress is not null) return true;
                        lastPending = i;
                    }

                    // Nothing is left, or nothing more will land and what is pending has no valid signature.
                    if (lastPending < 0 || completed) return false;
                }

                // The main thread has executed everything still pending — warming an account it has already read
                // only contends with it.
                if (PreWarmer.MainThreadTxIndex >= lastPending) return false;

                // The threshold disables SpinWait's own Sleep(1) backoff, so SenderArrivalWindow is the only bound
                // on the busy spin; past it the wait is the recovery's, bounded so cancellation is seen within a
                // millisecond, and every wake rescans: a sender can be filled in by a path the count never sees,
                // such as the transaction processor recovering it inline.
                if (Stopwatch.GetElapsedTime(start) < SenderArrivalWindow)
                {
                    spinner.SpinOnce(sleep1Threshold: -1);
                }
                else
                {
                    recovery.WaitForCompletion(1);
                    seen = -1;
                }
            }

            return false;
        }

        /// <summary>A claim is never released, so the lower bound of the unclaimed range only moves forward.</summary>
        private int FirstUnclaimed()
        {
            int seen = Volatile.Read(ref _firstUnclaimed);
            int first = seen;
            int[] claimed = _claimed;
            while (first < _txCount && claimed[first] != 0) first++;
            if (first > seen) Interlocked.CompareExchange(ref _firstUnclaimed, first, seen);
            return first;
        }

        /// <summary>A parked worker, or a new one when none is parked; it has no env yet.</summary>
        private TxWarmupWorker Rent()
        {
            TxWarmupWorker? worker;
            using (_parkedLock.EnterScope())
            {
                worker = _parked;
                if (worker is not null) _parked = worker.NextParked;
            }

            return worker ?? new TxWarmupWorker(this);
        }

        /// <summary>The fan-out's init: runs on the worker's own thread, so the env is rented there.</summary>
        private TxWarmupWorker RentAttached()
        {
            TxWarmupWorker worker = Rent();
            worker.Attach();
            return worker;
        }

        /// <summary>Takes the worker's env back and keeps the worker for the next rent, this block's or a later one's.</summary>
        public void Park(TxWarmupWorker worker)
        {
            worker.Detach();
            using (_parkedLock.EnterScope())
            {
                worker.NextParked = _parked;
                _parked = worker;
            }
        }

        /// <remarks>
        /// The prewarmer joins only its speculative session before disposing, so a reactive warm may still be running
        /// here; its lists and jobs are then left to the collector rather than returned under a fan-out still
        /// indexing them, since a pooled array returned twice corrupts the pool silently.
        /// </remarks>
        public void Dispose()
        {
            if (!TryAcquire()) return;

            using (_parkedLock.EnterScope())
            {
                for (TxWarmupWorker? worker = _parked; worker is not null; worker = worker.NextParked)
                {
                    worker.Dispose();
                }

                _parked = null;
            }

            _scratch.Dispose();
        }
    }

    /// <summary>
    /// A transaction-warming worker, kept by its queue across blocks with the run list it claims into; it holds an
    /// env only from rent to park. The fan-out's own workers run <see cref="Drain"/> and are parked by its loop
    /// finalizer; a helper recruited for a wave of late senders is queued to the pool directly and runs
    /// <see cref="Execute"/>.
    /// </summary>
    private sealed class TxWarmupWorker(WarmupQueue queue) : IThreadPoolWorkItem, IDisposable
    {
        private readonly WarmupQueue _queue = queue;
        // Reused for every late run this worker claims, so claiming allocates nothing per job.
        private readonly ArrayPoolList<(int Index, Transaction Tx)> _run = new(4);
        private IPrewarmerEnv? _env;

        /// <summary>Link of the queue's parked list; owned by the queue.</summary>
        public TxWarmupWorker? NextParked;

        public void Attach() => _env = _queue.PreWarmer._envPool.Get();

        /// <summary>Returns the env if one was rented; a helper whose rent threw parks without one.</summary>
        public void Detach()
        {
            IPrewarmerEnv? env = _env;
            if (env is null) return;
            _env = null;
            _queue.PreWarmer._envPool.Return(env);
        }

        public void Park() => _queue.Park(this);

        /// <summary>
        /// One of the fan-out's own workers; it counts against the degree from here until it leaves, however it leaves.
        /// On a CPU with performance and efficiency cores the first workers stay on the performance cores and warm what
        /// the processing thread reaches next; the rest stay on the efficiency cores, warm the far end of the block
        /// and leave the late arrivals, which the processing thread reaches soon, to the others.
        /// </summary>
        public void Drain(int slot)
        {
            WarmupQueue queue = _queue;
            BlockCachePreWarmer preWarmer = queue.PreWarmer;
            PerformanceCores.PrewarmSplit? split = preWarmer._coreSplit;
            bool far = split is not null && slot >= split.NearWorkers;
            using PerformanceCores.Scope cores = split is null ? default : far ? split.NarrowFar(preWarmer._logger) : split.NarrowNear(preWarmer._logger);
            queue.Enter();
            try
            {
                CancellationToken token = queue.Token;
                while (!token.IsCancellationRequested && queue.TryTakeJob(far, out WarmupJob job))
                {
                    Warm(job.Transactions.AsSpan(), job.LastIndex);
                }

                if (far) return;

                bool waiter = false;
                while (!token.IsCancellationRequested)
                {
                    if (WarmLate()) continue;

                    // Nothing is ready: one worker stays for stragglers, the rest are parked rather than left waiting
                    // on the pool while the recovery they would wait for needs it.
                    if (!waiter && !(waiter = queue.TryBecomeWaiter())) return;
                    if (!queue.WaitForArrival()) return;
                }
            }
            finally
            {
                queue.Leave();
            }
        }

        /// <summary>
        /// A helper recruited for a wave of late senders. Nothing may escape a pool work item, and the block joins
        /// helpers on the count <see cref="WarmupQueue.HelperExited"/> lowers, so the env is rented and returned
        /// inside the guarded region and the count is lowered whatever happened before. Parking comes before the
        /// count-out: the moment the count reaches zero the block may unload and dispose the queue, and a worker
        /// parked after that would sit on a disposed list. Nothing here touches the worker after it is parked, so
        /// being rented again at once is harmless.
        /// </summary>
        public void Execute()
        {
            try
            {
                CancellationToken token = _queue.Token;
                // Dispatched after the block finished: leave without renting an env.
                if (token.IsCancellationRequested) return;

                BlockCachePreWarmer preWarmer = _queue.PreWarmer;
                // Late runs are the ones the processing thread reaches soon.
                using PerformanceCores.Scope cores = preWarmer._coreSplit?.NarrowNear(preWarmer._logger) ?? default;
                try
                {
                    Attach();
                    while (!token.IsCancellationRequested && WarmLate()) { }
                }
                finally
                {
                    Detach();
                }
            }
            catch (OperationCanceledException)
            {
                // Ignore, block completed cancel
            }
            catch (Exception ex)
            {
                _queue.PreWarmer._logger.DebugError("Error pre-warming late transactions", ex);
            }
            finally
            {
                Park();
                _queue.HelperExited();
            }
        }

        /// <summary>
        /// Claims and warms the next late run; <c>false</c> when nothing is ready. Every run of another sender that
        /// was ready at the same time gets a helper, up to the fan-out's degree.
        /// </summary>
        private bool WarmLate()
        {
            ArrayPoolList<(int Index, Transaction Tx)> run = _run;
            run.Clear();
            if (!_queue.TryClaimLate(run, out int readyElsewhere)) return false;
            if (readyElsewhere > 0) _queue.Recruit(readyElsewhere);
            Warm(run.AsSpan(), run[^1].Index);
            return true;
        }

        private void Warm(ReadOnlySpan<(int Index, Transaction Tx)> transactions, int lastIndex)
        {
            BlockState blockState = _queue.BlockState;
            // Indices are ascending, so if the main thread has started the job's last tx it has started them all;
            // the per-tx guard would discard each one, so skip before building a scope.
            if (blockState.PreWarmer.MainThreadTxIndex >= lastIndex) return;

            CancellationToken token = _queue.Token;
            // Each job builds and disposes its own scope, so no speculative state crosses jobs.
            using IReadOnlyTxProcessingScope scope = _env!.BuildAtTarget(blockState.Block.Header);
            BlockExecutionContext context = new(blockState.Block.Header, blockState.Spec);
            scope.TransactionProcessor.SetBlockExecutionContext(context);

            foreach ((int txIndex, Transaction tx) in transactions)
            {
                if (token.IsCancellationRequested) return;
                WarmupSingleTransaction(scope, tx, txIndex, blockState, token);
            }
        }

        public void Dispose() => _run.Dispose();
    }

    private sealed record WarmMarker(Hash256 ParentHash, IReleaseSpec Spec, ISet<Hash256> WarmedTxHashes);
}
