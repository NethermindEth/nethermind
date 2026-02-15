// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Threading;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Evm.State;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.Consensus.Processing;

public sealed class BlockCachePreWarmer(
    PrewarmerEnvFactory envFactory,
    int concurrency,
    NodeStorageCache nodeStorageCache,
    PreBlockCaches preBlockCaches,
    ILogManager logManager
) : IBlockCachePreWarmer
{
    private readonly int _concurrencyLevel = concurrency == 0 ? Math.Min(Environment.ProcessorCount - 1, 16) : concurrency;
    private readonly ObjectPool<IReadOnlyTxProcessorSource> _envPool = new DefaultObjectPool<IReadOnlyTxProcessorSource>(new ReadOnlyTxProcessingEnvPooledObjectPolicy(envFactory, preBlockCaches), Environment.ProcessorCount * 2);
    private readonly ILogger _logger = logManager.GetClassLogger<BlockCachePreWarmer>();

    public BlockCachePreWarmer(
        PrewarmerEnvFactory envFactory,
        IBlocksConfig blocksConfig,
        NodeStorageCache nodeStorageCache,
        PreBlockCaches preBlockCaches,
        ILogManager logManager
    ) : this(
        envFactory,
        blocksConfig.PreWarmStateConcurrency,
        nodeStorageCache,
        preBlockCaches,
        logManager)
    {
    }

    public Task PreWarmCaches(Block suggestedBlock, BlockHeader? parent, IReleaseSpec spec, CancellationToken cancellationToken = default, params ReadOnlySpan<IHasAccessList> systemAccessLists)
    {
        if (preBlockCaches is not null)
        {
            // Fork detection: if parent doesn't match the last processed block, the cache is stale.
            // This handles reorgs, first block after startup, and error recovery.
            Hash256? lastHash = preBlockCaches.LastProcessedBlockHash;
            if (lastHash is null || parent?.Hash != lastHash)
            {
                if (_logger.IsDebug) _logger.Debug($"Cross-block cache miss: parent {parent?.Hash?.ToShortString()} != last {lastHash?.ToShortString()}, clearing warm caches.");
                preBlockCaches.ClearAllCaches();
            }

            // Clear per-block caches (precompile); state/storage kept warm across blocks
            preBlockCaches.ClearCaches();

            // NodeStorageCache uses content-addressed keys (Address, Path, Hash),
            // so entries from previous blocks are never stale - keep it warm across blocks.
            if (!nodeStorageCache.Enabled)
            {
                nodeStorageCache.Enabled = true;
            }

            bool hasTransactions = suggestedBlock.Transactions.Length > 0;
            bool hasSystemAccessLists = systemAccessLists.Length > 0;
            bool hasWithdrawals = spec.WithdrawalsEnabled && suggestedBlock.Withdrawals is not null && suggestedBlock.Withdrawals.Length > 0;
            bool hasAnyWarmupWork = hasTransactions || hasSystemAccessLists || hasWithdrawals;

            if (parent is not null && _concurrencyLevel > 1 && hasAnyWarmupWork && !cancellationToken.IsCancellationRequested)
            {
                BlockState blockState = new(this, suggestedBlock, parent, spec);
                ParallelOptions parallelOptions = new() { MaxDegreeOfParallelism = _concurrencyLevel, CancellationToken = cancellationToken };
                IHasAccessList[]? systemAccessListArray = hasSystemAccessLists ? [..systemAccessLists] : null;

                // Do not pass the cancellation token to the task, we don't want exceptions to be thrown in the main processing thread
                return Task.Run(() => PreWarmCachesParallel(blockState, suggestedBlock, spec, parallelOptions, systemAccessListArray, cancellationToken));
            }
        }

        return Task.CompletedTask;
    }

    public CacheType ClearCaches()
    {
        if (_logger.IsDebug) _logger.Debug("Clearing per-block caches");
        CacheType cachesCleared = preBlockCaches?.ClearCaches() ?? default;
        if (_logger.IsDebug) _logger.Debug($"Cleared caches: {cachesCleared}");
        return cachesCleared;
    }

    public void NotifyBlockProcessed(Hash256? blockHash)
    {
        if (preBlockCaches is not null)
        {
            preBlockCaches.LastProcessedBlockHash = blockHash;
        }
    }

    private void PreWarmCachesParallel(BlockState blockState, Block suggestedBlock, IReleaseSpec spec, ParallelOptions parallelOptions, IHasAccessList[]? systemAccessLists, CancellationToken cancellationToken)
    {
        try
        {
            if (cancellationToken.IsCancellationRequested) return;

            if (_logger.IsDebug) _logger.Debug($"Started pre-warming caches for block {suggestedBlock.Number}.");

            WarmupSystemAccessLists(systemAccessLists, suggestedBlock, blockState.Parent, spec, cancellationToken);
            WarmupTransactions(blockState, parallelOptions);
            WarmupWithdrawals(parallelOptions, spec, suggestedBlock, blockState.Parent);

            if (_logger.IsDebug) _logger.Debug($"Finished pre-warming caches for block {suggestedBlock.Number}.");
        }
        catch (Exception ex)
        {
            if (_logger.IsDebug) _logger.Warn($"DEBUG/ERROR Error pre-warming {suggestedBlock.Number}. {ex}");
        }
    }

    private void WarmupSystemAccessLists(IHasAccessList[]? systemAccessLists, Block block, BlockHeader parent, IReleaseSpec spec, CancellationToken cancellationToken)
    {
        if (systemAccessLists is null || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        ObjectPool<IReadOnlyTxProcessorSource> envPool = _envPool;
        IReadOnlyTxProcessorSource env = envPool.Get();
        try
        {
            using IReadOnlyTxProcessingScope scope = env.Build(parent);
            for (int i = 0; i < systemAccessLists.Length; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                AccessList list = systemAccessLists[i].GetAccessList(block, spec);
                scope.WorldState.WarmUp(list);
            }
        }
        catch (OperationCanceledException)
        {
            // Ignore, block completed cancel
        }
        catch (Exception ex)
        {
            if (_logger.IsDebug) _logger.Error("DEBUG/ERROR Error pre-warming system access lists", ex);
        }
        finally
        {
            envPool.Return(env);
        }
    }

    private void WarmupWithdrawals(ParallelOptions parallelOptions, IReleaseSpec spec, Block block, BlockHeader? parent)
    {
        if (parallelOptions.CancellationToken.IsCancellationRequested) return;

        try
        {
            if (spec.WithdrawalsEnabled && block.Withdrawals is not null)
            {
                WithdrawalWarmingState baseState = new(_envPool, block, parent);

                ParallelUnbalancedWork.For(
                    0,
                    block.Withdrawals.Length,
                    parallelOptions,
                    baseState.InitThreadState,
                    static (i, state) =>
                    {
                        try
                        {
                            state.Scope!.WorldState.WarmUp(state.Block.Withdrawals![i].Address);
                        }
                        catch (MissingTrieNodeException)
                        {
                        }

                        return state;
                    },
                    WithdrawalWarmingState.FinallyAction);
            }
        }
        catch (OperationCanceledException)
        {
            // Ignore, block completed cancel
        }
        catch (Exception ex)
        {
            if (_logger.IsDebug) _logger.Error("DEBUG/ERROR Error pre-warming withdrawal", ex);
        }
    }

    private readonly struct WithdrawalWarmingState(ObjectPool<IReadOnlyTxProcessorSource> envPool, Block block, BlockHeader? parent) : IDisposable
    {
        public static Action<WithdrawalWarmingState> FinallyAction { get; } = DisposeThreadState;

        private readonly ObjectPool<IReadOnlyTxProcessorSource> EnvPool = envPool;
        private readonly BlockHeader? Parent = parent;
        private readonly IReadOnlyTxProcessorSource? Env;
        public readonly Block Block = block;
        public readonly IReadOnlyTxProcessingScope? Scope;

        private WithdrawalWarmingState(
            ObjectPool<IReadOnlyTxProcessorSource> envPool,
            Block block,
            BlockHeader? parent,
            IReadOnlyTxProcessorSource env,
            IReadOnlyTxProcessingScope scope) : this(envPool, block, parent)
        {
            Env = env;
            Scope = scope;
        }

        public WithdrawalWarmingState InitThreadState()
        {
            IReadOnlyTxProcessorSource env = EnvPool.Get();
            return new(EnvPool, Block, Parent, env, env.Build(Parent));
        }

        public void Dispose()
        {
            Scope?.Dispose();
            if (Env is not null)
            {
                EnvPool.Return(Env);
            }
        }

        private static void DisposeThreadState(WithdrawalWarmingState state) => state.Dispose();
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
            using ArrayPoolList<ArrayPoolList<int>> groupedTransactionIndices = GroupTransactionIndicesBySender(block.Transactions);

            try
            {
                TransactionGroupWarmingState baseState = new(_envPool, blockState, groupedTransactionIndices, parallelOptions.CancellationToken);

                // Parallel across different senders, sequential within the same sender
                ParallelUnbalancedWork.For(
                    0,
                    groupedTransactionIndices.Count,
                    parallelOptions,
                    baseState.InitThreadState,
                    static (groupIndex, state) =>
                    {
                        if (state.CancellationToken.IsCancellationRequested)
                        {
                            return state;
                        }

                        // Warmup mutates scope-local state; restore parent baseline
                        // before each sender-group in a reused per-thread scope.
                        state.Scope!.WorldState.Restore(state.BaseSnapshot);

                        ArrayPoolList<int> txIndices = state.GroupedTransactionIndices[groupIndex];
                        Transaction[] transactions = state.BlockState.Block.Transactions;
                        foreach (int txIndex in txIndices.AsSpan())
                        {
                            if (state.CancellationToken.IsCancellationRequested)
                            {
                                return state;
                            }

                            WarmupSingleTransaction(state.Scope!, transactions[txIndex], txIndex, state.BlockState);
                        }

                        return state;
                    },
                    TransactionGroupWarmingState.FinallyAction);
            }
            finally
            {
                foreach (ArrayPoolList<int> txIndices in groupedTransactionIndices.AsSpan())
                {
                    txIndices.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ignore, block completed cancel
        }
        catch (Exception ex)
        {
            if (_logger.IsDebug) _logger.Error($"DEBUG/ERROR Error pre-warming transactions", ex);
        }
    }

    private static ArrayPoolList<ArrayPoolList<int>> GroupTransactionIndicesBySender(Transaction[] transactions)
    {
        Dictionary<AddressAsKey, int> groupIndexes = new(transactions.Length);
        ArrayPoolList<ArrayPoolList<int>> groups = new(transactions.Length);

        for (int i = 0; i < transactions.Length; i++)
        {
            Address sender = transactions[i].SenderAddress!;

            ref int groupIndex = ref CollectionsMarshal.GetValueRefOrAddDefault(groupIndexes, sender, out bool exists);
            if (!exists)
            {
                groupIndex = groups.Count;
                groups.Add(new ArrayPoolList<int>(4));
            }

            groups[groupIndex].Add(i);
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
            IWorldState worldState = scope.WorldState;

            if (blockState.Spec.UseTxAccessLists)
            {
                worldState.WarmUp(tx.AccessList); // eip-2930
            }

            TransactionResult result = scope.TransactionProcessor.Warmup(tx, NullTxTracer.Instance);

            if (blockState.PreWarmer._logger.IsTrace) blockState.PreWarmer._logger.Trace($"Finished pre-warming cache for tx[{txIndex}] {tx.Hash} with {result}");
        }
        catch (MissingTrieNodeException)
        {
        }
        catch (Exception ex) when (ex is EvmException or OverflowException)
        {
            // Ignore, regular tx processing exceptions
        }
        catch (Exception ex)
        {
            if (blockState.PreWarmer._logger.IsDebug) blockState.PreWarmer._logger.Error($"DEBUG/ERROR Error pre-warming cache {tx.Hash}", ex);
        }
    }

    private readonly struct TransactionGroupWarmingState(
        ObjectPool<IReadOnlyTxProcessorSource> envPool,
        BlockState blockState,
        ArrayPoolList<ArrayPoolList<int>> groupedTransactionIndices,
        CancellationToken cancellationToken) : IDisposable
    {
        public static Action<TransactionGroupWarmingState> FinallyAction { get; } = DisposeThreadState;

        private readonly ObjectPool<IReadOnlyTxProcessorSource> EnvPool = envPool;
        private readonly IReadOnlyTxProcessorSource? Env;
        public readonly BlockState BlockState = blockState;
        public readonly ArrayPoolList<ArrayPoolList<int>> GroupedTransactionIndices = groupedTransactionIndices;
        public readonly CancellationToken CancellationToken = cancellationToken;
        public readonly IReadOnlyTxProcessingScope? Scope;
        public readonly Snapshot BaseSnapshot;

        private TransactionGroupWarmingState(
            ObjectPool<IReadOnlyTxProcessorSource> envPool,
            BlockState blockState,
            ArrayPoolList<ArrayPoolList<int>> groupedTransactionIndices,
            CancellationToken cancellationToken,
            IReadOnlyTxProcessorSource env,
            IReadOnlyTxProcessingScope scope,
            Snapshot baseSnapshot) : this(envPool, blockState, groupedTransactionIndices, cancellationToken)
        {
            Env = env;
            Scope = scope;
            BaseSnapshot = baseSnapshot;
        }

        public TransactionGroupWarmingState InitThreadState()
        {
            IReadOnlyTxProcessorSource env = EnvPool.Get();
            IReadOnlyTxProcessingScope scope = env.Build(BlockState.Parent);
            scope.TransactionProcessor.SetBlockExecutionContext(new BlockExecutionContext(BlockState.Block.Header, BlockState.Spec));
            Snapshot baseSnapshot = scope.WorldState.TakeSnapshot();
            return new(EnvPool, BlockState, GroupedTransactionIndices, CancellationToken, env, scope, baseSnapshot);
        }

        public void Dispose()
        {
            Scope?.Dispose();
            if (Env is not null)
            {
                EnvPool.Return(Env);
            }
        }

        private static void DisposeThreadState(TransactionGroupWarmingState state) => state.Dispose();
    }

    private class ReadOnlyTxProcessingEnvPooledObjectPolicy(PrewarmerEnvFactory envFactory, PreBlockCaches preBlockCaches) : IPooledObjectPolicy<IReadOnlyTxProcessorSource>
    {
        public IReadOnlyTxProcessorSource Create() => envFactory.Create(preBlockCaches);
        public bool Return(IReadOnlyTxProcessorSource obj) => true;
    }

    private record BlockState(BlockCachePreWarmer PreWarmer, Block Block, BlockHeader Parent, IReleaseSpec Spec);
}
