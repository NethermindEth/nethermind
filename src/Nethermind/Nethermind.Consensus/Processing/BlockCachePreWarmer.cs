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
using Nethermind.Core.Eip2930;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Trie;

namespace Nethermind.Consensus.Processing;

public sealed class BlockCachePreWarmer : IBlockCachePreWarmer
{
    private readonly int _concurrencyLevel;
    private readonly bool _parallelExecutionBatchRead;
    private readonly ObjectPool<IReadOnlyTxProcessorSource> _envPool;
    private readonly ILogger _logger;
    private readonly PreBlockCaches _preBlockCaches;
    private readonly NodeStorageCache _nodeStorageCache;
    private readonly bool _parallelExecutionEnabled;

    // Skip speculatively re-executing transactions the main thread has already started (see PrewarmerTxAdapter).
    private readonly bool _skipStartedTxs;
    // >0: skip speculatively executing a dominating index-0 transaction whose gas limit exceeds this threshold.
    private readonly long _adaptiveAbortMinGas;
    // Emit per-block [PWDIAG] coverage diagnostics for heavy blocks.
    private readonly bool _diagnostics;
    // Run a concurrent single-scope sequential (index-order) warming pass to recover cross-tx-divergent reads.
    private readonly bool _sequentialShadow;
    private readonly int _sequentialShadowMinTx;

