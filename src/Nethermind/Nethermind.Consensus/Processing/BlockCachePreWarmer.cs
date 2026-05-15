// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Threading;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Evm.State;
using Nethermind.State;
using Nethermind.Core.Eip2930;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Trie;

namespace Nethermind.Consensus.Processing;

public sealed class BlockCachePreWarmer : IBlockCachePreWarmer
{
    private const int AutoConcurrencyLimit = 4;
    private const int SmallBlockLinearDedupThreshold = 8;
    private readonly int _concurrencyLevel;
    private readonly bool _parallelExecutionBatchRead;
    private readonly ObjectPool<IReadOnlyTxProcessorSource> _envPool;
    private readonly ILogger _logger;
    private readonly PreBlockCaches _preBlockCaches;
    private readonly NodeStorageCache _nodeStorageCache;
    private readonly bool _parallelExecutionEnabled;
    private readonly double _firstPassRatio;
    private readonly PreWarmRetryMode _retryMode;
    private readonly PreWarmFirstPassMode _firstPassMode;
    private readonly int _headStartMs;
    internal bool HeadStartEnabled => _headStartMs > 0;

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
        blocksConfig.PreWarmFirstPassRatio,
        ParseRetryMode(blocksConfig.PreWarmRetryMode),
        ParseFirstPassMode(blocksConfig.PreWarmFirstPassMode),
        blocksConfig.PreWarmHeadStartMs,
        nodeStorageCache,
        preBlockCaches,
        logManager) => _parallelExecutionEnabled = blocksConfig.ParallelExecution;

    internal BlockCachePreWarmer(
        IPooledObjectPolicy<IReadOnlyTxProcessorSource> poolPolicy,
        int maxPoolSize,
        int concurrency,
        bool parallelExecutionBatchRead,
        double firstPassRatio,
        PreWarmRetryMode retryMode,
        PreWarmFirstPassMode firstPassMode,
        int headStartMs,
        NodeStorageCache nodeStorageCache,
        PreBlockCaches preBlockCaches,
        ILogManager logManager)
    {
        _concurrencyLevel = concurrency == 0 ? Math.Clamp(Environment.ProcessorCount - 2, 1, AutoConcurrencyLimit) : concurrency;
        _parallelExecutionBatchRead = parallelExecutionBatchRead;
        _firstPassRatio = Math.Clamp(firstPassRatio, 0.0, 1.0);
        _retryMode = retryMode;
        _firstPassMode = firstPassMode;
        _headStartMs = headStartMs;
        _envPool = new DefaultObjectPoolProvider { MaximumRetained = maxPoolSize }.Create(poolPolicy);
        _logger = logManager.GetClassLogger<BlockCachePreWarmer>();
        _preBlockCaches = preBlockCaches;
        _nodeStorageCache = nodeStorageCache;
    }

    private static PreWarmRetryMode ParseRetryMode(string retryMode)
    {
        if (string.Equals(retryMode, "Hammer", StringComparison.OrdinalIgnoreCase))
        {
            return PreWarmRetryMode.Hammer;
        }

        if (string.Equals(retryMode, "None", StringComparison.OrdinalIgnoreCase))
        {
            return PreWarmRetryMode.None;
        }

        return PreWarmRetryMode.StateGated;
    }

    private static PreWarmFirstPassMode ParseFirstPassMode(string firstPassMode)
    {
        if (string.Equals(firstPassMode, "Forward", StringComparison.OrdinalIgnoreCase))
        {
            return PreWarmFirstPassMode.Forward;
        }

        if (string.Equals(firstPassMode, "Lookahead", StringComparison.OrdinalIgnoreCase))
        {
            return PreWarmFirstPassMode.Lookahead;
        }

        return PreWarmFirstPassMode.SenderGrouped;
    }

    public Task PreWarmCaches(Block suggestedBlock, BlockHeader? parent, IReleaseSpec spec, CancellationToken cancellationToken = default, params ReadOnlySpan<IHasAccessList> systemAccessLists)
    {
        if (_preBlockCaches is not null && ShouldPreWarm(spec))
        {
            CacheType result = _preBlockCaches.ClearCaches();
            _nodeStorageCache.ClearCaches();
            _nodeStorageCache.ClearCaches();
            _nodeStorageCache.Enabled = true;
            if (result != default)
            {
                if (_logger.IsWarn) _logger.Warn($"Caches {result} are not empty. Clearing them.");
            }

            if (parent is not null && _concurrencyLevel > 1 && !cancellationToken.IsCancellationRequested)
            {
                int txCount = suggestedBlock.Transactions.Length;
                DiagPrewarmerCompletedAt = new long[txCount];
                DiagMainThreadStartedAt = new long[txCount];
                DiagBlockStartTicks = Stopwatch.GetTimestamp();
                SpeculativeGasUsed = new long[txCount];

                BlockState blockState = new(this, suggestedBlock, parent, spec);
                ParallelOptions parallelOptions = new() { MaxDegreeOfParallelism = _concurrencyLevel, CancellationToken = cancellationToken };

                // BAL makes speculative tx execution redundant — when BAL-based read warming
                // is in use, drive warmup directly off the suggested block's access list.
                BlockAccessList? bal = IsBalReadWarmingEnabled(spec) ? suggestedBlock.BlockAccessList : null;

                // Run address warmer ahead of transactions warmer, but queue to ThreadPool so it doesn't block the txs
                AddressWarmer addressWarmer = new(parallelOptions, suggestedBlock, parent, spec, systemAccessLists, this, bal);
                ThreadPool.UnsafeQueueUserWorkItem(addressWarmer, preferLocal: false);
                // Do not pass the cancellation token to the task, we don't want exceptions to be thrown in the main processing thread
                Task task = Task.Run(() => PreWarmCachesParallel(blockState, suggestedBlock, parent, spec, parallelOptions, addressWarmer, cancellationToken));
                ActiveTask = task;
                return task;
            }
        }

        return Task.CompletedTask;
    }

    // Pre-warming runs in two distinct modes:
    //  - Speculative tx execution (default): runs txs against a snapshot to seed caches.
    //    Skipped when parallel execution will actually run, because parallel execution keeps
    //    its results rather than throwing them away after warmup. Parallel execution requires
    //    BAL, so when BAL isn't active for this spec we still need speculative prewarming.
    //  - BAL-based read warming: when parallel execution is on AND batch read is enabled,
    //    we still warm — but only by reading state/storage referenced by the block's
    //    access list (no tx execution).
    private bool ShouldPreWarm(IReleaseSpec spec)
        => !_parallelExecutionEnabled
        || !spec.BlockLevelAccessListsEnabled
        || IsBalReadWarmingEnabled(spec);

    public bool IsBalReadWarmingEnabled(IReleaseSpec spec)
        => _parallelExecutionBatchRead && spec.BlockLevelAccessListsEnabled;

    public CacheType ClearCaches()
    {
        if (_logger.IsDebug) _logger.Debug("Clearing caches");
        CacheType cachesCleared = _preBlockCaches?.ClearCaches() ?? default;
        cachesCleared |= _nodeStorageCache.ClearCaches() ? CacheType.Rlp : CacheType.None;
        if (_logger.IsDebug) _logger.Debug($"Cleared caches: {cachesCleared}");
        return cachesCleared;
    }

    public void Dispose()
    {
        (_envPool as IDisposable)?.Dispose();
        _mainThreadAdvanced.Dispose();
    }

    private void PreWarmCachesParallel(BlockState blockState, Block suggestedBlock, BlockHeader parent, IReleaseSpec spec, ParallelOptions parallelOptions, AddressWarmer addressWarmer, CancellationToken cancellationToken)
    {
        bool previousIsBlockProcessingThread = ProcessingThread.IsBlockProcessingThread;
        ProcessingThread.IsBlockProcessingThread = false;
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
            try
            {
                // Don't complete the task until address warmer is also done.
                addressWarmer.Wait();
                addressWarmer.Dispose();
            }
            finally
            {
                ProcessingThread.IsBlockProcessingThread = previousIsBlockProcessingThread;
            }
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

    /// <summary>
    /// Reference to the main thread's live WorldState. Prewarmer reads committed
    /// account/storage state from it via read-through fallback (unsafe concurrent read).
    /// </summary>
    internal IWorldState? MainThreadWorldState;

    /// <summary>
    /// The running prewarmer task. BlockProcessor can await this after cancellation
    /// to ensure prewarmer threads are fully stopped before merkle computation.
    /// </summary>
    internal Task? ActiveTask;

    // Diagnostic: timestamps (Stopwatch ticks) when prewarmer completes each tx
    internal long[]? DiagPrewarmerCompletedAt;
    // Diagnostic: timestamp when main thread starts processing each tx
    internal long[]? DiagMainThreadStartedAt;
    internal long DiagBlockStartTicks;

    // Speculative result: gas used by prewarmer for each tx (0 = not completed or failed)
    internal long[]? SpeculativeGasUsed;

    internal static long DiagTxStartedCallCount;

    internal void ReportMainThreadTxStarted(int txIndex)
    {
        Interlocked.Increment(ref DiagTxStartedCallCount);
        long[]? arr = DiagMainThreadStartedAt;
        if (arr is not null && (uint)txIndex < (uint)arr.Length)
            arr[txIndex] = Stopwatch.GetTimestamp();
    }

    internal string GetDiagTimingReport(int maxTxs = 20)
    {
        long[]? pw = DiagPrewarmerCompletedAt;
        long[]? mt = DiagMainThreadStartedAt;
        if (pw is null || mt is null) return "no timing data";

        long start = DiagBlockStartTicks;
        double ticksToMs = 1000.0 / Stopwatch.Frequency;
        System.Text.StringBuilder sb = new();
        sb.AppendLine("tx | prewarm_ms | main_ms | lead_ms | status");
        int count = Math.Min(Math.Min(pw.Length, mt.Length), maxTxs);
        int ahead = 0, behind = 0, notWarmed = 0;
        for (int i = 0; i < count; i++)
        {
            double pwMs = pw[i] == 0 ? -1 : (pw[i] - start) * ticksToMs;
            double mtMs = mt[i] == 0 ? -1 : (mt[i] - start) * ticksToMs;
            double lead = (mtMs >= 0 && pwMs >= 0) ? mtMs - pwMs : 0;
            string status = pw[i] == 0 ? "NOT_WARMED" : (lead > 0 ? "AHEAD" : "BEHIND");
            if (pw[i] == 0) notWarmed++; else if (lead > 0) ahead++; else behind++;
            sb.AppendLine($"{i,3} | {pwMs,10:F3} | {mtMs,8:F3} | {lead,8:F3} | {status}");
        }
        if (pw.Length > maxTxs)
        {
            for (int i = maxTxs; i < pw.Length; i++)
            {
                if (pw[i] == 0) notWarmed++;
                else if (mt[i] != 0 && mt[i] > pw[i]) ahead++;
                else behind++;
            }
        }
        int storCount = _preBlockCaches?.DiagStorageCacheCount() ?? 0;
        int acctCount = _preBlockCaches?.DiagAccountCacheCount() ?? 0;
        int specSuccess = 0;
        long[]? spec = SpeculativeGasUsed;
        if (spec is not null) { for (int i = 0; i < spec.Length; i++) { if (spec[i] > 0) specSuccess++; } }
        sb.AppendLine($"Summary: {ahead} ahead, {behind} behind, {notWarmed} not warmed (of {pw.Length} txs). Speculative: {specSuccess}/{spec?.Length ?? 0} succeeded. SeqlockCache: acct={acctCount} stor={storCount}");
        return sb.ToString();
    }

    private readonly ManualResetEventSlim _firstPassDone = new(false);

    /// <summary>
    /// Block until the prewarmer's first pass completes or timeout expires.
    /// Called by the main thread before starting EVM execution.
    /// </summary>
    internal void WaitForFirstPass()
    {
        if (_headStartMs <= 0) return;
        // Always wait the full duration — the prewarmer fills caches during this window.
        // Don't use the signal, since first pass may complete early but we still want
        // the full head start for retry passes to build up more cache hits.
        Thread.Sleep(_headStartMs);
    }

    /// <summary>
    /// Incremented by the main block processor after each tx.
    /// Workers abandon their current tx when the main thread passes it.
    /// </summary>
    internal int MainThreadTxIndex = -1;
    private readonly ManualResetEventSlim _mainThreadAdvanced = new(false);

    internal void ReportMainThreadTxExecuted(int txIndex)
    {
        Volatile.Write(ref MainThreadTxIndex, txIndex);
        _mainThreadAdvanced.Set();
    }

    private int _nextWarmupIndex;

    private void WarmupTransactions(BlockState blockState, ParallelOptions parallelOptions)
    {
        if (parallelOptions.CancellationToken.IsCancellationRequested) return;

        try
        {
            Block block = blockState.Block;
            int txCount = block.Transactions.Length;
            if (txCount == 0) return;

            int firstPassLimit = (int)(txCount * _firstPassRatio);
            Volatile.Write(ref _nextWarmupIndex, 0);
            Volatile.Write(ref MainThreadTxIndex, -1);
            _firstPassDone.Reset();
            _mainThreadAdvanced.Reset();

            int threadCount = Math.Min(_concurrencyLevel, txCount);
            BlockCachePreWarmer preWarmer = this;
            CancellationToken token = parallelOptions.CancellationToken;
            PreWarmRetryMode retryMode = _retryMode;
            PreWarmFirstPassMode firstPassMode = _firstPassMode;

            Thread[] workers = new Thread[threadCount];
            SenderGroups senderGroups = firstPassMode == PreWarmFirstPassMode.SenderGrouped
                ? SenderGroups.Build(block, firstPassLimit)
                : default;
            int firstPassWorkItems = firstPassMode == PreWarmFirstPassMode.SenderGrouped ? senderGroups.Count : firstPassLimit;
            try
            {
                for (int t = 0; t < threadCount; t++)
                {
                    workers[t] = new Thread(() =>
                    {
                        IReadOnlyTxProcessorSource env = _envPool.Get();
                        try
                        {
                            // Phase 1: one execution per tx, no retry.
                            while (!token.IsCancellationRequested)
                            {
                                int workItem = Interlocked.Increment(ref _nextWarmupIndex) - 1;
                                if (workItem >= firstPassWorkItems) break;

                                if (firstPassMode == PreWarmFirstPassMode.SenderGrouped)
                                {
                                    WarmupSenderGroup(
                                        env,
                                        senderGroups[workItem],
                                        blockState,
                                        block,
                                        preWarmer,
                                        token);
                                }
                                else
                                {
                                    int txIndex = firstPassMode == PreWarmFirstPassMode.Lookahead
                                        ? txCount - 1 - workItem
                                        : workItem;
                                    WarmupTransactionByIndex(env, blockState, block, preWarmer, txIndex);

                                    long[]? completedAt = preWarmer.DiagPrewarmerCompletedAt;
                                    if (completedAt is not null && (uint)txIndex < (uint)completedAt.Length && completedAt[txIndex] == 0)
                                        completedAt[txIndex] = Stopwatch.GetTimestamp();
                                }
                            }

                            if (firstPassLimit > 0) preWarmer._firstPassDone.Set();

                            if (!token.CanBeCanceled || retryMode == PreWarmRetryMode.None) return;

                            // Phase 2: retry mode, re-warm txs with fresher state.
                            Interlocked.CompareExchange(ref _nextWarmupIndex, 0, firstPassWorkItems + threadCount);

                            while (!token.IsCancellationRequested)
                            {
                                int myTx = Interlocked.Increment(ref _nextWarmupIndex) - 1;
                                if (myTx >= txCount)
                                {
                                    Interlocked.CompareExchange(ref _nextWarmupIndex, 0, txCount + threadCount);
                                    continue;
                                }

                                int lastSeenMain = Volatile.Read(ref MainThreadTxIndex);
                                if (lastSeenMain >= myTx) continue;

                                WarmupTransactionByIndex(env, blockState, block, preWarmer, myTx);

                                long[]? completedAt = preWarmer.DiagPrewarmerCompletedAt;
                                if (completedAt is not null && (uint)myTx < (uint)completedAt.Length && completedAt[myTx] == 0)
                                    completedAt[myTx] = Stopwatch.GetTimestamp();

                                if (retryMode == PreWarmRetryMode.Hammer)
                                {
                                    // Hammer: keep re-executing until main thread passes.
                                    // Each retry uses a fresh scope so read-through fallback can observe newer state.
                                    while (!token.IsCancellationRequested)
                                    {
                                        if (Volatile.Read(ref MainThreadTxIndex) >= myTx) break;

                                        WarmupTransactionByIndex(env, blockState, block, preWarmer, myTx);
                                    }
                                }
                                else
                                {
                                    // State-gated: re-execute only when main thread advances.
                                    while (!token.IsCancellationRequested)
                                    {
                                        if (lastSeenMain >= myTx) break;

                                        while (!token.IsCancellationRequested)
                                        {
                                            int current = Volatile.Read(ref MainThreadTxIndex);
                                            if (current > lastSeenMain) { lastSeenMain = current; break; }
                                            if (current >= myTx) { lastSeenMain = current; break; }

                                            _mainThreadAdvanced.Reset();
                                            current = Volatile.Read(ref MainThreadTxIndex);
                                            if (current > lastSeenMain) { lastSeenMain = current; break; }
                                            if (current >= myTx) { lastSeenMain = current; break; }

                                            _mainThreadAdvanced.Wait(token);
                                        }

                                        if (lastSeenMain >= myTx) break;

                                        WarmupTransactionByIndex(env, blockState, block, preWarmer, myTx);
                                    }
                                }
                            }
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception) { }
                        finally
                        {
                            _envPool.Return(env);
                        }
                    }) { IsBackground = true, Name = "PrewarmWorker" };
                    workers[t].Start();
                }

                for (int t = 0; t < threadCount; t++)
                    workers[t].Join();
            }
            finally
            {
                senderGroups.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.DebugError("Error pre-warming transactions", ex);
        }
    }

    private static Dictionary<AddressAsKey, ArrayPoolList<(int Index, Transaction Tx)>> GroupTransactionsBySender(Block block, int txLimit)
    {
        Dictionary<AddressAsKey, ArrayPoolList<(int, Transaction)>> groups = new();

        for (int i = 0; i < txLimit; i++)
        {
            Transaction tx = block.Transactions[i];
            Address sender = tx.SenderAddress!;

            if (!groups.TryGetValue(sender, out ArrayPoolList<(int, Transaction)> list))
            {
                list = new(4);
                groups[sender] = list;
            }
            list.Add((i, tx));
        }

        return groups;
    }

    private static void WarmupTransactionByIndex(
        IReadOnlyTxProcessorSource env,
        BlockState blockState,
        Block block,
        BlockCachePreWarmer preWarmer,
        int txIndex)
    {
        if (Volatile.Read(ref preWarmer.MainThreadTxIndex) >= txIndex) return;

        using IReadOnlyTxProcessingScope scope = env.Build(blockState.Parent);
        BlockExecutionContext context = new(block.Header, blockState.Spec);
        scope.TransactionProcessor.SetBlockExecutionContext(context);

        if (preWarmer.MainThreadWorldState is not null)
            (scope.WorldState as WorldState)?.SetReadFallback(preWarmer.MainThreadWorldState);

        WarmupSingleTransaction(scope, block.Transactions[txIndex], txIndex, blockState);
    }

    private static void WarmupSenderGroup(
        IReadOnlyTxProcessorSource env,
        ArrayPoolList<(int Index, Transaction Tx)> txList,
        BlockState blockState,
        Block block,
        BlockCachePreWarmer preWarmer,
        CancellationToken token)
    {
        using IReadOnlyTxProcessingScope scope = env.Build(blockState.Parent);
        BlockExecutionContext context = new(block.Header, blockState.Spec);
        scope.TransactionProcessor.SetBlockExecutionContext(context);

        if (preWarmer.MainThreadWorldState is not null)
            (scope.WorldState as WorldState)?.SetReadFallback(preWarmer.MainThreadWorldState);

        foreach ((int txIndex, Transaction tx) in txList.AsSpan())
        {
            if (token.IsCancellationRequested) break;
            if (Volatile.Read(ref preWarmer.MainThreadTxIndex) >= txIndex) continue;

            WarmupSingleTransaction(scope, tx, txIndex, blockState);
        }
    }

    private readonly struct SenderGroups : IDisposable
    {
        private readonly Dictionary<AddressAsKey, ArrayPoolList<(int Index, Transaction Tx)>>? _bySender;
        private readonly ArrayPoolList<ArrayPoolList<(int Index, Transaction Tx)>>? _groups;

        private SenderGroups(
            Dictionary<AddressAsKey, ArrayPoolList<(int Index, Transaction Tx)>> bySender,
            ArrayPoolList<ArrayPoolList<(int Index, Transaction Tx)>> groups)
        {
            _bySender = bySender;
            _groups = groups;
        }

        public int Count => _groups?.Count ?? 0;

        public ArrayPoolList<(int Index, Transaction Tx)> this[int index] => _groups![index];

        public static SenderGroups Build(Block block, int txLimit)
        {
            Dictionary<AddressAsKey, ArrayPoolList<(int Index, Transaction Tx)>> bySender = GroupTransactionsBySender(block, txLimit);
            return new(bySender, bySender.Values.ToPooledList());
        }

        public void Dispose()
        {
            if (_groups is null || _bySender is null) return;

            _groups.Dispose();

            foreach (ArrayPoolList<(int Index, Transaction Tx)> group in _bySender.Values)
            {
                group.Dispose();
            }
        }
    }

    private static bool WarmupSingleTransaction(
        IReadOnlyTxProcessingScope scope,
        Transaction tx,
        int txIndex,
        BlockState blockState)
    {
        try
        {
            Address senderAddress = tx.SenderAddress!;
            IWorldState worldState = scope.WorldState;

            if (!worldState.AccountExists(senderAddress))
            {
                worldState.CreateAccountIfNotExists(senderAddress, UInt256.Zero);
            }

            if (blockState.Spec.UseTxAccessLists)
            {
                worldState.WarmUp(tx.AccessList); // eip-2930
            }

            TransactionResult result = scope.TransactionProcessor.Warmup(tx, NullTxTracer.Instance);

            // Record speculative success for adoption measurement
            long[]? specGas = blockState.PreWarmer.SpeculativeGasUsed;
            if (result && specGas is not null && (uint)txIndex < (uint)specGas.Length)
            {
                specGas[txIndex] = 1; // Mark as successfully speculated
            }

            if (blockState.PreWarmer._logger.IsTrace) blockState.PreWarmer._logger.Trace($"Finished pre-warming cache for tx[{txIndex}] {tx.Hash} with {result}");
            return result;
        }
        catch (Exception ex) when (ex is EvmException or OverflowException)
        {
            return false;
        }
        catch (Exception ex)
        {
            blockState.PreWarmer._logger.DebugError($"Error pre-warming cache {tx.Hash}", ex);
            return false;
        }
    }

    private class AddressWarmer(ParallelOptions parallelOptions, Block block, BlockHeader parent, IReleaseSpec spec, ReadOnlySpan<IHasAccessList> systemAccessLists, BlockCachePreWarmer preWarmer, BlockAccessList? bal = null)
        : IThreadPoolWorkItem, IDisposable
    {
        private readonly Block Block = block;
        private readonly BlockCachePreWarmer PreWarmer = preWarmer;
        private readonly BlockAccessList? Bal = bal;
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
                if (SystemTxAccessLists is not null)
                {
                    IReadOnlyTxProcessorSource env = envPool.Get();
                    try
                    {
                        using IReadOnlyTxProcessingScope scope = env.Build(parent);

                        foreach (AccessList list in SystemTxAccessLists.AsSpan())
                        {
                            scope.WorldState.WarmUp(list);
                        }
                    }
                    finally
                    {
                        envPool.Return(env);
                        SystemTxAccessLists.Dispose();
                    }
                }

                if (Bal is not null && Bal.AccountChanges.Count > 0)
                {
                    WarmupFromBal(parallelOptions, envPool);
                }
                else
                {
                    using ArrayPoolList<AddressAsKey> addresses = CollectWarmupAddresses(block);
                    if (addresses.Count == 0) return;

                    WarmingState<ArrayPoolList<AddressAsKey>> baseState = new(envPool, addresses, parent);

                    // Warm accounts, code, and storage trie roots in one pass.
                    // This pre-fetches trie paths from RocksDB into block cache so both
                    // the prewarmer's EVM execution and the main thread are faster.
                    ParallelUnbalancedWork.For(
                        0,
                        addresses.Count,
                        parallelOptions,
                        baseState.InitThreadState,
                    static (i, state) =>
                    {
                        Address addr = state.Payload.GetRef(i);
                        IWorldState ws = state.Scope!.WorldState;
                        try
                        {
                            ws.WarmUp(addr);
                            if (ws.HasCode(addr))
                            {
                                ws.GetCode(addr);
                                ws.IsStorageEmpty(addr);
                            }
                        }
                        catch (MissingTrieNodeException) { }

                        return state;
                    },
                    WarmingState<ArrayPoolList<AddressAsKey>>.FinallyAction);

                    // Fast access list warmup: warm EIP-2930 storage slots for all txs.
                    // Much faster than full EVM execution and builds a lead on storage reads.
                    if (spec.UseTxAccessLists)
                    {
                        WarmingState<Block> alState = new(envPool, block, parent);
                        ParallelUnbalancedWork.For(
                            0,
                            block.Transactions.Length,
                            parallelOptions,
                            alState.InitThreadState,
                            static (i, state) =>
                            {
                                Transaction tx = state.Payload.Transactions[i];
                                if (tx.AccessList is not null)
                                    state.Scope!.WorldState.WarmUp(tx.AccessList);
                                return state;
                            },
                            WarmingState<Block>.FinallyAction);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Ignore, block completed cancel
            }
        }

        private static ArrayPoolList<AddressAsKey> CollectWarmupAddresses(Block block)
        {
            Transaction[] transactions = block.Transactions;
            int estimatedAddressCount = transactions.Length * 2;
            ArrayPoolList<AddressAsKey> addresses = new(estimatedAddressCount);
            HashSet<AddressAsKey>? seen = transactions.Length > SmallBlockLinearDedupThreshold
                ? new(estimatedAddressCount)
                : null;

            for (int i = 0; i < transactions.Length; i++)
            {
                Transaction tx = transactions[i];
                AddWarmupAddress(addresses, seen, tx.SenderAddress);
                AddWarmupAddress(addresses, seen, tx.To);
            }

            return addresses;
        }

        private static void AddWarmupAddress(ArrayPoolList<AddressAsKey> addresses, HashSet<AddressAsKey>? seen, Address? address)
        {
            if (address is null)
            {
                return;
            }

            AddressAsKey addressKey = address;
            if (seen is not null)
            {
                if (seen.Add(addressKey))
                {
                    addresses.Add(addressKey);
                }

                return;
            }

            for (int i = 0; i < addresses.Count; i++)
            {
                if (addresses[i].Equals(addressKey))
                {
                    return;
                }
            }

            addresses.Add(addressKey);
        }

        private void WarmupFromBal(ParallelOptions parallelOptions, ObjectPool<IReadOnlyTxProcessorSource> envPool)
        {
            using ArrayPoolList<AccountChanges> accounts = Bal!.AccountChanges.ToPooledList(Bal!.AccountChanges.Count);

            WarmingState<ArrayPoolList<AccountChanges>> baseState = new(envPool, accounts, parent);

            ParallelUnbalancedWork.For(
                0,
                accounts.Count,
                parallelOptions,
                baseState.InitThreadState,
                static (i, state) =>
                {
                    AccountChanges ac = state.Payload[i];
                    IWorldState worldState = state.Scope!.WorldState;

                    WarmupBalAccount(ac, worldState);

                    return state;
                },
                WarmingState<ArrayPoolList<AccountChanges>>.FinallyAction);
        }

        private static void WarmupBalAccount(AccountChanges ac, IWorldState worldState)
        {
            try
            {
                Address address = ac.Address;
                worldState.WarmUp(address);

                // Merge two sorted sequences (ChangedSlots, SortedStorageReads) into one
                // ascending pass for better trie path locality
                ReadOnlySpan<UInt256> changed = ac.ChangedSlots;
                ReadOnlySpan<UInt256> reads = ac.SortedStorageReads;
                int slotIndex = 0;
                int readIndex = 0;

                while (slotIndex < changed.Length || readIndex < reads.Length)
                {
                    UInt256 slot;
                    if (readIndex >= reads.Length)
                    {
                        slot = changed[slotIndex++];
                    }
                    else
                    {
                        slot = reads[readIndex];
                        if (slotIndex < changed.Length && changed[slotIndex].CompareTo(in slot) <= 0)
                        {
                            slot = changed[slotIndex++];
                        }
                        else
                        {
                            readIndex++;
                        }
                    }
                    worldState.Get(new StorageCell(address, slot));
                }
            }
            catch (MissingTrieNodeException)
            {
            }
        }

        private static void WarmupAddress(AddressAsKey address, IWorldState worldState)
        {
            try
            {
                worldState.WarmUp(address);
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
            return new(EnvPool, Payload, parent, env, scope: env.Build(parent));
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

    private record BlockState(BlockCachePreWarmer PreWarmer, Block Block, BlockHeader Parent, IReleaseSpec Spec, IWorldState? MainThreadState = null);

    internal enum PreWarmRetryMode
    {
        None,
        Hammer,
        StateGated,
    }

    internal enum PreWarmFirstPassMode
    {
        SenderGrouped,
        Forward,
        Lookahead,
    }
}
