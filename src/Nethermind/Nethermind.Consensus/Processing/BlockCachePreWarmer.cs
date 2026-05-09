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

internal interface IPreBlockCacheWarmupSource : IReadOnlyTxProcessorSource
{
    IPreBlockCacheWarmupSession BuildPreBlockCacheWarmup(BlockHeader? baseBlock);
}

public sealed class BlockCachePreWarmer : IBlockCachePreWarmer
{
    private readonly int _concurrencyLevel;
    private readonly bool _parallelExecutionBatchRead;
    private readonly ObjectPool<IPreBlockCacheWarmupSource> _envPool;
    private readonly ILogger _logger;
    private readonly PreBlockCaches _preBlockCaches;
    private readonly NodeStorageCache _nodeStorageCache;
    private readonly bool _parallelExecutionEnabled;

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
        IPooledObjectPolicy<IPreBlockCacheWarmupSource> poolPolicy,
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
                ParallelOptions parallelOptions = new() { MaxDegreeOfParallelism = _concurrencyLevel, CancellationToken = cancellationToken };

                // BAL makes speculative tx execution redundant — when BAL-based read warming
                // is in use, drive warmup directly off the suggested block's access list.
                BlockAccessList? bal = IsBalReadWarmingEnabled(spec) ? suggestedBlock.BlockAccessList : null;

                // Run address warmer ahead of transactions warmer, but queue to ThreadPool so it doesn't block the txs
                AddressWarmer addressWarmer = new(parallelOptions, suggestedBlock, parent, spec, systemAccessLists, this, bal);
                ThreadPool.UnsafeQueueUserWorkItem(addressWarmer, preferLocal: false);
                // Do not pass the cancellation token to the task, we don't want exceptions to be thrown in the main processing thread
                return Task.Run(() => PreWarmCachesParallel(blockState, suggestedBlock, parent, spec, parallelOptions, addressWarmer, cancellationToken));
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

    public void Dispose() => (_envPool as IDisposable)?.Dispose();