    // Tracks the block currently being prewarmed so the main processing thread (via PrewarmerTxAdapter)
    // can report its transaction progress, letting the prewarmer skip already-started transactions.
    private BlockState? _currentBlockState;

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
        logManager)
    {
        _parallelExecutionEnabled = blocksConfig.ParallelExecution;
        _skipStartedTxs = blocksConfig.PreWarmSkipStartedTxs;
        _adaptiveAbortMinGas = blocksConfig.PreWarmAdaptiveAbortMinGas;
        _diagnostics = blocksConfig.PreWarmDiagnostics;
        _sequentialShadow = blocksConfig.PreWarmSequentialShadow;
        _sequentialShadowMinTx = blocksConfig.PreWarmSequentialShadowMinTx;
    }

    internal BlockCachePreWarmer(
        IPooledObjectPolicy<IReadOnlyTxProcessorSource> poolPolicy,
        int maxPoolSize,
        int concurrency,
        bool parallelExecutionBatchRead,
        NodeStorageCache nodeStorageCache,
        PreBlockCaches preBlockCaches,
        ILogManager logManager)
    {
        _concurrencyLevel = concurrency == 0 ? Math.Min(Environment.ProcessorCount - 1, 16) : concurrency;
        _parallelExecutionBatchRead = parallelExecutionBatchRead;
        _envPool = new DefaultObjectPoolProvider { MaximumRetained = maxPoolSize }.Create(poolPolicy);
        _logger = logManager.GetClassLogger<BlockCachePreWarmer>();
        _preBlockCaches = preBlockCaches;
        _nodeStorageCache = nodeStorageCache;
    }

    public Task PreWarmCaches(Block suggestedBlock, BlockHeader? parent, IReleaseSpec spec, CancellationToken cancellationToken = default, params ReadOnlySpan<IHasAccessList> systemAccessLists)
    {
        if (_preBlockCaches is not null && ShouldPreWarm(spec))
        {
            CacheType result = _preBlockCaches.ClearCaches();
            _nodeStorageCache.ClearCaches();
            _nodeStorageCache.Enabled = true;
            if (result != default)
            {
                if (_logger.IsWarn) _logger.Warn($"Caches {result} are not empty. Clearing them.");
            }

            if (parent is not null && _concurrencyLevel > 1 && !cancellationToken.IsCancellationRequested)
            {
                BlockState blockState = new(this, suggestedBlock, parent, spec);
                _currentBlockState = blockState;
                ParallelOptions parallelOptions = new() { MaxDegreeOfParallelism = _concurrencyLevel, CancellationToken = cancellationToken };

                // BAL makes speculative tx execution redundant — when BAL-based read warming
                // is in use, drive warmup directly off the suggested block's access list.
                ReadOnlyBlockAccessList? bal = IsBalReadWarmingEnabled(spec) ? suggestedBlock.BlockAccessList : null;

                // Run address warmer ahead of transactions warmer, but queue to ThreadPool so it doesn't block the txs
                AddressWarmer addressWarmer = new(parallelOptions, suggestedBlock, parent, spec, systemAccessLists, this, bal);
                ThreadPool.UnsafeQueueUserWorkItem(addressWarmer, preferLocal: false);
                // Do not pass the cancellation token to the task, we don't want exceptions to be thrown in the main processing thread
                return Task.Run(() => PreWarmCachesParallel(blockState, suggestedBlock, parent, spec, parallelOptions, addressWarmer, cancellationToken));
            }
        }

        return Task.CompletedTask;
    }

    private bool ShouldPreWarm(IReleaseSpec spec)
        => !_parallelExecutionEnabled
        || !spec.BlockLevelAccessListsEnabled
        || IsBalReadWarmingEnabled(spec);

    public bool IsBalReadWarmingEnabled(IReleaseSpec spec)
        => _parallelExecutionBatchRead && spec.BlockLevelAccessListsEnabled;

    /// <summary>
    /// Called by the main processing thread (via <see cref="PrewarmerTxAdapter"/>) immediately before it
    /// executes each transaction, advancing the prewarmer's view of main-thread progress.
    /// </summary>
    /// <remarks>
    /// Lets the prewarmer skip transactions the main thread has already started, avoiding redundant
    /// speculative re-execution (and the cache/CPU contention it causes) of in-flight transactions,
    /// which frees warming capacity for transactions ahead of the main thread.
    /// </remarks>
    public void OnBeforeTxExecution(Transaction transaction) => _currentBlockState?.IncrementTransactionCounter();

    /// <summary>
    /// Whether to skip speculative <em>execution</em> of a transaction during warming (its sender and access
    /// list are still warmed cheaply by the caller).
    /// </summary>
    /// <remarks>
    /// The prewarmer starts together with the main processing thread, so it cannot get ahead of a heavy
    /// transaction at the very front of the block: the main thread begins executing it immediately. For such
    /// a dominating index-0 transaction, speculatively executing it does not pre-load anything the main
    /// thread reaches later — it only runs concurrently with the main thread's execution of the same
    /// transaction and contends with it (catastrophic for a compute-bound giant). Gated by gas so normal
    /// first transactions are unaffected; 0 disables the skip.
    /// </remarks>
    private bool SkipExecWarmup(Transaction tx, int txIndex)
        => _adaptiveAbortMinGas > 0 && txIndex == 0 && tx.GasLimit > (ulong)_adaptiveAbortMinGas;

    public CacheType ClearCaches()
    {
        if (_logger.IsDebug) _logger.Debug("Clearing caches");
        CacheType cachesCleared = _preBlockCaches?.ClearCaches() ?? default;
        cachesCleared |= _nodeStorageCache.ClearCaches() ? CacheType.Rlp : CacheType.None;
        if (_logger.IsDebug) _logger.Debug($"Cleared caches: {cachesCleared}");
        return cachesCleared;
    }

    public void Dispose() => (_envPool as IDisposable)?.Dispose();

    private void PreWarmCachesParallel(BlockState blockState, Block suggestedBlock, BlockHeader parent, IReleaseSpec spec, ParallelOptions parallelOptions, AddressWarmer addressWarmer, CancellationToken cancellationToken)
    {
        long startTs = _diagnostics ? Stopwatch.GetTimestamp() : 0;
        try
        {
            if (cancellationToken.IsCancellationRequested) return;

            if (_logger.IsDebug) _logger.Debug($"Started pre-warming caches for block {suggestedBlock.Number}.");

            if (!addressWarmer.HasBal)
            {
                // Optional sequential-shadow pass: runs concurrently with the parallel per-sender pass, sharing
                // the pre-block cache. Executing all txs in index order in one scope makes each tx see prior
                // txs' writes, so it warms the cross-tx-dependent (divergent) slots the parallel pass misses.
                Task shadow = _sequentialShadow && suggestedBlock.Transactions.Length >= _sequentialShadowMinTx
                    ? Task.Run(() => WarmupSequentialShadow(blockState, parallelOptions.CancellationToken))
                    : Task.CompletedTask;

                WarmupTransactions(blockState, parallelOptions);
                WarmupWithdrawals(parallelOptions, spec, suggestedBlock, parent);

                try { shadow.Wait(parallelOptions.CancellationToken); }
                catch (OperationCanceledException) { /* block completed */ }
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

            if (_diagnostics && !addressWarmer.HasBal)
            {
                // Coverage snapshot at the point warming stopped (usually when the main thread finished the
                // block and cancelled the prewarmer). warmed+skippedStarted+skippedExec vs txCount shows how
                // much of the block the prewarmer covered; a low ratio means the main thread paid cold reads.
                blockState.LogCoverage(_logger, Stopwatch.GetElapsedTime(startTs));
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

    private void WarmupTransactions(BlockState blockState, ParallelOptions parallelOptions)
    {
        if (parallelOptions.CancellationToken.IsCancellationRequested) return;

        try
        {
            Block block = blockState.Block;
            if (block.Transactions.Length == 0) return;

            // Group transactions by sender to process same-sender transactions sequentially
            // This ensures state changes (balance, storage) from tx[N] are visible to tx[N+1]
            Dictionary<AddressAsKey, ArrayPoolList<(int Index, Transaction Tx)>>? senderGroups = GroupTransactionsBySender(block);

            try
            {
                // Convert to array for parallel iteration
                using ArrayPoolList<ArrayPoolList<(int Index, Transaction Tx)>> groupArray = senderGroups.Values.ToPooledList();

                // Parallel across different senders, sequential within the same sender
                ParallelUnbalancedWork.For(
                    0,
                    groupArray.Count,
                    parallelOptions,
                    (blockState, groupArray, parallelOptions.CancellationToken),
                    static (groupIndex, tupleState) =>
                    {
                        (BlockState? blockState, ArrayPoolList<ArrayPoolList<(int Index, Transaction Tx)>> groups, CancellationToken token) = tupleState;
                        ArrayPoolList<(int Index, Transaction Tx)>? txList = groups[groupIndex];

                        // Get thread-local processing state for this sender's transactions
                        IReadOnlyTxProcessorSource env = blockState.PreWarmer._envPool.Get();
                        try
                        {
                            using IReadOnlyTxProcessingScope scope = env.Build(blockState.Parent);
                            BlockExecutionContext context = new(blockState.Block.Header, blockState.Spec);
                            scope.TransactionProcessor.SetBlockExecutionContext(context);

                            // Sequential within the same sender-state changes propagate correctly
                            foreach ((int txIndex, Transaction? tx) in txList.AsSpan())
                            {
                                if (token.IsCancellationRequested) return tupleState;
                                WarmupSingleTransaction(scope, tx, txIndex, blockState, token);
                            }
                        }
                        finally
                        {
                            blockState.PreWarmer._envPool.Return(env);
                        }

                        return tupleState;
                    });
            }
            finally
            {
                foreach (KeyValuePair<AddressAsKey, ArrayPoolList<(int Index, Transaction Tx)>> kvp in senderGroups)
                    kvp.Value.Dispose();
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

    /// <summary>
    /// Sequential-shadow warming pass: executes all of the block's transactions in index order within a single
    /// scope, so each transaction observes the state changes written by the preceding ones.
    /// </summary>
    /// <remarks>
    /// The parallel per-sender pass executes every transaction from the parent post-state, so a transaction whose
    /// control flow / read keys depend on state a preceding cross-sender transaction wrote (e.g. one that reverts
    /// early on stale parent state) reads a different slot set than the main thread does — leaving those
    /// intermediate-state-dependent slots cold. Running one in-order scope reproduces the accurate intermediate
    /// state and warms exactly those slots. It shares the pre-block cache with the parallel pass, so it only pays
    /// cold reads for the divergent minority; because the main thread is read-bound and serial, this pass can warm
    /// those slots ahead of it. Warming only — runs on a discarded scratch scope and never affects committed state.
    /// </remarks>
    private void WarmupSequentialShadow(BlockState blockState, CancellationToken cancellationToken)
    {
        try
        {
            if (cancellationToken.IsCancellationRequested) return;
            Block block = blockState.Block;
            IReadOnlyTxProcessorSource env = _envPool.Get();
            try
            {
                using IReadOnlyTxProcessingScope scope = env.Build(blockState.Parent);
                scope.TransactionProcessor.SetBlockExecutionContext(new BlockExecutionContext(block.Header, blockState.Spec));
                ReadOnlySpan<Transaction> txs = block.Transactions;
                for (int i = 0; i < txs.Length; i++)
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    Transaction tx = txs[i];
                    if (tx.SenderAddress is null) continue; // invalid signature — block will be rejected
                    WarmupSequentialTx(scope, tx, blockState, cancellationToken);
                }
            }
            finally
            {
                _envPool.Return(env);
            }
        }
        catch (OperationCanceledException)
        {
            // Block completed — stop.
        }
        catch (Exception ex)
        {
            _logger.DebugError("Error in sequential-shadow pre-warming", ex);
        }
    }

    /// <summary>Warm a single transaction on the shared in-order shadow scope (no skip gates — accuracy is the point).</summary>
    private static void WarmupSequentialTx(IReadOnlyTxProcessingScope scope, Transaction tx, BlockState blockState, CancellationToken cancellationToken)
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
                worldState.WarmUp(tx.AccessList, cancellationToken);
            }
            scope.TransactionProcessor.Warmup(tx, NullTxTracer.Instance);
            blockState.RecordWarmed();
        }
        catch (Exception ex) when (ex is EvmException or OverflowException)
        {
            // Regular tx processing exceptions during speculative warming — ignore.
        }
        catch (Exception ex)
        {
            blockState.PreWarmer._logger.DebugError($"Error sequential-shadow warming cache {tx.Hash}", ex);
        }
    }

    private static Dictionary<AddressAsKey, ArrayPoolList<(int Index, Transaction Tx)>> GroupTransactionsBySender(Block block)
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

            if (!groups.TryGetValue(sender, out ArrayPoolList<(int, Transaction)> list))
            {
                list = new(4);
                groups[sender] = list;
            }
            list.Add((i, tx));
        }

        return groups;
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
            // Skip transactions the main thread has already started: re-executing them speculatively is
            // wasted work and contends with the main thread (severe for a heavy tx at a low index). Their
            // state is already loaded by the main thread's own execution.
            if (blockState.PreWarmer._skipStartedTxs && blockState.LastExecutedTransaction >= txIndex)
            {
                blockState.RecordSkippedStarted();
                return;
            }

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

            if (blockState.PreWarmer.SkipExecWarmup(tx, txIndex))
            {
                blockState.RecordSkippedExec();
                return;
            }

            TransactionResult result = scope.TransactionProcessor.Warmup(tx, NullTxTracer.Instance);
            blockState.RecordWarmed();

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

                // BAL warmup is driven from BlockProcessor.HintBal; skip speculative warming here.
                if (Bal is null)
                {
                    WarmingState<Block> baseState = new(envPool, block, parent);

                    ParallelUnbalancedWork.For(
                        0,
                        block.Transactions.Length,
                        parallelOptions,
                        baseState.InitThreadState,
                    static (i, state) =>
                    {
                        Transaction tx = state.Payload.Transactions[i];
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

    private record BlockState(BlockCachePreWarmer PreWarmer, Block Block, BlockHeader Parent, IReleaseSpec Spec)
    {
        // Written only by the single main thread (in order) via IncrementTransactionCounter; read by prewarmer threads.
        private int _lastExecutedTransaction = -1;
        // Diagnostic coverage counters, incremented from prewarmer threads.
        private int _warmedCount;
        private int _skippedStartedCount;
        private int _skippedExecCount;

        /// <summary>Index of the last transaction the main thread has started executing (-1 before any).</summary>
        public int LastExecutedTransaction => Volatile.Read(ref _lastExecutedTransaction);
        public void IncrementTransactionCounter() => Interlocked.Increment(ref _lastExecutedTransaction);

        public void RecordWarmed() => Interlocked.Increment(ref _warmedCount);
        public void RecordSkippedStarted() => Interlocked.Increment(ref _skippedStartedCount);
        public void RecordSkippedExec() => Interlocked.Increment(ref _skippedExecCount);

        public void LogCoverage(ILogger logger, TimeSpan wall)
        {
            int txCount = Block.Transactions.Length;
            // Only the heaviest (tail) blocks matter for tail analysis; keep the log sparse (~tens of lines
            // per 1000-block run) so it does not perturb the P99 measurement.
            if (txCount < 150 && Block.GasUsed < 28_000_000) return;
            int warmed = Volatile.Read(ref _warmedCount);
            int skippedStarted = Volatile.Read(ref _skippedStartedCount);
            int skippedExec = Volatile.Read(ref _skippedExecCount);
            if (logger.IsInfo)
            {
                logger.Info(
                    $"[PWDIAG] blk={Block.Number} txs={txCount} gas={Block.GasUsed / 1_000_000}M " +
                    $"warmed={warmed} skipStarted={skippedStarted} skipExec={skippedExec} " +
                    $"mainReachedTx={LastExecutedTransaction} pwWallMs={wall.TotalMilliseconds:F1}");
            }
        }
    }
}
