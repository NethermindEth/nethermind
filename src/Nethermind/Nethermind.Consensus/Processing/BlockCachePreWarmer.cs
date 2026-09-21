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

    /// <summary>Yielding spin iterations a discovery worker takes before it starts sleeping for a sender.</summary>
    private const int SenderWaitSpins = 16;

    private readonly int _concurrencyLevel;
    // Speculative warming runs in the idle gap alongside RPC, so it is capped below the reactive level to leave cores free.
    private readonly int _speculativeConcurrencyLevel;
    private readonly bool _parallelExecutionBatchRead;
    private readonly ObjectPool<IPrewarmerEnv> _envPool;
    private readonly ILogger _logger;
    private readonly PreBlockCaches _preBlockCaches;
    private readonly NodeStorageCache _nodeStorageCache;
    private readonly bool _parallelExecutionEnabled;

    private const int MaxDiscoveryCandidates = 16;
    private const int MaxDiscoveryRounds = 6;
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
        NodeStorageCache nodeStorageCache,
        PreBlockCaches preBlockCaches,
        ILogManager logManager
    ) : this(
        new ReadOnlyTxProcessingEnvPooledObjectPolicy(envFactory, preBlockCaches),
        Environment.ProcessorCount * 2,
        blocksConfig.PreWarmStateConcurrency,
        blocksConfig.ParallelExecutionBatchRead,
        nodeStorageCache,
        preBlockCaches,
        logManager,
        blocksConfig.MempoolPreWarmConcurrency) => _parallelExecutionEnabled = blocksConfig.ParallelExecution;

    internal BlockCachePreWarmer(
        IPooledObjectPolicy<IPrewarmerEnv> poolPolicy,
        int minPoolSize,
        int concurrency,
        bool parallelExecutionBatchRead,
        NodeStorageCache nodeStorageCache,
        PreBlockCaches preBlockCaches,
        ILogManager logManager,
        int speculativeConcurrency = 0)
    {
        _concurrencyLevel = concurrency == 0 ? Environment.ProcessorCount - 1 : concurrency;
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
            carried = _preBlockCaches.PrepareFor(parent?.StateRoot, _logger);
        }

        bool skipReactiveWarming = !ShouldPreWarm(spec) || ShouldSkipReactiveWarming(suggestedBlock, spec);
        // The marker's tx set only means anything while the entries it describes are still in the caches.
        ISet<Hash256>? speculativelyWarmed =
            TryConsumeWarmMarker(suggestedBlock.ParentHash, spec, out ISet<Hash256>? warmed) && carried ? warmed : null;
        if (speculativelyWarmed is not null)
        {
            // Handoff taken: the RLP cache holds the session's nodes for this parent, so keep RLP caching on for execution.
            _nodeStorageCache.Enabled = true;
        }
        else
        {
            _nodeStorageCache.ClearCaches();
            // Without a handoff or a reactive pass, leave RLP caching disabled for execution.
            if (skipReactiveWarming) return Task.CompletedTask;
            _nodeStorageCache.Enabled = true;
        }

        if (skipReactiveWarming) return Task.CompletedTask;
        return WarmCaches(suggestedBlock, parent, spec, speculativelyWarmed, cancellationToken);
    }

    private Task WarmCaches(Block suggestedBlock, BlockHeader? parent, IReleaseSpec spec, ISet<Hash256>? speculativelyWarmed, CancellationToken cancellationToken)
    {
        if (parent is null || _concurrencyLevel <= 1 || cancellationToken.IsCancellationRequested) return Task.CompletedTask;

        (BlockState blockState, ParallelOptions parallelOptions, AddressWarmer addressWarmer) = PrepareWarm(suggestedBlock, spec, speculativelyWarmed, _concurrencyLevel, cancellationToken, warmSystemAccessLists: true);
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

        Task discoveryTask = Task.Run(() => DiscoverAndWarmStorageSafely(discoveryCandidates, suggestedBlock, spec, cancellationToken));
        return Task.WhenAll(normalWarmTask, discoveryTask);
    }

    private void DiscoverAndWarmStorageSafely(List<(int Index, Transaction Tx)> candidates, Block block, IReleaseSpec spec, CancellationToken cancellationToken)
    {
        try
        {
            DiscoverAndWarmStorage(candidates, block, spec, cancellationToken);
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

    internal void DiscoverAndWarmStorage(List<(int Index, Transaction Tx)> candidates, Block block, IReleaseSpec spec, CancellationToken cancellationToken)
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

            if (roundCells.Count > 0)
            {
                allDiscoveredCells.UnionWith(roundCells);
                if (!WarmDiscoveredStorage(block.Header, roundCells, cancellationToken)) return;
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

    /// <summary>Waits for a candidate's sender; <c>false</c> once the main thread has passed it or the block is done.</summary>
    /// <remarks>
    /// This wait skips the 1 ms spin window the other two open with: the same ascending-recovery argument that
    /// makes the wait worth taking makes it a long one, and several discovery workers spinning would take cores
    /// from the very recovery they are waiting on. It still opens with a handful of <see cref="SpinWait.SpinOnce()"/> iterations,
    /// which yield rather than burn a core past the first few, because the sleep below is
    /// <see cref="WaitHandle.WaitOne(int)"/> and rounds up to the platform timer tick — some 15 ms on Windows at the
    /// default resolution, against a discovery window of tens.
    /// </remarks>
    private bool WaitForSender((int Index, Transaction Tx) candidate, DiscoveryRound round)
    {
        SpinWait spinner = default;
        while (candidate.Tx.SenderAddress is null)
        {
            // Once the main thread is there, discovering its reads no longer helps and only contends.
            if (round.CancellationToken.IsCancellationRequested || MainThreadTxIndex >= candidate.Index) return false;

            if (spinner.Count < SenderWaitSpins) spinner.SpinOnce();
            else if (SleepUnlessDone(round.CancellationToken)) return false;
        }

        return true;
    }

    private void DiscoverTransactionStorageReads((int Index, Transaction Tx) candidate, DiscoveryRound round)
    {
        // Already started by the main thread — warming it now is redundant and contends; skip.
        if (MainThreadTxIndex >= candidate.Index) return;

        Transaction tx = candidate.Tx;
        if (tx.SenderAddress is null)
        {
            // Recovery hands senders out in ascending order and so reaches the heaviest transactions last, which
            // are exactly the ones selected here. Wait for this one rather than spend a round on it: rounds are
            // the chained-read depth budget, and six of them elapse well inside the recovery latency.
            if (!WaitForSender(candidate, round)) return;
            // The sender can land just as the main thread arrives; re-check before speculating on a heavy transaction.
            if (MainThreadTxIndex >= candidate.Index) return;
        }

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
        (BlockState blockState, ParallelOptions parallelOptions, AddressWarmer addressWarmer) = PrepareWarm(delta, spec, speculativelyWarmed: null, _speculativeConcurrencyLevel, token, warmSystemAccessLists);
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

    private (BlockState BlockState, ParallelOptions ParallelOptions, AddressWarmer AddressWarmer) PrepareWarm(Block block, IReleaseSpec spec, ISet<Hash256>? speculativelyWarmed, int maxDegreeOfParallelism, CancellationToken token, bool warmSystemAccessLists)
    {
        BlockState blockState = new(this, block, spec, speculativelyWarmed);
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
            _nodeStorageCache.ClearCaches();
            _nodeStorageCache.Enabled = true;

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
        int txCount = blockState.Block.Transactions.Length;
        if (txCount == 0 || parallelOptions.CancellationToken.IsCancellationRequested) return;

        // Senders may still be arriving while the block is already being processed (recovery runs in
        // ascending index order, see RecoverSignatures.StartRecovery), so each pass warms what is
        // recovered and the next pass picks up the rest, until nothing is left ahead of the main thread.
        bool[] claimed = ArrayPool<bool>.Shared.Rent(txCount);
        Array.Clear(claimed, 0, txCount);
        int firstUnclaimed = 0;
        bool morePasses;
        do
        {
            morePasses = WarmupRecoveredTransactions(blockState, parallelOptions, claimed)
                         && WaitForMoreSenders(blockState.Block.Transactions, claimed, ref firstUnclaimed, parallelOptions.CancellationToken);
        }
        while (morePasses);

        ArrayPool<bool>.Shared.Return(claimed);
    }

    /// <summary>
    /// Waits until a transaction no pass has claimed yet has its sender recovered; <c>false</c> once the main
    /// thread has passed every unclaimed transaction or the block is done.
    /// </summary>
    /// <param name="firstUnclaimed">Lower bound of the unclaimed range; a claim is never released, so it only moves forward.</param>
    /// <remarks>
    /// The speculative caller pins <c>MainThreadTxIndex</c> to -1, so that exit never fires for it. It builds its
    /// delta from the txpool, where every sender is already recovered, so the first pass claims everything and
    /// this is not entered at all.
    /// </remarks>
    private bool WaitForMoreSenders(Transaction[] txs, bool[] claimed, ref int firstUnclaimed, CancellationToken cancellationToken)
    {
        while (firstUnclaimed < txs.Length && claimed[firstUnclaimed]) firstUnclaimed++;
        if (firstUnclaimed == txs.Length) return false;

        int lastPending = Array.LastIndexOf(claimed, false, txs.Length - 1);

        long start = Stopwatch.GetTimestamp();
        SpinWait spinner = default;
        while (!cancellationToken.IsCancellationRequested && MainThreadTxIndex < lastPending)
        {
            for (int i = firstUnclaimed; i <= lastPending; i++)
            {
                if (!claimed[i] && txs[i].SenderAddress is not null) return true;
            }

            // The threshold disables SpinWait's own Sleep(1) backoff, so SenderArrivalWindow is the only bound
            // on the busy spin: past it the sender may never arrive at all, and a spinning core would starve the
            // very recovery this waits for. Waiting on the token rather than sleeping then keeps the end-of-block
            // join, which runs on the processing thread, from paying out the rest of a sleep quantum.
            if (Stopwatch.GetElapsedTime(start) < SenderArrivalWindow)
            {
                spinner.SpinOnce(sleep1Threshold: -1);
            }
            else if (SleepUnlessDone(cancellationToken)) return false;
        }

        return false;
    }

    /// <summary>Sleeps a millisecond unless the block finishes first; <c>true</c> once it has.</summary>
    /// <remarks>
    /// BranchProcessor disposes the token source before it joins the prewarm task, so a disposed source is
    /// itself the signal that the block is done.
    /// </remarks>
    private static bool SleepUnlessDone(CancellationToken cancellationToken)
    {
        try
        {
            return cancellationToken.WaitHandle.WaitOne(1);
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    /// <returns><c>false</c> when the pass ended early, so the caller must not run another.</returns>
    private bool WarmupRecoveredTransactions(BlockState blockState, ParallelOptions parallelOptions, bool[] claimed)
    {
        if (parallelOptions.CancellationToken.IsCancellationRequested) return false;

        try
        {
            Block block = blockState.Block;
            if (block.Transactions.Length == 0) return false;

            // Group transactions by sender: an unsplit group warms sequentially so state changes
            // (balance, storage) from tx[N] are visible to tx[N+1]; exceptionally heavy chains are
            // split into independent parent-state jobs. Warming is speculative — canonical
            // execution never consumes its writes — so the split only trades warm relevance for
            // parallelism, never correctness.
            using ArrayPoolList<WarmupJob> senderGroups =
                GroupTransactionsBySender(block, parallelOptions.MaxDegreeOfParallelism, blockState.SpeculativelyWarmed, claimed);
            if (senderGroups.Count == 0) return true;

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

                        using IReadOnlyTxProcessingScope scope = worker.Env.BuildAtTarget(blockState.Block.Header);
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
            return false;
        }
        catch (Exception ex)
        {
            _logger.DebugError("Error pre-warming transactions", ex);
            return false;
        }

        return true;
    }

    internal static ArrayPoolList<WarmupJob> GroupTransactionsBySender(Block block, int maxWorkers, ISet<Hash256>? speculativelyWarmed = null, bool[]? claimed = null)
    {
        Dictionary<AddressAsKey, ArrayPoolList<(int, Transaction)>> groups = [];

        for (int i = 0; i < block.Transactions.Length; i++)
        {
            if (claimed is not null && claimed[i]) continue;

            Transaction tx = block.Transactions[i];
            if (tx.SenderAddress is not Address sender)
            {
                // Not recovered yet (a later pass picks it up) or an invalid signature (the block will be rejected).
                continue;
            }

            if (claimed is not null) claimed[i] = true;
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
                    int count = block.Transactions.Length + (block.InclusionListTransactions?.Length ?? 0);

                    // Unlike the transaction warmup a pass never revisits an index, so a sender recovered after
                    // its index went by would lose its account warm for the whole block. Indices keep their
                    // claim once warmed, so later passes only revisit what is still waiting for a sender.
                    bool[] warmed = ArrayPool<bool>.Shared.Rent(count);
                    // End-of-block cancellation throws out of the passes below; that is the routine exit here,
                    // not an unexpected one, so the rental is returned on it rather than left to the GC.
                    try
                    {
                        Array.Clear(warmed, 0, count);
                        // Recipients are known up front, so only the first pass warms them; later passes exist
                        // solely to pick up senders that had not been recovered yet.
                        bool warmRecipients = true;
                        do
                        {
                            WarmingState<(Block Block, bool[] Warmed, bool WarmRecipients)> baseState =
                                new(envPool, (block, warmed, warmRecipients), block.Header);
                            ParallelUnbalancedWork.For(
                                0,
                                count,
                                parallelOptions,
                                baseState.InitThreadState,
                                WarmupSenderAt,
                                WarmingState<(Block, bool[], bool)>.FinallyAction);
                            warmRecipients = false;
                        }
                        while (WaitForMoreSenders(block, warmed, count, parallelOptions.CancellationToken));
                    }
                    finally
                    {
                        ArrayPool<bool>.Shared.Return(warmed);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Ignore, block completed cancel
            }
        }

        private static WarmingState<(Block Block, bool[] Warmed, bool WarmRecipients)> WarmupSenderAt(
            int i,
            WarmingState<(Block Block, bool[] Warmed, bool WarmRecipients)> state)
        {
            if (state.Payload.Warmed[i]) return state;

            Transaction tx = TransactionAt(state.Payload.Block, i);
            // One read: a sender arriving between warming and claiming would mark the index warmed without it,
            // and no later pass revisits a claimed index.
            Address? sender = tx.SenderAddress;
            WarmupSender(sender, state.Payload.WarmRecipients ? tx.To : null, state.Scope!.WorldState);
            if (sender is not null) state.Payload.Warmed[i] = true;

            return state;
        }

        /// <summary>Indexes past the block transactions address inclusion-list ones, which may be promoted into the block.</summary>
        private static Transaction TransactionAt(Block block, int i)
        {
            Transaction[] txs = block.Transactions;
            return i < txs.Length ? txs[i] : block.InclusionListTransactions![i - txs.Length];
        }

        /// <summary>
        /// Waits until an index no pass has warmed yet has its sender; <c>false</c> once every index is warmed
        /// or the block is done.
        /// </summary>
        /// <remarks>
        /// Waiting for a sender rather than re-testing at once is what keeps a steady trickle of arrivals from
        /// queueing a fresh fan-out, and a scope build per worker, to warm a single address.
        /// </remarks>
        private bool WaitForMoreSenders(Block block, bool[] warmed, int count, CancellationToken cancellationToken)
        {
            long start = Stopwatch.GetTimestamp();
            SpinWait spinner = default;
            while (!cancellationToken.IsCancellationRequested)
            {
                int lastPending = -1;
                for (int i = 0; i < count; i++)
                {
                    if (warmed[i]) continue;
                    if (TransactionAt(block, i).SenderAddress is not null) return true;
                    // Only a block transaction can still be waiting on the background recovery. The pipeline step
                    // recovers inclusion-list entries itself before prewarming starts, so a null sender there is
                    // an invalid signature and final — waiting on one would rescan for the rest of the block.
                    if (i < block.Transactions.Length) lastPending = i;
                }

                // Nothing left, or the main thread has executed everything still pending — warming an account
                // it has already read only contends with it. A sender that never arrives exits here too.
                if (lastPending < 0 || PreWarmer.MainThreadTxIndex >= lastPending) return false;

                // SenderArrivalWindow is the only bound on the spin: the threshold disables SpinWait's own
                // Sleep(1) backoff, so past the window this must sleep instead of taking a core from recovery.
                if (Stopwatch.GetElapsedTime(start) < SenderArrivalWindow) spinner.SpinOnce(sleep1Threshold: -1);
                else if (SleepUnlessDone(cancellationToken)) return false;
            }

            return false;
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

    private record BlockState(BlockCachePreWarmer PreWarmer, Block Block, IReleaseSpec Spec, ISet<Hash256>? SpeculativelyWarmed = null);

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
        public readonly IPrewarmerEnv Env = blockState.PreWarmer._envPool.Get();

        public void ReturnEnv() => BlockState.PreWarmer._envPool.Return(Env);
    }

    private sealed record WarmMarker(Hash256 ParentHash, IReleaseSpec Spec, ISet<Hash256> WarmedTxHashes);
}
