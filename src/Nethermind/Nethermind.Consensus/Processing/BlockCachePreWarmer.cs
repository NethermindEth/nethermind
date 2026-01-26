// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
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
    private BlockStateSource? _currentBlockState = null;

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
            _currentBlockState = new(this, suggestedBlock, parent, spec);
            CacheType result = preBlockCaches.ClearCaches();
            result |= nodeStorageCache.ClearCaches() ? CacheType.Rlp : CacheType.None;
            nodeStorageCache.Enabled = true;
            if (result != default)
            {
                if (_logger.IsWarn) _logger.Warn($"Caches {result} are not empty. Clearing them.");
            }

            if (parent is not null && _concurrencyLevel > 1 && !cancellationToken.IsCancellationRequested)
            {
                ParallelOptions parallelOptions = new() { MaxDegreeOfParallelism = _concurrencyLevel, CancellationToken = cancellationToken };

                // Run address warmer ahead of transactions warmer, but queue to ThreadPool so it doesn't block the txs
                var addressWarmer = new AddressWarmer(parallelOptions, suggestedBlock, parent, spec, systemAccessLists, this);
                ThreadPool.UnsafeQueueUserWorkItem(addressWarmer, preferLocal: false);
                // Do not pass cancellation token to the task, we don't want exceptions to be thrown in main processing thread
                return Task.Run(() => PreWarmCachesParallel(_currentBlockState, suggestedBlock, parent, spec, parallelOptions, addressWarmer, cancellationToken));
            }
        }

        return Task.CompletedTask;
    }

    public CacheType ClearCaches()
    {
        if (_logger.IsDebug) _logger.Debug("Clearing caches");
        CacheType cachesCleared = preBlockCaches?.ClearCaches() ?? default;

        nodeStorageCache.Enabled = false;
        cachesCleared |= nodeStorageCache.ClearCaches() ? CacheType.Rlp : CacheType.None;
        if (_logger.IsDebug) _logger.Debug($"Cleared caches: {cachesCleared}");
        return cachesCleared;
    }

    private void PreWarmCachesParallel(BlockStateSource blockState, Block suggestedBlock, BlockHeader parent, IReleaseSpec spec, ParallelOptions parallelOptions, AddressWarmer addressWarmer, CancellationToken cancellationToken)
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
            addressWarmer.Wait();
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
            if (_logger.IsDebug) _logger.Error($"DEBUG/ERROR Error pre-warming withdrawal", ex);
        }
    }

    private void WarmupTransactions(BlockStateSource blockState, ParallelOptions parallelOptions)
    {
        if (parallelOptions.CancellationToken.IsCancellationRequested) return;

        try
        {
            Block block = blockState.Block;
            if (block.Transactions.Length == 0) return;

            AccessList? accessListUnion = null;
            if (blockState.Spec.UseTxAccessLists)
            {
                accessListUnion = BuildAccessListUnion(block.Transactions);
                if (accessListUnion is not null && !accessListUnion.IsEmpty)
                {
                    WarmupAccessListUnion(blockState, accessListUnion);
                }
                else
                {
                    accessListUnion = null;
                }
            }

            // Group transactions by recipient (fallback to sender for contract creation) to improve cache locality
            // and ensure sequential execution within each group.
            var transactionGroups = GroupTransactionsByRecipient(block);
            bool accessListUnionApplied = accessListUnion is not null;

            try
            {
                // Convert to array for parallel iteration
                ArrayPoolList<(int Index, Transaction Tx)>[]? groupArray = transactionGroups.Values.ToArray();

                // Parallel across different senders, sequential within the same sender
                ParallelUnbalancedWork.For(
                    0,
                    groupArray.Length,
                    parallelOptions,
                    (blockState, groupArray, accessListUnionApplied),
                    static (groupIndex, tupleState) =>
                    {
                        (BlockStateSource? blockState, ArrayPoolList<(int Index, Transaction Tx)>[]? groups, bool accessListUnionApplied) = tupleState;
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
                                WarmupSingleTransaction(scope, tx, txIndex, blockState, accessListUnionApplied);
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
                foreach (KeyValuePair<AddressAsKey, ArrayPoolList<(int Index, Transaction Tx)>> kvp in transactionGroups)
                    kvp.Value.Dispose();
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

    private static Dictionary<AddressAsKey, ArrayPoolList<(int Index, Transaction Tx)>> GroupTransactionsByRecipient(Block block)
    {
        Dictionary<AddressAsKey, ArrayPoolList<(int, Transaction)>> groups = new();

        for (int i = 0; i < block.Transactions.Length; i++)
        {
            Transaction tx = block.Transactions[i];
            Address? recipient = tx.To;
            Address groupKey = recipient ?? tx.SenderAddress!;

            if (!groups.TryGetValue(groupKey, out ArrayPoolList<(int, Transaction)> list))
            {
                list = new(4);
                groups[groupKey] = list;
            }
            list.Add((i, tx));
        }

        return groups;
    }

    private static void WarmupSingleTransaction(
        IReadOnlyTxProcessingScope scope,
        Transaction tx,
        int txIndex,
        BlockStateSource blockState,
        bool accessListUnionApplied)
    {
        try
        {
            Address senderAddress = tx.SenderAddress!;
            IWorldState worldState = scope.WorldState;

            if (!worldState.AccountExists(senderAddress))
            {
                worldState.CreateAccountIfNotExists(senderAddress, UInt256.Zero);
            }

            if (blockState.Spec.UseTxAccessLists && !accessListUnionApplied)
            {
                worldState.WarmUp(tx.AccessList); // eip-2930
            }

            TransactionResult result = scope.TransactionProcessor.Warmup(tx, NullTxTracer.Instance);

            if (blockState.PreWarmer._logger.IsTrace)
                blockState.PreWarmer._logger.Trace($"Finished pre-warming cache for tx[{txIndex}] {tx.Hash} with {result}");
        }
        catch (Exception ex) when (ex is EvmException or OverflowException)
        {
            // Ignore, regular tx processing exceptions
        }
        catch (Exception ex)
        {
            if (blockState.PreWarmer._logger.IsDebug)
                blockState.PreWarmer._logger.Error($"DEBUG/ERROR Error pre-warming cache {tx.Hash}", ex);
        }
    }

    private void WarmupAccessListUnion(BlockStateSource blockState, AccessList accessList)
    {
        IReadOnlyTxProcessorSource env = _envPool.Get();
        try
        {
            using IReadOnlyTxProcessingScope scope = env.Build(blockState.Parent);
            scope.WorldState.WarmUp(accessList);
        }
        catch (MissingTrieNodeException)
        {
        }
        catch (Exception ex)
        {
            if (_logger.IsDebug) _logger.Error("DEBUG/ERROR Error pre-warming access list union", ex);
        }
        finally
        {
            _envPool.Return(env);
        }
    }

    private static AccessList? BuildAccessListUnion(ReadOnlySpan<Transaction> transactions)
    {
        Dictionary<Address, HashSet<UInt256>>? merged = null;

        foreach (Transaction tx in transactions)
        {
            AccessList? accessList = tx.AccessList;
            if (accessList is null || accessList.IsEmpty)
            {
                continue;
            }

            merged ??= new Dictionary<Address, HashSet<UInt256>>();

            foreach ((Address address, AccessList.StorageKeysEnumerable storageKeys) in accessList)
            {
                if (!merged.TryGetValue(address, out HashSet<UInt256>? keys))
                {
                    keys = new HashSet<UInt256>();
                    merged[address] = keys;
                }

                foreach (UInt256 key in storageKeys)
                {
                    keys.Add(key);
                }
            }
        }

        if (merged is null || merged.Count == 0)
        {
            return null;
        }

        AccessList.Builder builder = new();
        foreach ((Address address, HashSet<UInt256> keys) in merged)
        {
            builder.AddAddress(address);
            foreach (UInt256 key in keys)
            {
                builder.AddStorage(key);
            }
        }

        return builder.Build();
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
                SystemTxAccessLists!.Dispose();
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

        public AddressWarmingState(ObjectPool<IReadOnlyTxProcessorSource> envPool, Block block, BlockHeader parent, IReadOnlyTxProcessorSource env, IReadOnlyTxProcessingScope scope) : this(envPool, block, parent)
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

    private record BlockStateSource(BlockCachePreWarmer PreWarmer, Block Block, BlockHeader Parent, IReleaseSpec Spec);
}