    private void PreWarmCachesParallel(BlockState blockState, Block suggestedBlock, BlockHeader parent, IReleaseSpec spec, ParallelOptions parallelOptions, AddressWarmer addressWarmer, CancellationToken cancellationToken)
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
            // Don't complete the task until address warmer is also done.
            addressWarmer.Wait();
            addressWarmer.Dispose();
        }
    }

    private void WarmupWithdrawals(ParallelOptions parallelOptions, IReleaseSpec spec, Block block, BlockHeader parent)
    {
        if (parallelOptions.CancellationToken.IsCancellationRequested) return;

        try
        {
            if (spec.WithdrawalsEnabled && block.Withdrawals is not null)
            {
                if (TryWarmupWithdrawalsShared(parallelOptions, block, parent))
                {
                    return;
                }

                PreBlockCacheWarmingState<Block> baseState = new(_envPool, block, parent);

                ParallelUnbalancedWork.For(0, block.Withdrawals.Length, parallelOptions, baseState.InitThreadState,
                    static (i, state) =>
                    {
                        try
                        {
                            state.Scope!.WarmUp(state.Payload.Withdrawals![i].Address);
                        }
                        catch (MissingTrieNodeException)
                        {
                        }

                        return state;
                    },
                    PreBlockCacheWarmingState<Block>.FinallyAction);
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

    private bool TryWarmupWithdrawalsShared(ParallelOptions parallelOptions, Block block, BlockHeader parent) =>
        TryWarmupShared(parallelOptions, _envPool, parent, block, block.Withdrawals!.Length,
            static (i, payload, warmup) =>
            {
                try
                {
                    warmup.WarmUp(payload.Withdrawals![i].Address);
                }
                catch (MissingTrieNodeException)
                {
                }
            });

    /// <summary>
    /// Acquires an env, builds a shared <see cref="IPreBlockCacheWarmupSession"/>, and runs the per-item
    /// body in parallel - but only when the session reports <see cref="IPreBlockCacheWarmupSession.CanBeShared"/>.
    /// Returns <c>false</c> when sharing is unavailable so callers can fall back to per-thread sessions.
    /// </summary>
    private static bool TryWarmupShared<TPayload>(
        ParallelOptions parallelOptions,
        ObjectPool<IPreBlockCacheWarmupSource> envPool,
        BlockHeader parent,
        TPayload payload,
        int count,
        Action<int, TPayload, IPreBlockCacheWarmupSession> body)
    {
        IPreBlockCacheWarmupSource env = envPool.Get();
        try
        {
            using IPreBlockCacheWarmupSession warmup = env.BuildPreBlockCacheWarmup(parent);
            if (!warmup.CanBeShared)
            {
                return false;
            }

            ParallelUnbalancedWork.For(0, count, parallelOptions, (payload, warmup, body),
                static (i, state) =>
                {
                    state.body(i, state.payload, state.warmup);
                    return state;
                });

            return true;
        }
        finally
        {
            envPool.Return(env);
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
                        IPreBlockCacheWarmupSource env = blockState.PreWarmer._envPool.Get();
                        try
                        {
                            using IReadOnlyTxProcessingScope scope = env.Build(blockState.Parent);
                            BlockExecutionContext context = new(blockState.Block.Header, blockState.Spec);
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

            ObjectPool<IPreBlockCacheWarmupSource> envPool = PreWarmer._envPool;
            try
            {
                if (SystemTxAccessLists is not null)
                {
                    IPreBlockCacheWarmupSource env = envPool.Get();
                    try
                    {
                        using IPreBlockCacheWarmupSession warmup = env.BuildPreBlockCacheWarmup(parent);

                        foreach (AccessList list in SystemTxAccessLists.AsSpan())
                        {
                            warmup.WarmUp(list);
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
                    if (TryWarmupTransactionAddressesShared(parallelOptions, envPool, block))
                    {
                        return;
                    }

                    PreBlockCacheWarmingState<Block> baseState = new(envPool, block, parent);

                    ParallelUnbalancedWork.For(
                        0,
                        block.Transactions.Length,
                        parallelOptions,
                        baseState.InitThreadState,
                        static (i, state) =>
                        {
                            Transaction tx = state.Payload.Transactions[i];
                            WarmupSender(tx.SenderAddress, tx.To, state.Scope!);

                            return state;
                        },
                        PreBlockCacheWarmingState<Block>.FinallyAction);
                }
            }
            catch (OperationCanceledException)
            {
                // Ignore, block completed cancel
            }
        }

        private void WarmupFromBal(ParallelOptions parallelOptions, ObjectPool<IPreBlockCacheWarmupSource> envPool)
        {
            using ArrayPoolList<AccountChanges> accounts = Bal!.AccountChanges.ToPooledList(Bal!.AccountChanges.Count);

            if (TryWarmupFromBalShared(parallelOptions, envPool, accounts))
            {
                return;
            }

            PreBlockCacheWarmingState<ArrayPoolList<AccountChanges>> baseState = new(envPool, accounts, parent);

            ParallelUnbalancedWork.For(
                0,
                accounts.Count,
                parallelOptions,
                baseState.InitThreadState,
                static (i, state) =>
                {
                    AccountChanges ac = state.Payload[i];
                    IPreBlockCacheWarmupSession warmup = state.Scope!;

                    WarmupBalAccount(ac, warmup);

                    return state;
                },
                PreBlockCacheWarmingState<ArrayPoolList<AccountChanges>>.FinallyAction);
        }

        private bool TryWarmupTransactionAddressesShared(ParallelOptions parallelOptions, ObjectPool<IPreBlockCacheWarmupSource> envPool, Block block) =>
            TryWarmupShared(parallelOptions, envPool, parent, block, block.Transactions.Length,
                static (i, payload, warmup) =>
                {
                    Transaction tx = payload.Transactions[i];
                    WarmupSender(tx.SenderAddress, tx.To, warmup);
                });

        private bool TryWarmupFromBalShared(ParallelOptions parallelOptions, ObjectPool<IPreBlockCacheWarmupSource> envPool, ArrayPoolList<AccountChanges> accounts) =>
            TryWarmupShared(parallelOptions, envPool, parent, accounts, accounts.Count,
                static (i, payload, warmup) => WarmupBalAccount(payload[i], warmup));

        private static void WarmupBalAccount(AccountChanges ac, IPreBlockCacheWarmupSession warmup)
        {
            try
            {
                Address address = ac.Address;
                warmup.WarmUp(address);

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
                    warmup.Get(new StorageCell(address, slot));
                }
            }
            catch (MissingTrieNodeException)
            {
            }
        }

        private static void WarmupSender(Address? sender, Address? to, IPreBlockCacheWarmupSession warmup)
        {
            try
            {
                if (sender is not null)
                {
                    warmup.WarmUp(sender);
                }

                if (to is not null)
                {
                    warmup.WarmUp(to);
                }
            }
            catch (MissingTrieNodeException)
            {
            }
        }
    }

    private readonly struct PreBlockCacheWarmingState<TPayload>(ObjectPool<IPreBlockCacheWarmupSource> envPool, TPayload payload, BlockHeader parent) : IDisposable
    {
        public static Action<PreBlockCacheWarmingState<TPayload>> FinallyAction { get; } = DisposeThreadState;

        private readonly ObjectPool<IPreBlockCacheWarmupSource> EnvPool = envPool;
        private readonly IPreBlockCacheWarmupSource? Env;
        public readonly TPayload Payload = payload;
        public readonly IPreBlockCacheWarmupSession? Scope;

        private PreBlockCacheWarmingState(ObjectPool<IPreBlockCacheWarmupSource> envPool, TPayload payload, BlockHeader parent, IPreBlockCacheWarmupSource env, IPreBlockCacheWarmupSession scope) : this(envPool, payload, parent)
        {
            Env = env;
            Scope = scope;
        }

        public PreBlockCacheWarmingState<TPayload> InitThreadState()
        {
            IPreBlockCacheWarmupSource env = EnvPool.Get();
            return new(EnvPool, Payload, parent, env, env.BuildPreBlockCacheWarmup(parent));
        }

        public void Dispose()
        {
            Scope?.Dispose();
            if (Env is not null)
            {
                EnvPool.Return(Env);
            }
        }

        private static void DisposeThreadState(PreBlockCacheWarmingState<TPayload> state) => state.Dispose();
    }

    /// <summary>
    /// Pool policy for <see cref="IPreBlockCacheWarmupSource"/> envs used by the prewarmer.
    /// </summary>
    internal class ReadOnlyTxProcessingEnvPooledObjectPolicy(PrewarmerEnvFactory envFactory, PreBlockCaches _preBlockCaches) : IPooledObjectPolicy<IPreBlockCacheWarmupSource>
    {
        // The factory always builds a PrewarmerTxProcessingEnv, which implements both interfaces.
        // The cast is justified by the factory contract; if it ever fails, the prewarmer is
        // misconfigured and refusing to start is the right behavior.
        public IPreBlockCacheWarmupSource Create() => (IPreBlockCacheWarmupSource)envFactory.Create(_preBlockCaches);

        /// <remarks>
        /// Always returns true — the env is valid for reuse. The pool that owns this policy
        /// must call <see cref="IDisposable.Dispose"/> on any item it cannot retain; failing
        /// to do so leaks resources held by the env for the lifetime of the process.
        /// </remarks>
        public bool Return(IPreBlockCacheWarmupSource obj) => true;
    }

    private record BlockState(BlockCachePreWarmer PreWarmer, Block Block, BlockHeader Parent, IReleaseSpec Spec);
}
