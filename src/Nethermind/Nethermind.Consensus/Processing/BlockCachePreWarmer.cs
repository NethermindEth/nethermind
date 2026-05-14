// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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
    private readonly PrewarmerTaskScheduler _dedicatedScheduler;
    private readonly TaskFactory _dedicatedFactory;

    /// <summary>
    /// Shared with the block processor so the prewarmer can skip transactions
    /// that main execution has already processed (like Reth's atomic tx index).
    /// </summary>
    public TxExecutionProgress TxProgress { get; } = new();

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
        logManager) => _parallelExecutionEnabled = blocksConfig.ParallelExecution;

    internal BlockCachePreWarmer(
        IPooledObjectPolicy<IReadOnlyTxProcessorSource> poolPolicy,
        int maxPoolSize,
        int concurrency,
        bool parallelExecutionBatchRead,
        NodeStorageCache nodeStorageCache,
        PreBlockCaches preBlockCaches,
        ILogManager logManager)
    {
        _concurrencyLevel = concurrency == 0 ? Math.Max(2, Environment.ProcessorCount / 2) : concurrency;
        _parallelExecutionBatchRead = parallelExecutionBatchRead;
        _envPool = new DefaultObjectPoolProvider { MaximumRetained = maxPoolSize }.Create(poolPolicy);
        _logger = logManager.GetClassLogger<BlockCachePreWarmer>();
        _preBlockCaches = preBlockCaches;
        _nodeStorageCache = nodeStorageCache;

        // Dedicated thread pool keeps prewarmer off the shared .NET ThreadPool,
        // preventing CPU contention with block processing, RPC, and post-tx work.
        _dedicatedScheduler = new PrewarmerTaskScheduler(_concurrencyLevel);
        _dedicatedFactory = new TaskFactory(_dedicatedScheduler);
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
                TxProgress.Reset();
                BlockState blockState = new(this, suggestedBlock, parent, spec);
                ParallelOptions parallelOptions = new() { MaxDegreeOfParallelism = _concurrencyLevel, CancellationToken = cancellationToken, TaskScheduler = _dedicatedScheduler };

                // BAL makes speculative tx execution redundant — when BAL-based read warming
                // is in use, drive warmup directly off the suggested block's access list.
                BlockAccessList? bal = IsBalReadWarmingEnabled(spec) ? suggestedBlock.BlockAccessList : null;

                // Run address warmer on the dedicated pool (not the shared .NET ThreadPool)
                AddressWarmer addressWarmer = new(parallelOptions, suggestedBlock, parent, spec, systemAccessLists, this, bal);
                _dedicatedFactory.StartNew(() => ((IThreadPoolWorkItem)addressWarmer).Execute());
                // Do not pass the cancellation token to the task — we don't want TaskCanceledException
                // thrown in the main processing thread. The inner method checks the token itself.
                return _dedicatedFactory.StartNew(() => PreWarmCachesParallel(blockState, suggestedBlock, parent, spec, parallelOptions, addressWarmer, cancellationToken));
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

    public void NotifyTxExecuted(int txIndex) => TxProgress.MarkExecuted(txIndex);

    public void Dispose()
    {
        (_envPool as IDisposable)?.Dispose();
        _dedicatedScheduler.Dispose();
    }

    private void PreWarmCachesParallel(BlockState blockState, Block suggestedBlock, BlockHeader parent, IReleaseSpec spec, ParallelOptions parallelOptions, AddressWarmer addressWarmer, CancellationToken cancellationToken)
    {
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

            // Group transactions by sender to process same-sender transactions sequentially
            // This ensures state changes (balance, storage) from tx[N] are visible to tx[N+1]
            Dictionary<AddressAsKey, ArrayPoolList<(int Index, Transaction Tx)>>? senderGroups = GroupTransactionsBySender(block);

            try
            {
                // Sort sender groups by their earliest tx index so the prewarmer processes
                // transactions roughly in block order — staying ahead of the main thread.
                using ArrayPoolList<ArrayPoolList<(int Index, Transaction Tx)>> groupArray = senderGroups.Values.ToPooledList();
                groupArray.Sort(static (a, b) => a[0].Index.CompareTo(b[0].Index));

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
                                // Skip transactions the main processor has already executed (Reth-style)
                                if (blockState.PreWarmer.TxProgress.IsAlreadyExecuted(txIndex)) continue;
                                WarmupSingleTransaction(scope, tx, txIndex, blockState);
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

    private static Dictionary<AddressAsKey, ArrayPoolList<(int Index, Transaction Tx)>> GroupTransactionsBySender(Block block)
    {
        Dictionary<AddressAsKey, ArrayPoolList<(int, Transaction)>> groups = new();

        for (int i = 0; i < block.Transactions.Length; i++)
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

    private static void WarmupSingleTransaction(
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

    private record BlockState(BlockCachePreWarmer PreWarmer, Block Block, BlockHeader Parent, IReleaseSpec Spec);
}
