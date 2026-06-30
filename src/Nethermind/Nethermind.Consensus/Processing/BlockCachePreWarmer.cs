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

    // Isolation flags (see IBlocksConfig). Default to current behavior (group by sender, no skip).
    private readonly bool _senderGrouping = true;
    private readonly bool _skipStartedTxs;

    // Warmup gas cap: a transaction whose gas limit exceeds this is speculatively warmed via a gas-capped
    // clone (see CapForWarmup), so the trailing compute of a heavy transaction is not re-executed against
    // the main thread. 0 disables the feature.
    private readonly long _adaptiveAbortMinGas;

    // When true, warm each tx through a cancellation-observing tracer so in-flight speculative warming is
    // abandoned the instant the main thread cancels (end of block). See BlockState.WarmupTracer.
    private readonly bool _cancelInflightWarming;

    // When true (default), eagerly load every slot/address declared in a tx's EIP-2930 access list. A tx can
    // over-declare a huge access list (e.g. 12.5k slots) while actually touching a handful; the speculative
    // warmup execution already warms the slots actually accessed, so the eager load is largely redundant and,
    // on the trie-backed (HalfPath) store, dominates the prewarm task. Off skips it.
    private readonly bool _warmupAccessList = true;

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
        _senderGrouping = blocksConfig.PreWarmSenderGrouping;
        _skipStartedTxs = blocksConfig.PreWarmSkipStartedTxs;
        _adaptiveAbortMinGas = blocksConfig.PreWarmAdaptiveAbortMinGas;
        _cancelInflightWarming = blocksConfig.PreWarmCancelInflightWarming;
        _warmupAccessList = blocksConfig.PreWarmWarmupAccessList;
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
                BlockState blockState = new(this, suggestedBlock, parent, spec, cancellationToken);
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
    /// speculative re-execution (and the cache/CPU contention it causes) of in-flight transactions.
    /// </remarks>
    public void OnBeforeTxExecution(Transaction transaction) => _currentBlockState?.IncrementTransactionCounter();

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

            long _pwTxStart = Stopwatch.GetTimestamp();
            if (!addressWarmer.HasBal)
            {
                WarmupTransactions(blockState, parallelOptions);
                WarmupWithdrawals(parallelOptions, spec, suggestedBlock, parent);
            }
            blockState.DiagWarmTxTicks = Stopwatch.GetTimestamp() - _pwTxStart;

            if (_logger.IsDebug) _logger.Debug($"Finished pre-warming caches for block {suggestedBlock.Number}.");
        }
        catch (Exception ex)
        {
            _logger.DebugWarn($"Error pre-warming {suggestedBlock.Number}. {ex}");
        }
        finally
        {
            // Don't complete the task until address warmer is also done.
            long _addrStart = Stopwatch.GetTimestamp();
            addressWarmer.Wait();
            long _addrTicks = Stopwatch.GetTimestamp() - _addrStart;
            if (_logger.IsWarn)
            {
                (long cnt, long totTicks, long maxTicks, long maxIdx) = blockState.WarmStats();
                static double toMs(long t) => t * 1000.0 / Stopwatch.Frequency;
                if (toMs(blockState.DiagWarmTxTicks) + toMs(_addrTicks) > 60.0)
                    _logger.Warn($"[PWDIAG] block={suggestedBlock.Number} txs={suggestedBlock.Transactions.Length} warmTxMs={toMs(blockState.DiagWarmTxTicks):F1} addrWaitMs={toMs(_addrTicks):F1} accListMs={toMs(blockState.AccessListTicks):F1} warmedTxs={cnt} sumWarmMs={toMs(totTicks):F1} maxTxMs={toMs(maxTicks):F1}@idx{maxIdx}");
            }
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

            if (!_senderGrouping)
            {
                WarmupTransactionsPerTx(blockState, parallelOptions);
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

    /// <summary>
    /// Per-transaction warming path (pre-#10330 behavior): one parallel work item per transaction, each
    /// warmed in a per-thread scope built from the parent. Used when sender grouping is disabled
    /// (<see cref="IBlocksConfig.PreWarmSenderGrouping"/>) — for isolating the effect of sender grouping.
    /// </summary>
    private void WarmupTransactionsPerTx(BlockState blockState, ParallelOptions parallelOptions)
    {
        Block block = blockState.Block;
        ParallelUnbalancedWork.For(
            0,
            block.Transactions.Length,
            parallelOptions,
            new WarmingState<BlockState>(_envPool, blockState, blockState.Parent).InitThreadState,
            static (i, state) =>
            {
                BlockState bs = state.Payload;
                Transaction? tx = null;
                try
                {
                    if (bs.PreWarmer._skipStartedTxs && bs.LastExecutedTransaction >= i) return state;

                    tx = bs.Block.Transactions[i];
                    Address senderAddress = tx.SenderAddress!;
                    IReadOnlyTxProcessingScope scope = state.Scope!;
                    IWorldState worldState = scope.WorldState;
                    if (!worldState.AccountExists(senderAddress))
                    {
                        worldState.CreateAccountIfNotExists(senderAddress, UInt256.Zero);
                    }

                    // Advance the sender nonce by the count of preceding same-sender txs so warming uses the right nonce.
                    ulong nonceDelta = 0;
                    for (int prev = 0; prev < i; prev++)
                    {
                        if (senderAddress == bs.Block.Transactions[prev].SenderAddress) nonceDelta++;
                    }
                    if (nonceDelta != 0) worldState.IncrementNonce(senderAddress, nonceDelta, out _);

                    if (bs.Spec.UseTxAccessLists && bs.PreWarmer._warmupAccessList)
                    {
                        long _at = Stopwatch.GetTimestamp();
                        worldState.WarmUp(tx.AccessList); // eip-2930
                        bs.RecordAccessList(Stopwatch.GetTimestamp() - _at);
                    }
                    if (bs.PreWarmer.SkipExecWarmup(tx, i)) return state;
                    scope.TransactionProcessor.SetBlockExecutionContext(new BlockExecutionContext(bs.Block.Header, bs.Spec));
                    scope.TransactionProcessor.Warmup(tx, bs.WarmupTracer);
                }
                catch (Exception ex) when (ex is EvmException or OverflowException or OperationCanceledException)
                {
                    // Ignore: regular tx processing exceptions and adaptive-warming aborts.
                }
                catch (Exception ex)
                {
                    bs.PreWarmer._logger.DebugError($"Error pre-warming cache {tx?.Hash}", ex);
                }

                return state;
            },
            WarmingState<BlockState>.FinallyAction);
    }

    private static Dictionary<AddressAsKey, ArrayPoolList<(int Index, Transaction Tx)>> GroupTransactionsBySender(Block block)
    {
        Dictionary<AddressAsKey, ArrayPoolList<(int, Transaction)>> groups = [];

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

    /// <summary>
    /// Whether to skip speculative <em>execution</em> of a transaction during warming.
    /// </summary>
    /// <remarks>
    /// The prewarmer starts together with the main processing thread, so it cannot get ahead of a heavy
    /// transaction at the very front of the block: the main thread begins executing it immediately. For such
    /// a dominating index-0 transaction, speculatively executing it does not pre-load anything the main
    /// thread reaches later — it only runs concurrently with the main thread's execution of the same
    /// transaction and contends with it (catastrophic for a compute-bound giant, e.g. block 22360451). Its
    /// sender and access list are still warmed cheaply by the caller. Gated by gas so normal first
    /// transactions are unaffected; 0 disables the skip.
    /// </remarks>
    private bool SkipExecWarmup(Transaction tx, int txIndex)
        => _adaptiveAbortMinGas > 0 && txIndex == 0 && tx.GasLimit > (ulong)_adaptiveAbortMinGas;

    private static void WarmupSingleTransaction(
        IReadOnlyTxProcessingScope scope,
        Transaction tx,
        int txIndex,
        BlockState blockState)
    {
        try
        {
            // Skip transactions the main thread has already started: re-executing them speculatively is
            // wasted work and contends with the main thread (severe for a heavy tx at a low index).
            if (blockState.PreWarmer._skipStartedTxs && blockState.LastExecutedTransaction >= txIndex) return;

            Address senderAddress = tx.SenderAddress!;
            IWorldState worldState = scope.WorldState;

            if (!worldState.AccountExists(senderAddress))
            {
                worldState.CreateAccountIfNotExists(senderAddress, UInt256.Zero);
            }

            if (blockState.Spec.UseTxAccessLists && blockState.PreWarmer._warmupAccessList)
            {
                long _at = Stopwatch.GetTimestamp();
                worldState.WarmUp(tx.AccessList); // eip-2930
                blockState.RecordAccessList(Stopwatch.GetTimestamp() - _at);
            }

            if (blockState.PreWarmer.SkipExecWarmup(tx, txIndex)) return;

            long _wt = Stopwatch.GetTimestamp();
            TransactionResult result = scope.TransactionProcessor.Warmup(tx, blockState.WarmupTracer);
            blockState.RecordWarm(txIndex, Stopwatch.GetTimestamp() - _wt);

            if (blockState.PreWarmer._logger.IsTrace) blockState.PreWarmer._logger.Trace($"Finished pre-warming cache for tx[{txIndex}] {tx.Hash} with {result}");
        }
        catch (Exception ex) when (ex is EvmException or OverflowException or OperationCanceledException)
        {
            // Ignore: regular tx processing exceptions and adaptive-warming aborts.
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

    private sealed class BlockState(BlockCachePreWarmer preWarmer, Block block, BlockHeader parent, IReleaseSpec spec, CancellationToken cancellationToken)
    {
        public BlockCachePreWarmer PreWarmer { get; } = preWarmer;
        public Block Block { get; } = block;
        public BlockHeader Parent { get; } = parent;
        public IReleaseSpec Spec { get; } = spec;

        // Tracer reused for every speculatively-warmed transaction in this block. When _cancelInflightWarming is on
        // it is a CancellationTxTracer bound to the prewarmer token: IsCancelable makes the EVM poll IsCancelled
        // (every 1024 opcodes per frame) so warming abandons the in-flight tx the instant the main thread cancels
        // (BranchProcessor.CancelBackgroundWork). Off (default) keeps the stock non-cancelable NullTxTracer.
        public ITxTracer WarmupTracer { get; } = preWarmer._cancelInflightWarming
            ? new CancellationTxTracer(NullTxTracer.Instance, cancellationToken)
            : NullTxTracer.Instance;

        // Highest transaction index the main processing thread has started executing (-1 = none yet).
        // Written only by the single main thread (in order) via IncrementTransactionCounter; read by prewarmer threads.
        private int _lastExecutedTransaction = -1;
        public int LastExecutedTransaction => Volatile.Read(ref _lastExecutedTransaction);
        public void IncrementTransactionCounter() => Interlocked.Increment(ref _lastExecutedTransaction);

        // Diagnostic accumulators for attributing prewarm-task time (see [PWDIAG] log).
        public long DiagWarmTxTicks;
        private long _accessListTicks;
        public void RecordAccessList(long ticks) => Interlocked.Add(ref _accessListTicks, ticks);
        public long AccessListTicks => Volatile.Read(ref _accessListTicks);
        private long _warmedCount;
        private long _totalWarmTicks;
        private long _maxWarmTicks;
        private long _maxWarmTxIndex;
        public void RecordWarm(int txIndex, long ticks)
        {
            Interlocked.Increment(ref _warmedCount);
            Interlocked.Add(ref _totalWarmTicks, ticks);
            long prevMax = Volatile.Read(ref _maxWarmTicks);
            while (ticks > prevMax)
            {
                long orig = Interlocked.CompareExchange(ref _maxWarmTicks, ticks, prevMax);
                if (orig == prevMax) { Volatile.Write(ref _maxWarmTxIndex, txIndex); break; }
                prevMax = orig;
            }
        }
        public (long Count, long TotalTicks, long MaxTicks, long MaxIdx) WarmStats()
            => (Volatile.Read(ref _warmedCount), Volatile.Read(ref _totalWarmTicks), Volatile.Read(ref _maxWarmTicks), Volatile.Read(ref _maxWarmTxIndex));
    }
}
