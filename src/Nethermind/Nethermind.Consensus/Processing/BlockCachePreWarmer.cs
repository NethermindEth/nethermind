// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
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
    ILogManager logManager,
    bool groupBySender = false,
    bool validateSenderNonce = false,
    bool fastPathSimpleTransfers = false,
    bool repeatWarmup = true,
    int maxWarmupPasses = 3,
    bool warmupStorageKeys = true,
    bool warmupCode = true
) : IBlockCachePreWarmer
{
    private int _concurrencyLevel = (concurrency == 0 ? Math.Min(Environment.ProcessorCount - 1, 16) : concurrency);
    private readonly ObjectPool<IReadOnlyTxProcessorSource> _envPool = new DefaultObjectPool<IReadOnlyTxProcessorSource>(new ReadOnlyTxProcessingEnvPooledObjectPolicy(envFactory, preBlockCaches), Environment.ProcessorCount * 2);
    private readonly ILogger _logger = logManager.GetClassLogger<BlockCachePreWarmer>();
    private readonly bool _groupBySender = groupBySender;
    private readonly bool _validateSenderNonce = validateSenderNonce;
    private readonly bool _fastPathSimpleTransfers = fastPathSimpleTransfers;
    private readonly bool _repeatWarmup = repeatWarmup;
    private readonly int _maxWarmupPasses = maxWarmupPasses;
    private readonly bool _warmupStorageKeys = warmupStorageKeys;
    private readonly bool _warmupCode = warmupCode;
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
        logManager,
        blocksConfig.PreWarmStateGroupBySender,
        blocksConfig.PreWarmStateValidateSenderNonce,
        blocksConfig.PreWarmStateFastPathSimpleTransfers,
        blocksConfig.PreWarmStateRepeatWarmup,
        blocksConfig.PreWarmStateMaxWarmupPasses,
        blocksConfig.PreWarmStateWarmupStorageKeys,
        blocksConfig.PreWarmStateWarmupCode)
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
            // Don't compete task until address warmer is also done.
            addressWarmer.Wait();
        }
    }

    public void OnBeforeTxExecution(Transaction transaction)
    {
        _currentBlockState?.IncrementTransactionCounter();
    }

    private void WarmupWithdrawals(ParallelOptions parallelOptions, IReleaseSpec spec, Block block, BlockHeader? parent)
    {
        if (parallelOptions.CancellationToken.IsCancellationRequested) return;

        try
        {
            if (spec.WithdrawalsEnabled && block.Withdrawals is not null)
            {
                ParallelUnbalancedWork.For(0, block.Withdrawals.Length, parallelOptions, (envPool: _envPool, block, parent),
                    static (i, state) =>
                    {
                        IReadOnlyTxProcessorSource env = state.envPool.Get();
                        try
                        {
                            using IReadOnlyTxProcessingScope scope = env.Build(state.parent);
                            scope.WorldState.WarmUp(state.block.Withdrawals[i].Address);
                        }
                        catch (MissingTrieNodeException)
                        {
                        }
                        finally
                        {
                            state.envPool.Return(env);
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
            SenderWarmupPlan senderPlan = default;
            HashSet<Address>? repeatedRecipients = null;
            bool useSenderPlan = _groupBySender || _validateSenderNonce;
            if (useSenderPlan)
            {
                senderPlan = BuildSenderWarmupPlan(block.Transactions, _groupBySender);
                repeatedRecipients = _warmupCode ? BuildRepeatedRecipients(block.Transactions) : null;
                blockState.ApplyWarmupPlan(senderPlan, repeatedRecipients, _validateSenderNonce, _fastPathSimpleTransfers, _warmupStorageKeys, _warmupCode);
            }
            else
            {
                repeatedRecipients = _warmupCode ? BuildRepeatedRecipients(block.Transactions) : null;
                blockState.ApplyWarmupPlan(default, repeatedRecipients, _validateSenderNonce, _fastPathSimpleTransfers, _warmupStorageKeys, _warmupCode);
            }

            try
            {
                int maxPasses = _repeatWarmup ? Math.Max(1, _maxWarmupPasses) : 1;
                int pass = 0;
                while (pass < maxPasses)
                {
                    if (parallelOptions.CancellationToken.IsCancellationRequested) return;

                    int lastExecutedBefore = blockState.LastExecutedTransaction;
                    if (lastExecutedBefore >= block.Transactions.Length)
                    {
                        break;
                    }

                    if (_groupBySender && senderPlan.SenderGroups is not null)
                    {
                        WarmupTransactionsBySender(blockState, senderPlan.SenderGroups, parallelOptions);
                    }
                    else
                    {
                        WarmupTransactionsParallel(blockState, parallelOptions);
                    }

                    pass++;
                    if (!_repeatWarmup) break;

                    int lastExecutedAfter = blockState.LastExecutedTransaction;
                    if (lastExecutedAfter >= block.Transactions.Length)
                    {
                        break;
                    }

                    if (lastExecutedAfter == lastExecutedBefore)
                    {
                        break;
                    }
                }
            }
            finally
            {
                blockState.ClearWarmupPlan();
                senderPlan.Dispose();
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

    private void WarmupTransactionsParallel(BlockStateSource blockState, ParallelOptions parallelOptions)
    {
        Block block = blockState.Block;
        ParallelUnbalancedWork.For(
            0,
            block.Transactions.Length,
            parallelOptions,
            blockState.InitThreadState,
        static (i, state) =>
        {
            Transaction? tx = null;
            try
            {
                // If the transaction has already been processed or being processed, exit early
                if (state.LastExecutedTransaction >= i)
                {
                    return state;
                }

                tx = state.Block.Transactions[i];

                Address senderAddress = tx.SenderAddress!;
                IWorldState worldState = state.Scope.WorldState;
                if (!worldState.AccountExists(senderAddress))
                {
                    worldState.CreateAccountIfNotExists(senderAddress, UInt256.Zero);
                }

                int senderOffset = GetSenderOffset(state, i, senderAddress);
                if (state.UseSenderOffsets)
                {
                    if (!TryApplySenderNonce(state, senderAddress, senderOffset, tx))
                    {
                        return state;
                    }
                }
                else
                {
                    if (senderOffset != 0)
                    {
                        worldState.IncrementNonce(senderAddress, new UInt256((ulong)senderOffset));
                    }
                }

                    if (state.Spec.UseTxAccessLists)
                    {
                        worldState.WarmUp(tx.AccessList); // eip-2930
                        if (state.WarmupStorageKeys && tx.AccessList is not null)
                        {
                            WarmupStorageKeysFromAccessList(worldState, tx.AccessList);
                        }
                    }

                    if (state.WarmupCode)
                    {
                        WarmupCodeForRecipient(worldState, tx.To, state.Spec, state.RepeatedRecipients);
                    }

                    if (state.FastPathSimpleTransfers && CanSkipEvmWarmup(tx, worldState, state.Spec))
                    {
                        return state;
                    }

                TransactionResult result = state.Scope.TransactionProcessor.Warmup(tx, NullTxTracer.Instance);
                if (state.Logger.IsTrace) state.Logger.Trace($"Finished pre-warming cache for tx[{i}] {tx.Hash} with {result}");
            }
            catch (Exception ex) when (ex is EvmException or OverflowException)
            {
                // Ignore, regular tx processing exceptions
            }
            catch (Exception ex)
            {
                if (state.Logger.IsDebug) state.Logger.Error($"DEBUG/ERROR Error pre-warming cache {tx?.Hash}", ex);
            }

            return state;
        },
        BlockStateSource.FinallyAction);
    }

    private void WarmupTransactionsBySender(BlockStateSource blockState, ArrayPoolList<ArrayPoolList<int>> senderGroups, ParallelOptions parallelOptions)
    {
        ParallelUnbalancedWork.For(
            0,
            senderGroups.Count,
            parallelOptions,
            blockState.InitThreadState,
        (groupIndex, state) =>
        {
            ArrayPoolList<int> group = senderGroups[groupIndex];
            Transaction? tx = null;
            try
            {
                for (int index = 0; index < group.Count; index++)
                {
                    int txIndex = group[index];
                    if (state.LastExecutedTransaction >= txIndex)
                    {
                        continue;
                    }

                    tx = state.Block.Transactions[txIndex];
                    Address senderAddress = tx.SenderAddress!;
                    IWorldState worldState = state.Scope.WorldState;
                    if (!worldState.AccountExists(senderAddress))
                    {
                        worldState.CreateAccountIfNotExists(senderAddress, UInt256.Zero);
                    }

                    int senderOffset = GetSenderOffset(state, txIndex, senderAddress);
                    if (!TryApplySenderNonce(state, senderAddress, senderOffset, tx))
                    {
                        continue;
                    }

                    if (state.Spec.UseTxAccessLists)
                    {
                        worldState.WarmUp(tx.AccessList); // eip-2930
                        if (state.WarmupStorageKeys && tx.AccessList is not null)
                        {
                            WarmupStorageKeysFromAccessList(worldState, tx.AccessList);
                        }
                    }

                    if (state.WarmupCode)
                    {
                        WarmupCodeForRecipient(worldState, tx.To, state.Spec, state.RepeatedRecipients);
                    }

                    if (state.FastPathSimpleTransfers && CanSkipEvmWarmup(tx, worldState, state.Spec))
                    {
                        continue;
                    }

                    TransactionResult result = state.Scope.TransactionProcessor.Warmup(tx, NullTxTracer.Instance);
                    if (state.Logger.IsTrace) state.Logger.Trace($"Finished pre-warming cache for tx[{txIndex}] {tx.Hash} with {result}");
                }
            }
            catch (Exception ex) when (ex is EvmException or OverflowException)
            {
                // Ignore, regular tx processing exceptions
            }
            catch (Exception ex)
            {
                if (state.Logger.IsDebug) state.Logger.Error($"DEBUG/ERROR Error pre-warming cache {tx?.Hash}", ex);
            }

            return state;
        },
        BlockStateSource.FinallyAction);
    }

    private static bool TryApplySenderNonce(BlockState state, Address senderAddress, int senderOffset, Transaction tx)
    {
        IWorldState worldState = state.Scope.WorldState;
        UInt256 expectedNonce;
        if (state.UseSenderOffsets)
        {
            UInt256 baseNonce = state.GetSenderBaseNonce(senderAddress);
            expectedNonce = baseNonce + new UInt256((ulong)senderOffset);
            if (state.ValidateSenderNonce && expectedNonce != tx.Nonce)
            {
                return false;
            }

            worldState.SetNonce(senderAddress, expectedNonce);
            return true;
        }

        if (senderOffset != 0)
        {
            worldState.IncrementNonce(senderAddress, new UInt256((ulong)senderOffset));
        }

        return true;
    }

    private static int GetSenderOffset(BlockState state, int txIndex, Address senderAddress)
    {
        if (state.UseSenderOffsets && txIndex < state.SenderOffsetsCount && state.SenderOffsets is not null)
        {
            return state.SenderOffsets[txIndex];
        }

        int senderOffset = 0;
        for (int prev = 0; prev < txIndex; prev++)
        {
            if (senderAddress == state.Block.Transactions[prev].SenderAddress)
            {
                senderOffset++;
            }
        }

        return senderOffset;
    }

    internal static bool CanSkipEvmWarmup(Transaction tx, IWorldState worldState, IReleaseSpec spec)
    {
        if (tx.IsContractCreation || tx.DataLength != 0)
        {
            return false;
        }

        Address? recipient = tx.To;
        if (recipient is null)
        {
            return false;
        }

        if (spec.IsPrecompile(recipient))
        {
            return true;
        }

        return !worldState.IsContract(recipient);
    }

    internal static void WarmupStorageKeysFromAccessList(IWorldState worldState, AccessList accessList)
    {
        foreach ((Address address, AccessList.StorageKeysEnumerable storageKeys) in accessList)
        {
            foreach (UInt256 key in storageKeys)
            {
                try
                {
                    worldState.Get(new StorageCell(address, key));
                }
                catch (MissingTrieNodeException)
                {
                }
            }
        }
    }

    internal static void WarmupCodeForRecipient(IWorldState worldState, Address? recipient, IReleaseSpec spec, HashSet<Address>? repeatedRecipients)
    {
        if (recipient is null || spec.IsPrecompile(recipient))
        {
            return;
        }

        if (repeatedRecipients is not null && !repeatedRecipients.Contains(recipient))
        {
            return;
        }

        if (worldState.IsContract(recipient))
        {
            worldState.GetCode(recipient);
        }
    }

    internal static HashSet<Address>? BuildRepeatedRecipients(ReadOnlySpan<Transaction> transactions)
    {
        if (transactions.IsEmpty)
        {
            return null;
        }

        HashSet<Address> seen = new(transactions.Length);
        HashSet<Address> repeated = new();

        foreach (Transaction tx in transactions)
        {
            Address? recipient = tx.To;
            if (recipient is null)
            {
                continue;
            }

            if (!seen.Add(recipient))
            {
                repeated.Add(recipient);
            }
        }

        return repeated.Count == 0 ? null : repeated;
    }

    internal static SenderWarmupPlan BuildSenderWarmupPlan(ReadOnlySpan<Transaction> transactions, bool includeGroups)
    {
        int txCount = transactions.Length;
        if (txCount == 0)
        {
            return default;
        }

        int[] offsets = ArrayPool<int>.Shared.Rent(txCount);
        Dictionary<Address, SenderAccumulator> senders = new(txCount);
        ArrayPoolList<ArrayPoolList<int>>? groups = includeGroups ? new ArrayPoolList<ArrayPoolList<int>>(Math.Min(txCount, 1024)) : null;

        for (int i = 0; i < txCount; i++)
        {
            Address sender = transactions[i].SenderAddress!;
            ref SenderAccumulator acc = ref CollectionsMarshal.GetValueRefOrAddDefault(senders, sender, out bool exists);
            if (!exists)
            {
                int groupIndex = groups is null ? -1 : groups.Count;
                acc = new SenderAccumulator(0, groupIndex);
                if (groups is not null)
                {
                    groups.Add(new ArrayPoolList<int>(capacity: 4));
                }
            }

            offsets[i] = acc.Count;
            acc.Count++;

            if (groups is not null)
            {
                groups[acc.GroupIndex].Add(i);
            }
        }

        return new SenderWarmupPlan(offsets, txCount, groups);
    }

    internal readonly struct SenderWarmupPlan : IDisposable
    {
        private readonly int[]? _offsets;
        private readonly int _transactionCount;
        public readonly ArrayPoolList<ArrayPoolList<int>>? SenderGroups;

        public SenderWarmupPlan(int[] offsets, int transactionCount, ArrayPoolList<ArrayPoolList<int>>? senderGroups)
        {
            _offsets = offsets;
            _transactionCount = transactionCount;
            SenderGroups = senderGroups;
        }

        public int[]? OffsetsArray => _offsets;
        public int TransactionCount => _transactionCount;

        public void Dispose()
        {
            if (_offsets is not null)
            {
                ArrayPool<int>.Shared.Return(_offsets, clearArray: false);
            }

            if (SenderGroups is not null)
            {
                foreach (ArrayPoolList<int> group in SenderGroups.AsSpan())
                {
                    group.Dispose();
                }
                SenderGroups.Dispose();
            }
        }
    }

    private struct SenderAccumulator(int count, int groupIndex)
    {
        public int Count = count;
        public int GroupIndex = groupIndex;
    }

    private class AddressWarmer(ParallelOptions parallelOptions, Block block, BlockHeader parent, IReleaseSpec spec, ReadOnlySpan<IHasAccessList> systemAccessLists, BlockCachePreWarmer preWarmer)
        : IThreadPoolWorkItem
    {
        private readonly Block Block = block;
        private readonly Hash256 StateRoot = parent.StateRoot;
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
                SystemTxAccessLists.Dispose();
                return;
            }

            ObjectPool<IReadOnlyTxProcessorSource> envPool = PreWarmer._envPool;
            try
            {
                if (SystemTxAccessLists is not null)
                {
                    var env = envPool.Get();
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
                            state.Scope.WorldState.WarmUp(sender);
                        }

                        Address to = tx.To;
                        if (to is not null)
                        {
                            state.Scope.WorldState.WarmUp(to);
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

        public readonly ObjectPool<IReadOnlyTxProcessorSource> EnvPool = envPool;
        public readonly Block Block = block;
        public readonly IReadOnlyTxProcessorSource? Env;
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
            Scope.Dispose();
            EnvPool.Return(Env);
        }

        private static void DisposeThreadState(AddressWarmingState state) => state.Dispose();
    }

    private class ReadOnlyTxProcessingEnvPooledObjectPolicy(PrewarmerEnvFactory envFactory, PreBlockCaches preBlockCaches) : IPooledObjectPolicy<IReadOnlyTxProcessorSource>
    {
        public IReadOnlyTxProcessorSource Create() => envFactory.Create(preBlockCaches);
        public bool Return(IReadOnlyTxProcessorSource obj) => true;
    }

    private class BlockStateSource(BlockCachePreWarmer preWarmer, Block block, BlockHeader parent, IReleaseSpec spec)
    {
        public static Action<BlockState> FinallyAction { get; } = DisposeThreadState;

        public readonly BlockCachePreWarmer PreWarmer = preWarmer;
        public readonly Block Block = block;
        public readonly BlockHeader Parent = parent;
        public readonly IReleaseSpec Spec = spec;
        public volatile int LastExecutedTransaction = 0;
        public int[]? SenderOffsets;
        public int SenderOffsetsCount;
        public bool ValidateSenderNonce;
        public bool FastPathSimpleTransfers;
        public bool WarmupStorageKeys;
        public bool WarmupCode;
        public HashSet<Address>? RepeatedRecipients;

        public BlockState InitThreadState()
        {
            return new(this);
        }

        private static void DisposeThreadState(BlockState state) => state.Dispose();

        public void IncrementTransactionCounter()
        {
            Interlocked.Increment(ref LastExecutedTransaction);
        }

        public void ApplyWarmupPlan(SenderWarmupPlan plan, HashSet<Address>? repeatedRecipients, bool validateSenderNonce, bool fastPathSimpleTransfers, bool warmupStorageKeys, bool warmupCode)
        {
            SenderOffsets = plan.OffsetsArray;
            SenderOffsetsCount = plan.TransactionCount;
            ValidateSenderNonce = validateSenderNonce;
            FastPathSimpleTransfers = fastPathSimpleTransfers;
            WarmupStorageKeys = warmupStorageKeys;
            WarmupCode = warmupCode;
            RepeatedRecipients = repeatedRecipients;
        }

        public void ClearWarmupPlan()
        {
            SenderOffsets = null;
            SenderOffsetsCount = 0;
            ValidateSenderNonce = false;
            FastPathSimpleTransfers = false;
            WarmupStorageKeys = false;
            WarmupCode = false;
            RepeatedRecipients = null;
        }
    }

    private readonly struct BlockState
    {
        private readonly BlockStateSource Src;
        public readonly IReadOnlyTxProcessorSource Env;
        public readonly IReadOnlyTxProcessingScope Scope;
        private readonly Dictionary<Address, UInt256>? _senderBaseNonces;

        public ref readonly ILogger Logger => ref Src.PreWarmer._logger;
        public IReleaseSpec Spec => Src.Spec;
        public Block Block => Src.Block;
        public int LastExecutedTransaction => Src.LastExecutedTransaction;
        public int[]? SenderOffsets => Src.SenderOffsets;
        public int SenderOffsetsCount => Src.SenderOffsetsCount;
        public bool ValidateSenderNonce => Src.ValidateSenderNonce;
        public bool FastPathSimpleTransfers => Src.FastPathSimpleTransfers;
        public bool WarmupStorageKeys => Src.WarmupStorageKeys;
        public bool WarmupCode => Src.WarmupCode;
        public HashSet<Address>? RepeatedRecipients => Src.RepeatedRecipients;
        public bool UseSenderOffsets => Src.SenderOffsets is not null;

        public BlockState(BlockStateSource src)
        {
            Src = src;
            Env = src.PreWarmer._envPool.Get();
            Scope = Env.Build(src.Parent);
            Scope.TransactionProcessor.SetBlockExecutionContext(new BlockExecutionContext(Block.Header, Spec));
            _senderBaseNonces = Src.SenderOffsets is not null ? new Dictionary<Address, UInt256>() : null;
        }

        public void Dispose()
        {
            Scope.Dispose();
            Src.PreWarmer._envPool.Return(Env);
        }

        public UInt256 GetSenderBaseNonce(Address senderAddress)
        {
            if (_senderBaseNonces is null)
            {
                return Scope.WorldState.GetNonce(senderAddress);
            }

            if (!_senderBaseNonces.TryGetValue(senderAddress, out UInt256 nonce))
            {
                nonce = Scope.WorldState.GetNonce(senderAddress);
                _senderBaseNonces.Add(senderAddress, nonce);
            }

            return nonce;
        }
    }
}
