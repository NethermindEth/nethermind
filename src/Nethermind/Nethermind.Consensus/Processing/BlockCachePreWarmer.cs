// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Diagnostics;
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
    private readonly bool _gasPriorityOrder;
    private readonly ulong _skipStartedMaxGas = ulong.MaxValue;
    private readonly bool _earlyKickoffEnabled;
    private readonly ISpecProvider? _specProvider;
    private readonly IHasAccessList? _beaconBlockRootHandler;

    private Task _clearTask = Task.CompletedTask;
    private Hash256? _earlyWarmedBlock;
    private Task? _earlyTask;
    private CancellationTokenSource? _earlyCts;

    private int _mainThreadTxIndex = -1;
    internal int MainThreadTxIndex => Volatile.Read(ref _mainThreadTxIndex);

    public BlockCachePreWarmer(
        PrewarmerEnvFactory envFactory,
        IBlocksConfig blocksConfig,
        ISpecProvider specProvider,
        IBeaconBlockRootHandler beaconBlockRootHandler,
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
        _gasPriorityOrder = blocksConfig.PreWarmGasPriorityOrder;
        _skipStartedMaxGas = blocksConfig.PreWarmSkipStartedMaxGas;
        _earlyKickoffEnabled = blocksConfig.PreWarmEarlyKickoff;
        _specProvider = specProvider;
        _beaconBlockRootHandler = beaconBlockRootHandler;
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
            if (_earlyWarmedBlock is not null && _earlyWarmedBlock == suggestedBlock.Hash)
            {
                // Early kickoff already cleared the caches and is warming this exact block from the same
                // parent basis — keep its warmth (and let a still-running early pass finish; Sets are idempotent).
                _earlyWarmedBlock = null;
            }
            else
            {
                // Reorg or no early phase: drain any stale-basis early writers BEFORE clearing so a
                // straggler cannot repopulate the cleared caches with wrong-parent values.
                DrainEarlyWarm();
                CacheType result = _preBlockCaches.ClearCaches();
                _nodeStorageCache.ClearCaches();
                _nodeStorageCache.Enabled = true;
                if (result != default)
                {
                    if (_logger.IsWarn) _logger.Warn($"Caches {result} are not empty. Clearing them.");
                }
            }

            if (parent is not null && _concurrencyLevel > 1 && !cancellationToken.IsCancellationRequested)
            {
                BlockState blockState = new(this, suggestedBlock, parent, spec);
                Volatile.Write(ref _mainThreadTxIndex, -1);
                ParallelOptions parallelOptions = new() { MaxDegreeOfParallelism = _concurrencyLevel, CancellationToken = cancellationToken };

                // BAL makes speculative tx execution redundant — when BAL-based read warming
                // is in use, drive warmup directly off the suggested block's access list.
                ReadOnlyBlockAccessList? bal = IsBalReadWarmingEnabled(spec) ? suggestedBlock.BlockAccessList : null;

                // System-call slots (e.g. EIP-4788 ring buffer) race the main thread's system call, which is
                // the very first thing the block executes — warm them on a dedicated item so they never queue
                // behind the address pass setup.
                SystemAccessListsWarmer systemWarmer = new(this, parent, GetAccessLists(suggestedBlock, spec, systemAccessLists));
                ThreadPool.UnsafeQueueUserWorkItem(systemWarmer, preferLocal: false);

                // Run address warmer ahead of transactions warmer, but queue to ThreadPool so it doesn't block the txs
                AddressWarmer addressWarmer = new(parallelOptions, suggestedBlock, parent, this, bal);
                ThreadPool.UnsafeQueueUserWorkItem(addressWarmer, preferLocal: false);
                // Do not pass the cancellation token to the task, we don't want exceptions to be thrown in the main processing thread
                return Task.Run(() => PreWarmCachesParallel(blockState, suggestedBlock, parent, spec, parallelOptions, addressWarmer, systemWarmer, cancellationToken));
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

    /// <summary>Reports main-thread progress (called via <see cref="PrewarmerTxAdapter"/>) so warming can skip already-started txs.</summary>
    /// <remarks>Only the single main execution thread writes, in ascending tx order, so a plain release store publishes progress to the polling warmup workers — no interlocked read-modify-write is needed.</remarks>
    public void OnBeforeTxExecution() => Volatile.Write(ref _mainThreadTxIndex, _mainThreadTxIndex + 1);

    public CacheType ClearCaches()
    {
        if (_logger.IsDebug) _logger.Debug("Clearing caches");
        DrainEarlyWarm();
        CacheType cachesCleared = _preBlockCaches?.ClearCaches() ?? default;
        cachesCleared |= _nodeStorageCache.ClearCaches() ? CacheType.Rlp : CacheType.None;
        if (_logger.IsDebug) _logger.Debug($"Cleared caches: {cachesCleared}");
        return cachesCleared;
    }

    public void WaitForCacheClear() => _clearTask.GetAwaiter().GetResult();

    public void QueueClearCaches(Task? preWarmTask)
    {
        if (preWarmTask is not null)
        {
            // Clear caches after prewarm completes; run inline to avoid ThreadPool scheduling jitter.
            _clearTask = preWarmTask.ContinueWith(static (_, state) => ((BlockCachePreWarmer)state!).ClearCaches(), this, TaskContinuationOptions.ExecuteSynchronously);
        }
        else
        {
            ClearCaches();
            _clearTask = Task.CompletedTask;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Runs on the block-processing thread before sender recovery: clears the caches for the incoming
    /// block (after awaiting the previous block's clear) and warms what is known without senders —
    /// tx.To targets and EIP-2930 access lists — overlapping recovery, branch setup, and scope open.
    /// The main <see cref="PreWarmCaches"/> detects the matching block hash and skips its entry clear.
    /// </remarks>
    public void PreWarmCachesEarly(Block suggestedBlock, BlockHeader parent)
    {
        if (!_earlyKickoffEnabled || _specProvider is null || _preBlockCaches is null || _concurrencyLevel <= 1) return;
        if (suggestedBlock.Transactions.Length < 3 || suggestedBlock.Hash is null) return;

        IReleaseSpec spec = _specProvider.GetSpec(suggestedBlock.Header);
        if (!ShouldPreWarm(spec)) return;

        WaitForCacheClear();
        DrainEarlyWarm();
        _preBlockCaches.ClearCaches();
        _nodeStorageCache.ClearCaches();
        _nodeStorageCache.Enabled = true;
        ReadTrace.BeginProvenance((long)suggestedBlock.Number);

        _earlyWarmedBlock = suggestedBlock.Hash;
        _earlyCts = new CancellationTokenSource();
        CancellationToken token = _earlyCts.Token;

        // System-call slots need the most lead: the beacon-roots call is the very first thing the
        // block executes, and a kickoff at PreWarmCaches time demonstrably loses that race.
        SystemAccessListsWarmer? systemWarmer = null;
        if (_beaconBlockRootHandler is not null)
        {
            systemWarmer = new SystemAccessListsWarmer(this, parent, GetAccessLists(suggestedBlock, spec, [_beaconBlockRootHandler]));
            ThreadPool.UnsafeQueueUserWorkItem(systemWarmer, preferLocal: false);
        }

        EarlyWarmer warmer = new(this, suggestedBlock, parent, spec, token);
        _earlyTask = Task.Run(() =>
        {
            try
            {
                warmer.Warm();
            }
            finally
            {
                systemWarmer?.Wait();
                systemWarmer?.Dispose();
            }
        });
    }

    private void DrainEarlyWarm()
    {
        Task? earlyTask = _earlyTask;
        if (earlyTask is null) return;

        _earlyCts?.Cancel();
        try
        {
            earlyTask.GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // Early warming is best-effort; failures must never surface on the processing path.
        }

        _earlyCts?.Dispose();
        _earlyCts = null;
        _earlyTask = null;
        _earlyWarmedBlock = null;
    }

    /// <summary>Sender-free warm pass: tx.To addresses and EIP-2930 access lists against parent state.</summary>
    private sealed class EarlyWarmer(BlockCachePreWarmer preWarmer, Block block, BlockHeader parent, IReleaseSpec spec, CancellationToken token)
    {
        public void Warm()
        {
            try
            {
                // Modest parallelism: sender recovery (ecrecover) runs concurrently on the same cores.
                ParallelOptions parallelOptions = new() { MaxDegreeOfParallelism = Math.Min(4, preWarmer._concurrencyLevel), CancellationToken = token };
                WarmingState<Block> baseState = new(preWarmer._envPool, block, parent);

                ParallelUnbalancedWork.For(
                    0,
                    block.Transactions.Length,
                    parallelOptions,
                    baseState.InitThreadState,
                    (i, state) =>
                    {
                        Transaction tx = state.Payload.Transactions[i];
                        IWorldState worldState = state.Scope!.WorldState;
                        WarmupSender(tx.SenderAddress, tx.To, worldState);
                        if (spec.UseTxAccessLists)
                        {
                            worldState.WarmUp(tx.AccessList, token);
                        }

                        return state;
                    },
                    WarmingState<Block>.FinallyAction);
            }
            catch (OperationCanceledException)
            {
                // Drained by the main phase or a reorg clear.
            }
            catch (Exception ex)
            {
                preWarmer._logger.DebugError("Error early pre-warming", ex);
            }
        }
    }

    public void Dispose() => (_envPool as IDisposable)?.Dispose();

    private void PreWarmCachesParallel(BlockState blockState, Block suggestedBlock, BlockHeader parent, IReleaseSpec spec, ParallelOptions parallelOptions, AddressWarmer addressWarmer, SystemAccessListsWarmer systemWarmer, CancellationToken cancellationToken)
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
            // Don't complete the task until the address and system warmers are also done.
            addressWarmer.Wait();
            addressWarmer.Dispose();
            systemWarmer.Wait();
            systemWarmer.Dispose();
        }
    }

    /// <summary>Warms the system-call access lists (e.g. the EIP-4788 beacon-roots slots) with minimal dispatch latency.</summary>
    private sealed class SystemAccessListsWarmer(BlockCachePreWarmer preWarmer, BlockHeader parent, ArrayPoolList<AccessList>? accessLists)
        : IThreadPoolWorkItem, IDisposable
    {
        private readonly ManualResetEventSlim _doneEvent = new(initialState: false);

        public void Wait() => _doneEvent.Wait();

        public void Dispose() => _doneEvent.Dispose();

        void IThreadPoolWorkItem.Execute()
        {
            try
            {
                if (accessLists is null) return;

                IReadOnlyTxProcessorSource env = preWarmer._envPool.Get();
                try
                {
                    using IReadOnlyTxProcessingScope scope = env.Build(parent);
                    foreach (AccessList list in accessLists.AsSpan())
                    {
                        scope.WorldState.WarmUp(list);
                    }
                }
                finally
                {
                    preWarmer._envPool.Return(env);
                }
            }
            catch (Exception ex)
            {
                preWarmer._logger.DebugError("Error pre-warming system access lists", ex);
            }
            finally
            {
                accessLists?.Dispose();
                _doneEvent.Set();
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

                if (_gasPriorityOrder)
                {
                    // Give the largest cold read sets a worker first: order groups by total gas descending.
                    // Grouping (sequential same-sender warming) is unchanged — only the claim order across groups.
                    groupArray.AsSpan().Sort(static (a, b) => TotalGasLimit(b).CompareTo(TotalGasLimit(a)));
                }

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

    private static ulong TotalGasLimit(ArrayPoolList<(int Index, Transaction Tx)> group)
    {
        ulong total = 0;
        foreach ((int _, Transaction tx) in group.AsSpan())
        {
            total += tx.GasLimit;
        }

        return total;
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
            // Already started by the main thread — warming it now is redundant and contends; skip.
            // High-gas txs are exempt (PreWarmSkipStartedMaxGas): racing the main thread inside a
            // large cold tx keeps its remaining reads one step ahead instead of leaving them all cold.
            if (tx.GasLimit < blockState.PreWarmer._skipStartedMaxGas && blockState.PreWarmer.MainThreadTxIndex >= txIndex) return;

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

    private class AddressWarmer(ParallelOptions parallelOptions, Block block, BlockHeader parent, BlockCachePreWarmer preWarmer, ReadOnlyBlockAccessList? bal = null)
        : IThreadPoolWorkItem, IDisposable
    {
        private readonly Block Block = block;
        private readonly BlockCachePreWarmer PreWarmer = preWarmer;
        private readonly ReadOnlyBlockAccessList? Bal = bal;
        private readonly ManualResetEventSlim _doneEvent = new(initialState: false);

        public bool HasBal => Bal is not null;

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
            if (parallelOptions.CancellationToken.IsCancellationRequested)
            {
                return;
            }

            ObjectPool<IReadOnlyTxProcessorSource> envPool = PreWarmer._envPool;
            try
            {
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

    private record BlockState(BlockCachePreWarmer PreWarmer, Block Block, BlockHeader Parent, IReleaseSpec Spec);
}
