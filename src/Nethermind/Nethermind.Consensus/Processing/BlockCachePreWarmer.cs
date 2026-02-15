// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
using Nethermind.Int256;
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
    // Minimum estimated block gas below which prewarming is skipped.
    // Benchmark data shows the prewarmer overhead (thread scheduling, scope creation,
    // speculative execution) exceeds cache benefit for blocks under ~1M gas.
    // On mainnet, blocks typically have 15-30M gas so this rarely triggers.
    private const long MinPrewarmBlockGas = 1_000_000;

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
            nodeStorageCache.Enabled = true;

            bool hasSystemAccessLists = systemAccessLists.Length > 0;
            bool hasWithdrawals = spec.WithdrawalsEnabled && suggestedBlock.Withdrawals is not null && suggestedBlock.Withdrawals.Length > 0;
            bool hasEnoughTransactionWork = suggestedBlock.Transactions.Length > 0 && EstimateBlockGas(suggestedBlock) >= MinPrewarmBlockGas;
            bool hasAnyWarmupWork = hasEnoughTransactionWork || hasSystemAccessLists || hasWithdrawals;

            if (parent is not null && _concurrencyLevel > 1 && hasAnyWarmupWork && !cancellationToken.IsCancellationRequested)
            {
                BlockState blockState = new(this, suggestedBlock, parent, spec);
                ParallelOptions parallelOptions = new() { MaxDegreeOfParallelism = _concurrencyLevel, CancellationToken = cancellationToken };

                AddressWarmer? addressWarmer = null;
                if (hasEnoughTransactionWork || hasSystemAccessLists)
                {
                    // Run address warmer ahead of transactions warmer, but queue to ThreadPool so it doesn't block the txs
                    addressWarmer = new AddressWarmer(parallelOptions, suggestedBlock, parent, spec, systemAccessLists, this);
                    ThreadPool.UnsafeQueueUserWorkItem(addressWarmer, preferLocal: false);
                }

                // Do not pass the cancellation token to the task, we don't want exceptions to be thrown in the main processing thread
                return Task.Run(() => PreWarmCachesParallel(blockState, suggestedBlock, parent, spec, parallelOptions, addressWarmer, cancellationToken));
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

    private void PreWarmCachesParallel(BlockState blockState, Block suggestedBlock, BlockHeader parent, IReleaseSpec spec, ParallelOptions parallelOptions, AddressWarmer? addressWarmer, CancellationToken cancellationToken)
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
            if (_logger.IsDebug) _logger.Warn($"DEBUG/ERROR Error pre-warming {suggestedBlock.Number}. {ex}");
        }
        finally
        {
            // Don't compete the task until address warmer is also done.
            addressWarmer?.Wait();
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

            Transaction[] transactions = block.Transactions;

            // Keep same-sender txs on one lane to preserve nonce progression, but avoid
            // per-sender dictionary/list allocations and per-group scope churn.
            int laneCount = Math.Min(
                transactions.Length,
                Math.Max(1, parallelOptions.MaxDegreeOfParallelism << 2));

            SenderLanePartition senderLanePartition = CreateSenderLanePartition(transactions, laneCount);
            try
            {
                TransactionWarmingState baseState = new(
                    _envPool,
                    blockState,
                    transactions,
                    senderLanePartition.LaneStarts,
                    senderLanePartition.TxIndices,
                    parallelOptions.CancellationToken);

                ParallelUnbalancedWork.For(
                    0,
                    laneCount,
                    parallelOptions,
                    baseState.InitThreadState,
                    static (laneIndex, state) =>
                    {
                        state.WarmupLane(laneIndex);
                        return state;
                    },
                    TransactionWarmingState.FinallyAction);
            }
            finally
            {
                senderLanePartition.Dispose();
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

    private static SenderLanePartition CreateSenderLanePartition(Transaction[] transactions, int laneCount)
    {
        int[] laneStarts = ArrayPool<int>.Shared.Rent(laneCount + 1);
        int[] txIndices = ArrayPool<int>.Shared.Rent(transactions.Length);
        int[] laneWriteOffsets = ArrayPool<int>.Shared.Rent(laneCount);
        int[] txLanes = ArrayPool<int>.Shared.Rent(transactions.Length);

        try
        {
            Array.Clear(laneWriteOffsets, 0, laneCount);

            for (int i = 0; i < transactions.Length; i++)
            {
                Address sender = transactions[i].SenderAddress!;
                int laneIndex = GetSenderLane(sender, laneCount);
                txLanes[i] = laneIndex;
                laneWriteOffsets[laneIndex]++;
            }

            int nextOffset = 0;
            for (int laneIndex = 0; laneIndex < laneCount; laneIndex++)
            {
                laneStarts[laneIndex] = nextOffset;
                int laneSize = laneWriteOffsets[laneIndex];
                laneWriteOffsets[laneIndex] = nextOffset;
                nextOffset += laneSize;
            }

            laneStarts[laneCount] = nextOffset;

            for (int i = 0; i < transactions.Length; i++)
            {
                int laneIndex = txLanes[i];
                txIndices[laneWriteOffsets[laneIndex]++] = i;
            }

            return new SenderLanePartition(laneStarts, txIndices);
        }
        catch
        {
            ArrayPool<int>.Shared.Return(laneStarts, clearArray: false);
            ArrayPool<int>.Shared.Return(txIndices, clearArray: false);
            throw;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(laneWriteOffsets, clearArray: false);
            ArrayPool<int>.Shared.Return(txLanes, clearArray: false);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetSenderLane(Address sender, int laneCount)
    {
        return (int)((uint)sender.GetHashCode() % (uint)laneCount);
    }

    private static long EstimateBlockGas(Block block)
    {
        long totalGas = 0;
        Transaction[] txs = block.Transactions;
        for (int i = 0; i < txs.Length; i++)
        {
            totalGas += txs[i].GasLimit;
        }

        return totalGas;
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
            if (blockState.PreWarmer._logger.IsDebug) blockState.PreWarmer._logger.Error($"DEBUG/ERROR Error pre-warming cache {tx.Hash}", ex);
        }
    }

    private readonly struct SenderLanePartition(int[] laneStarts, int[] txIndices) : IDisposable
    {
        public int[] LaneStarts { get; } = laneStarts;
        public int[] TxIndices { get; } = txIndices;

        public void Dispose()
        {
            ArrayPool<int>.Shared.Return(LaneStarts, clearArray: false);
            ArrayPool<int>.Shared.Return(TxIndices, clearArray: false);
        }
    }

    private readonly struct TransactionWarmingState(
        ObjectPool<IReadOnlyTxProcessorSource> envPool,
        BlockState blockState,
        Transaction[] transactions,
        int[] laneStarts,
        int[] txIndices,
        CancellationToken cancellationToken) : IDisposable
    {
        public static Action<TransactionWarmingState> FinallyAction { get; } = DisposeThreadState;

        private readonly ObjectPool<IReadOnlyTxProcessorSource> EnvPool = envPool;
        private readonly BlockState BlockState = blockState;
        private readonly Transaction[] Transactions = transactions;
        private readonly int[] LaneStarts = laneStarts;
        private readonly int[] TxIndices = txIndices;
        private readonly CancellationToken CancellationToken = cancellationToken;
        private readonly IReadOnlyTxProcessorSource? Env;
        private readonly IReadOnlyTxProcessingScope? Scope;

        private TransactionWarmingState(
            ObjectPool<IReadOnlyTxProcessorSource> envPool,
            BlockState blockState,
            Transaction[] transactions,
            int[] laneStarts,
            int[] txIndices,
            CancellationToken cancellationToken,
            IReadOnlyTxProcessorSource env,
            IReadOnlyTxProcessingScope scope) : this(envPool, blockState, transactions, laneStarts, txIndices, cancellationToken)
        {
            Env = env;
            Scope = scope;
        }

        public TransactionWarmingState InitThreadState()
        {
            IReadOnlyTxProcessorSource env = EnvPool.Get();
            IReadOnlyTxProcessingScope scope = env.Build(BlockState.Parent);
            scope.TransactionProcessor.SetBlockExecutionContext(new BlockExecutionContext(BlockState.Block.Header, BlockState.Spec));
            return new(EnvPool, BlockState, Transactions, LaneStarts, TxIndices, CancellationToken, env, scope);
        }

        public void WarmupLane(int laneIndex)
        {
            int start = LaneStarts[laneIndex];
            int end = LaneStarts[laneIndex + 1];
            for (int i = start; i < end; i++)
            {
                if (CancellationToken.IsCancellationRequested)
                {
                    return;
                }

                int txIndex = TxIndices[i];
                WarmupSingleTransaction(Scope!, Transactions[txIndex], txIndex, BlockState);
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

        private static void DisposeThreadState(TransactionWarmingState state) => state.Dispose();
    }

    private class AddressWarmer(ParallelOptions parallelOptions, Block block, BlockHeader parent, IReleaseSpec spec, ReadOnlySpan<IHasAccessList> systemAccessLists, BlockCachePreWarmer preWarmer)
        : IThreadPoolWorkItem
    {
        private readonly Block Block = block;
        private readonly BlockCachePreWarmer PreWarmer = preWarmer;
        private readonly ArrayPoolList<AccessList>? SystemTxAccessLists = GetAccessLists(block, spec, systemAccessLists);
        private readonly ManualResetEventSlim _doneEvent = new(initialState: false);

        public void Wait() => _doneEvent.Wait();

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
                if (PreWarmer._logger.IsDebug) PreWarmer._logger.Error($"DEBUG/ERROR Error pre-warming addresses", ex);
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

                AddressWarmingState baseState = new(envPool, block, parent);

                ParallelUnbalancedWork.For(
                    0,
                    block.Transactions.Length,
                    parallelOptions,
                    baseState.InitThreadState,
                static (i, state) =>
                {
                    Transaction tx = state.Block.Transactions[i];
                    Address? sender = tx.SenderAddress;

                    try
                    {
                        if (sender is not null)
                        {
                            state.Scope!.WorldState.WarmUp(sender);
                        }

                        Address to = tx.To;
                        if (to is not null)
                        {
                            state.Scope!.WorldState.WarmUp(to);
                        }
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
    }

    private readonly struct AddressWarmingState(ObjectPool<IReadOnlyTxProcessorSource> envPool, Block block, BlockHeader parent) : IDisposable
    {
        public static Action<AddressWarmingState> FinallyAction { get; } = DisposeThreadState;

        private readonly ObjectPool<IReadOnlyTxProcessorSource> EnvPool = envPool;
        private readonly IReadOnlyTxProcessorSource? Env;
        public readonly Block Block = block;
        public readonly IReadOnlyTxProcessingScope? Scope;

        private AddressWarmingState(ObjectPool<IReadOnlyTxProcessorSource> envPool, Block block, BlockHeader parent, IReadOnlyTxProcessorSource env, IReadOnlyTxProcessingScope scope) : this(envPool, block, parent)
        {
            Env = env;
            Scope = scope;
        }

        public AddressWarmingState InitThreadState()
        {
            IReadOnlyTxProcessorSource env = EnvPool.Get();
            return new(EnvPool, Block, parent, env, scope: env.Build(parent));
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

    private class ReadOnlyTxProcessingEnvPooledObjectPolicy(PrewarmerEnvFactory envFactory, PreBlockCaches preBlockCaches) : IPooledObjectPolicy<IReadOnlyTxProcessorSource>
    {
        public IReadOnlyTxProcessorSource Create() => envFactory.Create(preBlockCaches);
        public bool Return(IReadOnlyTxProcessorSource obj) => true;
    }

    private record BlockState(BlockCachePreWarmer PreWarmer, Block Block, BlockHeader Parent, IReleaseSpec Spec);
}
