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
using Nethermind.State;
using Nethermind.Evm.State;
using Nethermind.Core.Eip2930;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

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
    private readonly IReadOnlyTrieStore? _trieStore;

    /// <summary>
    /// Updated by main thread after each tx via <see cref="SetTxExecutedCallback"/>.
    /// Prewarmer threads read this to skip txs the main thread already processed.
    /// </summary>
    internal volatile int MainThreadTxIndex = -1;

    public BlockCachePreWarmer(
        PrewarmerEnvFactory envFactory,
        IBlocksConfig blocksConfig,
        NodeStorageCache nodeStorageCache,
        PreBlockCaches preBlockCaches,
        IWorldStateManager worldStateManager,
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
        _trieStore = worldStateManager.CreateReadOnlyTrieStore();
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
            // NodeStorageCache is NOT cleared between blocks: trie nodes are immutable
            // and content-addressed, so cached RLP from the previous block is still valid.
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
        // NodeStorageCache is kept alive: trie nodes are immutable, content-addressed data.
        if (_logger.IsDebug) _logger.Debug($"Cleared caches: {cachesCleared}");
        return cachesCleared;
    }

    public void Dispose() => (_envPool as IDisposable)?.Dispose();

    private void PreWarmCachesParallel(BlockState blockState, Block suggestedBlock, BlockHeader parent, IReleaseSpec spec, ParallelOptions parallelOptions, AddressWarmer addressWarmer, CancellationToken cancellationToken)
    {
        // Mark prewarmer threads as non-processing so IsBlockProcessingThread gates
        // (metrics, SeqlockCache propagation) correctly distinguish prewarmer from main thread.
        bool prev = ProcessingThread.IsBlockProcessingThread;
        ProcessingThread.IsBlockProcessingThread = false;
        try
        {
            if (cancellationToken.IsCancellationRequested) return;

            if (_logger.IsDebug) _logger.Debug($"Started pre-warming caches for block {suggestedBlock.Number}.");

            if (parent?.StateRoot is not null)
            {
                _trieStore?.PrefetchUpperStateTrie(parent.StateRoot, maxDepth: 2);
            }

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
            addressWarmer.Wait();
            addressWarmer.Dispose();
            ProcessingThread.IsBlockProcessingThread = prev;
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
            int txCount = block.Transactions.Length;
            if (txCount == 0) return;

            // Phase 1: Bulk-warm all EIP-2930 access list entries across ALL txs in one parallel pass.
            // This populates the SeqlockCache with known storage slots before speculative execution begins,
            // giving the main thread immediate cache hits for access-listed entries.
            if (blockState.Spec.UseTxAccessLists)
            {
                WarmupAllAccessLists(blockState, parallelOptions);
            }

            if (parallelOptions.CancellationToken.IsCancellationRequested) return;

            // Phase 2: Moving window that stays ahead of the main thread. Workers claim txs
            // via an atomic counter, always targeting txs LookaheadOffset positions ahead of
            // the main thread. This gives the prewarmer time to finish before the main thread
            // arrives. After reaching the end, workers loop back to re-execute txs the main
            // thread still hasn't processed — each pass warms deeper cache entries.
            int nextTx = 0;
            WarmingState<BlockState> baseState = new(_envPool, blockState, blockState.Parent);

            ParallelUnbalancedWork.For(
                0,
                _concurrencyLevel,
                parallelOptions,
                baseState.InitThreadState,
                (workerIndex, state) =>
                {
                    BlockExecutionContext context = new(state.Payload.Block.Header, state.Payload.Spec);
                    state.Scope!.TransactionProcessor.SetBlockExecutionContext(context);
                    Transaction[] txs = state.Payload.Block.Transactions;
                    BlockCachePreWarmer pw = state.Payload.PreWarmer;
                    CancellationToken ct = parallelOptions.CancellationToken;

                    while (!ct.IsCancellationRequested)
                    {
                        int txIndex = Interlocked.Increment(ref nextTx) - 1;

                        // Past the end — wrap around to just ahead of main thread + offset
                        if (txIndex >= txCount)
                        {
                            int mainAt = pw.MainThreadTxIndex;
                            if (mainAt >= txCount - 1) break; // main thread done

                            // Reset to ahead of main thread; only one thread resets
                            int desired = mainAt + 1;
                            Interlocked.CompareExchange(ref nextTx, desired, txIndex + 1);
                            continue;
                        }

                        // Skip txs the main thread already processed
                        if (txIndex <= pw.MainThreadTxIndex)
                            continue;

                        if (Nethermind.Logging.DiagnosticLogger.IsEnabled)
                            Nethermind.Logging.DiagnosticLogger.Log($"PREWARM tx[{txIndex}] start (main at {pw.MainThreadTxIndex})");

                        WarmupSingleTransaction(state.Scope!, txs[txIndex], txIndex, state.Payload);
                    }

                    return state;
                },
                WarmingState<BlockState>.FinallyAction);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.DebugError("Error pre-warming transactions", ex);
        }
    }

    /// <summary>
    /// Bulk-warm all EIP-2930 access list entries from every tx in the block in parallel.
    /// Each worker claims a tx, reads its sender+to accounts and all access list storage slots
    /// into the SeqlockCache via WorldState.WarmUp. This runs before speculative tx execution
    /// so the cache is pre-populated with known entries for immediate main-thread hits.
    /// </summary>
    private void WarmupAllAccessLists(BlockState blockState, ParallelOptions parallelOptions)
    {
        Block block = blockState.Block;
        Transaction[] txs = block.Transactions;
        if (txs.Length == 0) return;

        WarmingState<BlockState> baseState = new(_envPool, blockState, blockState.Parent);

        ParallelUnbalancedWork.For(
            0,
            txs.Length,
            parallelOptions,
            baseState.InitThreadState,
            static (txIndex, state) =>
            {
                Transaction tx = state.Payload.Block.Transactions[txIndex];
                IWorldState worldState = state.Scope!.WorldState;

                try
                {
                    if (tx.SenderAddress is not null)
                        worldState.WarmUp(tx.SenderAddress);
                    if (tx.To is not null)
                        worldState.WarmUp(tx.To);
                    worldState.WarmUp(tx.AccessList);
                }
                catch (Exception) when (state.Scope is not null)
                {
                    // Swallow trie/state exceptions during warmup
                }

                return state;
            },
            WarmingState<BlockState>.FinallyAction);
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
