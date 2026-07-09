// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Collections.Pooled;
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
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Trie;
using PrewarmMetrics = Nethermind.Consensus.Processing.Prewarming.Metrics;

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

    private int _mainThreadTxIndex = -1;
    internal int MainThreadTxIndex => Volatile.Read(ref _mainThreadTxIndex);

    // Start/cancel/join are serialized by _speculativeLock, and a session is always joined before the reactive path
    // touches the shared caches.
    private readonly Lock _speculativeLock = new();
    private CancellationTokenSource? _speculativeCts;
    private Task _speculativeTask = Task.CompletedTask;
    private long _speculativeGeneration = long.MinValue;

    // Written only by the loop thread and read after it is joined, so the marker and its tx-hash set need no further sync.
    private WarmMarker? _warmMarker;

    private readonly PooledSet<Hash256> _warmedTxHashes = [];

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
        _concurrencyLevel = concurrency == 0 ? Math.Min(Environment.ProcessorCount - 1, 16) : concurrency;
        _parallelExecutionBatchRead = parallelExecutionBatchRead;
        _envPool = new DefaultObjectPoolProvider { MaximumRetained = maxPoolSize }.Create(poolPolicy);
        _logger = logManager.GetClassLogger<BlockCachePreWarmer>();
        _preBlockCaches = preBlockCaches;
        _nodeStorageCache = nodeStorageCache;
    }

    public Task PreWarmCaches(Block suggestedBlock, BlockHeader? parent, IReleaseSpec spec, CancellationToken cancellationToken = default, params ReadOnlySpan<IHasAccessList> systemAccessLists)
    {
        if (_preBlockCaches is null || !ShouldPreWarm(spec)) return Task.CompletedTask;

        CancelAndJoinSpeculative();

        if (TryConsumeWarmMarker(suggestedBlock.ParentHash, spec, out ISet<Hash256>? speculativelyWarmed))
        {
            // Handoff: keep the speculatively-warmed caches; the reactive warm below fills only the un-warmed senders.
            PrewarmMetrics.MempoolPrewarmHandoffs++;
            _nodeStorageCache.Enabled = true;
        }
        else
        {
            CacheType result = _preBlockCaches.ClearCaches();
            _nodeStorageCache.ClearCaches();
            _nodeStorageCache.Enabled = true;
            if (result != default)
            {
                if (_logger.IsWarn) _logger.Warn($"Caches {result} are not empty. Clearing them.");
            }
        }

        return WarmCaches(suggestedBlock, parent, spec, speculativelyWarmed, cancellationToken, systemAccessLists);
    }

    private Task WarmCaches(Block suggestedBlock, BlockHeader? parent, IReleaseSpec spec, ISet<Hash256>? speculativelyWarmed, CancellationToken cancellationToken, ReadOnlySpan<IHasAccessList> systemAccessLists)
    {
        if (parent is null || _concurrencyLevel <= 1 || cancellationToken.IsCancellationRequested) return Task.CompletedTask;

        (BlockState blockState, ParallelOptions parallelOptions, AddressWarmer addressWarmer) = PrepareWarm(suggestedBlock, parent, spec, speculativelyWarmed, cancellationToken, systemAccessLists);
        // Run address warmer ahead of transactions warmer, but queue to ThreadPool so it doesn't block the txs
        ThreadPool.UnsafeQueueUserWorkItem(addressWarmer, preferLocal: false);
        // Do not pass the cancellation token to the task, we don't want exceptions to be thrown in the main processing thread
        return Task.Run(() => PreWarmCachesParallel(blockState, suggestedBlock, parent, spec, parallelOptions, addressWarmer, cancellationToken));
    }

    // Synchronous variant used by the speculative loop: joins its workers before returning.
    private void WarmDeltaSync(Block delta, BlockHeader head, IReleaseSpec spec, CancellationToken token)
    {
        (BlockState blockState, ParallelOptions parallelOptions, AddressWarmer addressWarmer) = PrepareWarm(delta, head, spec, speculativelyWarmed: null, token, systemAccessLists: default);
        ThreadPool.UnsafeQueueUserWorkItem(addressWarmer, preferLocal: false);
        PreWarmCachesParallel(blockState, delta, head, spec, parallelOptions, addressWarmer, token);
    }

    private (BlockState BlockState, ParallelOptions ParallelOptions, AddressWarmer AddressWarmer) PrepareWarm(Block block, BlockHeader parent, IReleaseSpec spec, ISet<Hash256>? speculativelyWarmed, CancellationToken token, ReadOnlySpan<IHasAccessList> systemAccessLists)
    {
        BlockState blockState = new(this, block, parent, spec, speculativelyWarmed);
        // Safe for the speculative caller too: it never overlaps main execution (reactive path joins it before
        // ProcessOne; the next session only starts on NewHeadBlock, after processing completes).
        Volatile.Write(ref _mainThreadTxIndex, -1);
        ParallelOptions parallelOptions = new() { MaxDegreeOfParallelism = _concurrencyLevel, CancellationToken = token };
        // BAL makes speculative tx execution redundant — when BAL-based read warming is in use, drive warmup
        // directly off the block's access list.
        ReadOnlyBlockAccessList? bal = IsBalReadWarmingEnabled(spec) ? block.BlockAccessList : null;
        AddressWarmer addressWarmer = new(parallelOptions, block, parent, spec, systemAccessLists, this, bal);
        return (blockState, parallelOptions, addressWarmer);
    }

    public Task StartSpeculativePreWarm(BlockHeader head, IReleaseSpec spec, long generation, Func<CancellationToken, Block?> nextDelta, int idlePassDelayMs, CancellationToken cancellationToken)
    {
        // _concurrencyLevel <= 1 disables warming; a session that warms nothing must not publish a handoff marker.
        if (_preBlockCaches is null || !ShouldPreWarm(spec) || _concurrencyLevel <= 1) return Task.CompletedTask;
        if (head.Hash is not Hash256 headHash) return Task.CompletedTask;

        lock (_speculativeLock)
        {
            // An equal-or-newer session already started (out-of-order work item); don't clobber it.
            if (generation <= _speculativeGeneration) return _speculativeTask;
            _speculativeGeneration = generation;

            CancelAndJoinSpeculativeLocked();

            // Link to the caller's token so the caller can cancel (dispose); the reactive path cancels this CTS directly.
            CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _speculativeCts = cts;
            CancellationToken token = cts.Token;

            ClearWarmMarker();
            _warmedTxHashes.Clear();
            _preBlockCaches.ClearCaches();
            _nodeStorageCache.ClearCaches();
            _nodeStorageCache.Enabled = true;

            return _speculativeTask = Task.Run(() => RunSpeculativeLoop(headHash, head, spec, nextDelta, idlePassDelayMs, token));
        }
    }

    /// <summary>Repeated delta passes against <paramref name="head"/> until cancelled, re-sampling after a short idle so late-arriving txs are still warmed.</summary>
    private void RunSpeculativeLoop(Hash256 headHash, BlockHeader head, IReleaseSpec spec, Func<CancellationToken, Block?> nextDelta, int idlePassDelayMs, CancellationToken token)
    {
        // The tx-hash set is reused across sessions (cleared at session start); only the small marker is per-session.
        WarmMarker marker = new(headHash, spec, _warmedTxHashes);
        try
        {
            int delay = Math.Max(1, idlePassDelayMs);
            while (!token.IsCancellationRequested)
            {
                Block? delta = nextDelta(token);
                if (token.IsCancellationRequested) break;

                if (delta is not null && delta.Transactions.Length > 0)
                {
                    WarmDeltaSync(delta, head, spec, token);
                    // Don't record hashes of a delta cancelled mid-warm, or the reactive pass would skip senders never
                    // fully warmed. The prior completed delta's marker still stands.
                    if (token.IsCancellationRequested) break;
                    foreach (Transaction tx in delta.Transactions)
                    {
                        if (tx.Hash is Hash256 hash) _warmedTxHashes.Add(hash);
                    }
                    PrewarmMetrics.MempoolPrewarmDeltaPasses++;
                    PrewarmMetrics.MempoolPrewarmTxsWarmed += delta.Transactions.Length;
                    Volatile.Write(ref _warmMarker, marker);
                }

                // Rate-limit every pass (not only empty ones) so a churning mempool can't keep a lazy, non-cancellable
                // ITxSource selection continuously in flight. Cancellation wakes us immediately and ends the loop.
                if (token.WaitHandle.WaitOne(delay)) break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.DebugWarn($"Error during speculative pre-warming. {ex}");
        }
    }

    /// <summary>True once a speculative session has warmed at least one delta and published its handoff marker; for tests.</summary>
    internal bool SpeculativeMarkerPublished => Volatile.Read(ref _warmMarker) is not null;

    private void CancelAndJoinSpeculative()
    {
        lock (_speculativeLock)
        {
            CancelAndJoinSpeculativeLocked();
        }
    }

    private void CancelAndJoinSpeculativeLocked()
    {
        if (_speculativeCts is null) return;

        _speculativeCts.Cancel();
        try
        {
            _speculativeTask.GetAwaiter().GetResult();
        }
        catch
        {
            // Warming failures are already logged inside the pass; nothing actionable here.
        }
        _speculativeCts.Dispose();
        _speculativeCts = null;
        _speculativeTask = Task.CompletedTask;
    }

    private void ClearWarmMarker() => Volatile.Write(ref _warmMarker, null);

    private bool TryConsumeWarmMarker(Hash256? parentHash, IReleaseSpec spec, out ISet<Hash256>? warmedTxHashes)
    {
        WarmMarker? marker = Volatile.Read(ref _warmMarker);
        // Fork identity via ReferenceEquals: ISpecProvider hands out per-fork singletons, so a match means same fork.
        // A mismatch only disables the handoff (caches are cleared as usual) — an effectiveness guard, not correctness.
        if (marker is not null && parentHash is not null && marker.ParentHash == parentHash && ReferenceEquals(marker.Spec, spec))
        {
            warmedTxHashes = marker.WarmedTxHashes;
            Volatile.Write(ref _warmMarker, null);
            return true;
        }

        warmedTxHashes = null;
        return false;
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
        CancelAndJoinSpeculative();
        ClearWarmMarker();
        CacheType cachesCleared = _preBlockCaches?.ClearCaches() ?? default;
        cachesCleared |= _nodeStorageCache.ClearCaches() ? CacheType.Rlp : CacheType.None;
        if (_logger.IsDebug) _logger.Debug($"Cleared caches: {cachesCleared}");
        return cachesCleared;
    }

    public void Dispose()
    {
        CancelAndJoinSpeculative();
        _warmedTxHashes.Dispose();
        (_envPool as IDisposable)?.Dispose();
    }

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

                        // Whole group already warmed speculatively — skip; leave the rest to the reactive pass.
                        if (blockState.SpeculativelyWarmed is { } warmed)
                        {
                            if (AllSpeculativelyWarmed(txList, warmed))
                            {
                                Interlocked.Increment(ref PrewarmMetrics.MempoolPrewarmSendersSkipped);
                                return tupleState;
                            }
                            Interlocked.Increment(ref PrewarmMetrics.MempoolPrewarmSendersWarmed);
                        }

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

    private static bool AllSpeculativelyWarmed(ArrayPoolList<(int Index, Transaction Tx)> group, ISet<Hash256> warmed)
    {
        foreach ((int _, Transaction tx) in group.AsSpan())
        {
            if (tx.Hash is not Hash256 hash || !warmed.Contains(hash)) return false;
        }

        return true;
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
            if (blockState.PreWarmer.MainThreadTxIndex >= txIndex) return;

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

    private record BlockState(BlockCachePreWarmer PreWarmer, Block Block, BlockHeader Parent, IReleaseSpec Spec, ISet<Hash256>? SpeculativelyWarmed = null);

    private sealed record WarmMarker(Hash256 ParentHash, IReleaseSpec Spec, ISet<Hash256> WarmedTxHashes);
}
