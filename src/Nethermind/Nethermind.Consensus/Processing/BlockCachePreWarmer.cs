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
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Trie;

namespace Nethermind.Consensus.Processing;

public sealed class BlockCachePreWarmer : IBlockCachePreWarmer
{
    private readonly int _concurrencyLevel;
    private readonly ObjectPool<IReadOnlyTxProcessorSource> _envPool;
    private readonly ILogger _logger;
    private readonly PreBlockCaches _preBlockCaches;
    private readonly NodeStorageCache _nodeStorageCache;

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
        nodeStorageCache,
        preBlockCaches,
        logManager)
    {
    }

    internal BlockCachePreWarmer(
        IPooledObjectPolicy<IReadOnlyTxProcessorSource> poolPolicy,
        int maxPoolSize,
        int concurrency,
        NodeStorageCache nodeStorageCache,
        PreBlockCaches preBlockCaches,
        ILogManager logManager)
    {
        _concurrencyLevel = concurrency == 0 ? Math.Min(Environment.ProcessorCount - 1, 16) : concurrency;
        _envPool = new DefaultObjectPoolProvider { MaximumRetained = maxPoolSize }.Create(poolPolicy);
        _logger = logManager.GetClassLogger<BlockCachePreWarmer>();
        _preBlockCaches = preBlockCaches;
        _nodeStorageCache = nodeStorageCache;
    }

    public Task PreWarmCaches(Block suggestedBlock, BlockHeader? parent, IReleaseSpec spec, CancellationToken cancellationToken = default, params ReadOnlySpan<IHasAccessList> systemAccessLists)
    {
        if (_preBlockCaches is not null)
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
                ParallelOptions parallelOptions = new() { MaxDegreeOfParallelism = _concurrencyLevel, CancellationToken = cancellationToken };

                // Run address warmer ahead of transactions warmer, but queue to ThreadPool so it doesn't block the txs
                AddressWarmer addressWarmer = new(parallelOptions, suggestedBlock, parent, spec, systemAccessLists, this);
                ThreadPool.UnsafeQueueUserWorkItem(addressWarmer, preferLocal: false);
                // Do not pass the cancellation token to the task, we don't want exceptions to be thrown in the main processing thread
                return Task.Run(() => PreWarmCachesParallel(blockState, suggestedBlock, parent, spec, parallelOptions, addressWarmer, cancellationToken));
            }
        }

        return Task.CompletedTask;
    }

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
        try
        {
            if (cancellationToken.IsCancellationRequested) return;

            if (_logger.IsDebug) _logger.Debug($"Started pre-warming caches for block {suggestedBlock.Number}.");

            WarmupTransactions(blockState, parallelOptions);
            WarmupWithdrawals(parallelOptions, spec, suggestedBlock, parent);

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
            Transaction[] transactions = block.Transactions;
            if (transactions.Length == 0) return;

            BlockExecutionContext context = new(block.Header, blockState.Spec);
            if (!HasDuplicateSenders(transactions))
            {
                WarmupIndependentTransactions(blockState, context, parallelOptions);
                return;
            }

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
                    (blockState, groupArray, parallelOptions.CancellationToken, context),
                    static (groupIndex, tupleState) =>
                    {
                        (BlockState? blockState, ArrayPoolList<ArrayPoolList<(int Index, Transaction Tx)>> groups, CancellationToken token, BlockExecutionContext context) = tupleState;
                        ArrayPoolList<(int Index, Transaction Tx)>? txList = groups[groupIndex];

                        // Get thread-local processing state for this sender's transactions
                        IReadOnlyTxProcessorSource env = blockState.PreWarmer._envPool.Get();
                        try
                        {
                            using IReadOnlyTxProcessingScope scope = env.Build(blockState.Parent);
                            scope.TransactionProcessor.SetBlockExecutionContext(context);

                            // Sequential within the same sender-state changes propagate correctly
                            foreach ((int txIndex, Transaction? tx) in txList.AsSpan())
                            {
                                if (token.IsCancellationRequested) return tupleState;
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

    private void WarmupIndependentTransactions(BlockState blockState, BlockExecutionContext context, ParallelOptions parallelOptions)
    {
        IndependentTxWarmingState baseState = new(_envPool, blockState, context);

        ParallelUnbalancedWork.For(
            0,
            blockState.Block.Transactions.Length,
            parallelOptions,
            baseState.InitThreadState,
            static (txIndex, state) =>
            {
                try
                {
                    WarmupSingleTransaction(state.Scope!, state.Block.Transactions[txIndex], txIndex, state.BlockState);
                }
                finally
                {
                    state.Scope!.WorldState.Reset();
                }

                return state;
            },
            IndependentTxWarmingState.FinallyAction);
    }

    private static bool HasDuplicateSenders(Transaction[] transactions)
    {
        HashSet<AddressAsKey> senders = new(transactions.Length);

        for (int i = 0; i < transactions.Length; i++)
        {
            if (!senders.Add(transactions[i].SenderAddress!))
            {
                return true;
            }
        }

        return false;
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

    private readonly struct IndependentTxWarmingState(
        ObjectPool<IReadOnlyTxProcessorSource> envPool,
        BlockState blockState,
        BlockExecutionContext context) : IDisposable
    {
        public static Action<IndependentTxWarmingState> FinallyAction { get; } = DisposeThreadState;

        private readonly ObjectPool<IReadOnlyTxProcessorSource> EnvPool = envPool;
        private readonly BlockExecutionContext Context = context;
        private readonly IReadOnlyTxProcessorSource? Env;
        public readonly BlockState BlockState = blockState;
        public readonly Block Block = blockState.Block;
        public readonly IReadOnlyTxProcessingScope? Scope;

        private IndependentTxWarmingState(
            ObjectPool<IReadOnlyTxProcessorSource> envPool,
            BlockState blockState,
            BlockExecutionContext context,
            IReadOnlyTxProcessorSource env,
            IReadOnlyTxProcessingScope scope) : this(envPool, blockState, context)
        {
            Env = env;
            Scope = scope;
        }

        public IndependentTxWarmingState InitThreadState()
        {
            IReadOnlyTxProcessorSource env = EnvPool.Get();
            IReadOnlyTxProcessingScope scope = env.Build(BlockState.Parent);
            scope.TransactionProcessor.SetBlockExecutionContext(Context);

            return new(EnvPool, BlockState, Context, env, scope);
        }

        public void Dispose()
        {
            Scope?.Dispose();
            if (Env is not null)
            {
                EnvPool.Return(Env);
            }
        }

        private static void DisposeThreadState(IndependentTxWarmingState state) => state.Dispose();
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

    private class AddressWarmer(ParallelOptions parallelOptions, Block block, BlockHeader parent, IReleaseSpec spec, ReadOnlySpan<IHasAccessList> systemAccessLists, BlockCachePreWarmer preWarmer)
        : IThreadPoolWorkItem, IDisposable
    {
        private readonly Block Block = block;
        private readonly BlockCachePreWarmer PreWarmer = preWarmer;
        private readonly ArrayPoolList<AccessList>? SystemTxAccessLists = GetAccessLists(block, spec, systemAccessLists);
        private readonly ManualResetEventSlim _doneEvent = new(initialState: false);

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

                using ArrayPoolList<AddressAsKey> addresses = CollectWarmupAddresses(block);
                if (addresses.Count == 0) return;

                AddressWarmingState baseState = new(envPool, addresses, parent);

                ParallelUnbalancedWork.For(
                    0,
                    addresses.Count,
                    parallelOptions,
                    baseState.InitThreadState,
                static (i, state) =>
                {
                    try
                    {
                        state.Scope!.WorldState.WarmUp(state.Addresses.GetRef(i));
                    }
                    catch (MissingTrieNodeException)
                    {
                    }

                    return state;
                },
                AddressWarmingState.FinallyAction);
            }
            catch (OperationCanceledException)
            {
                // Ignore, block completed cancel
            }
        }

        private static ArrayPoolList<AddressAsKey> CollectWarmupAddresses(Block block)
        {
            Transaction[] transactions = block.Transactions;
            ArrayPoolList<AddressAsKey> addresses = new(transactions.Length);
            HashSet<AddressAsKey> seen = new(transactions.Length);

            for (int i = 0; i < transactions.Length; i++)
            {
                Address? to = transactions[i].To;
                if (to is not null && seen.Add(to))
                {
                    addresses.Add(to);
                }
            }

            return addresses;
        }
    }

    private readonly struct AddressWarmingState(ObjectPool<IReadOnlyTxProcessorSource> envPool, ArrayPoolList<AddressAsKey> addresses, BlockHeader parent) : IDisposable
    {
        public static Action<AddressWarmingState> FinallyAction { get; } = DisposeThreadState;

        private readonly ObjectPool<IReadOnlyTxProcessorSource> EnvPool = envPool;
        private readonly IReadOnlyTxProcessorSource? Env;
        public readonly ArrayPoolList<AddressAsKey> Addresses = addresses;
        public readonly IReadOnlyTxProcessingScope? Scope;

        private AddressWarmingState(ObjectPool<IReadOnlyTxProcessorSource> envPool, ArrayPoolList<AddressAsKey> addresses, BlockHeader parent, IReadOnlyTxProcessorSource env, IReadOnlyTxProcessingScope scope) : this(envPool, addresses, parent)
        {
            Env = env;
            Scope = scope;
        }

        public AddressWarmingState InitThreadState()
        {
            IReadOnlyTxProcessorSource env = EnvPool.Get();
            return new(EnvPool, Addresses, parent, env, scope: env.Build(parent));
        }

        public void Dispose()
        {
            Scope?.Dispose();
            if (Env is not null)
            {
                EnvPool.Return(Env);
            }
        }

        private static void DisposeThreadState(AddressWarmingState state) => state.Dispose();
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
