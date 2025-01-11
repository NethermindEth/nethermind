// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Threading;
using Nethermind.Core.Cpu;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Collections;

namespace Nethermind.Consensus.Processing;

public sealed class BlockCachePreWarmer(ReadOnlyTxProcessingEnvFactory envFactory, ISpecProvider specProvider, ILogManager logManager, PreBlockCaches? preBlockCaches = null) : IBlockCachePreWarmer
{
    private readonly ObjectPool<IReadOnlyTxProcessorSource> _envPool = new DefaultObjectPool<IReadOnlyTxProcessorSource>(new ReadOnlyTxProcessingEnvPooledObjectPolicy(envFactory), Environment.ProcessorCount * 4);
    private readonly ILogger _logger = logManager.GetClassLogger<BlockCachePreWarmer>();

    public Task PreWarmCaches(Block suggestedBlock, Hash256? parentStateRoot, IReleaseSpec spec, CancellationToken cancellationToken = default, params ReadOnlySpan<IHasAccessList> systemAccessLists)
    {
        if (preBlockCaches is not null)
        {
            CacheType result = preBlockCaches.ClearCaches();
            if (result != default)
            {
                if (_logger.IsWarn) _logger.Warn($"Caches {result} are not empty. Clearing them.");
            }

            var physicalCoreCount = RuntimeInformation.PhysicalCoreCount;
            if (!IsGenesisBlock(parentStateRoot) && physicalCoreCount > 2 && !cancellationToken.IsCancellationRequested)
            {
                ParallelOptions parallelOptions = new() { MaxDegreeOfParallelism = physicalCoreCount - 1, CancellationToken = cancellationToken };

                // Run address warmer ahead of transactions warmer, but queue to ThreadPool so it doesn't block the txs
                var addressWarmer = new AddressWarmer(parallelOptions, suggestedBlock, parentStateRoot, spec, systemAccessLists, this);
                ThreadPool.UnsafeQueueUserWorkItem(addressWarmer, preferLocal: false);
                // Do not pass cancellation token to the task, we don't want exceptions to be thrown in main processing thread
                return Task.Run(() => PreWarmCachesParallel(suggestedBlock, parentStateRoot, parallelOptions, addressWarmer, cancellationToken));
            }
        }

        return Task.CompletedTask;
    }

    // Parent state root is null for genesis block
    private static bool IsGenesisBlock(Hash256? parentStateRoot) => parentStateRoot is null;

    public CacheType ClearCaches()
    {
        if (_logger.IsDebug) _logger.Debug("Clearing caches");
        CacheType cachesCleared = preBlockCaches?.ClearCaches() ?? default;
        if (_logger.IsDebug) _logger.Debug($"Cleared caches: {cachesCleared}");

        return cachesCleared;
    }

    private void PreWarmCachesParallel(Block suggestedBlock, Hash256 parentStateRoot, ParallelOptions parallelOptions, AddressWarmer addressWarmer, CancellationToken cancellationToken)
    {
        try
        {
            if (cancellationToken.IsCancellationRequested) return;

            if (_logger.IsDebug) _logger.Debug($"Started pre-warming caches for block {suggestedBlock.Number}.");

            IReleaseSpec spec = specProvider.GetSpec(suggestedBlock.Header);
            WarmupTransactions(parallelOptions, spec, suggestedBlock, parentStateRoot);
            WarmupWithdrawals(parallelOptions, spec, suggestedBlock, parentStateRoot);

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

    private void WarmupWithdrawals(ParallelOptions parallelOptions, IReleaseSpec spec, Block block, Hash256 stateRoot)
    {
        if (parallelOptions.CancellationToken.IsCancellationRequested) return;

        try
        {
            if (spec.WithdrawalsEnabled && block.Withdrawals is not null)
            {
                ParallelUnbalancedWork.For(0, block.Withdrawals.Length, parallelOptions, (envPool: _envPool, block, stateRoot),
                    static (i, state) =>
                    {
                        IReadOnlyTxProcessorSource env = state.envPool.Get();
                        try
                        {
                            using IReadOnlyTxProcessingScope scope = env.Build(state.stateRoot);
                            scope.WorldState.WarmUp(state.block.Withdrawals[i].Address);
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

    private void WarmupTransactions(ParallelOptions parallelOptions, IReleaseSpec spec, Block block, Hash256 stateRoot)
    {
        if (parallelOptions.CancellationToken.IsCancellationRequested) return;

        try
        {
            ParallelUnbalancedWork.For<BlockState>(0, block.Transactions.Length, parallelOptions, new(this, block, stateRoot, spec), static (i, state) =>
            {
                IReadOnlyTxProcessorSource env = state.PreWarmer._envPool.Get();
                Transaction? tx = null;
                try
                {
                    // If the transaction has already been processed or being processed, exit early
                    if (state.Block.TransactionProcessed > i) return state;

                    tx = state.Block.Transactions[i];
                    using IReadOnlyTxProcessingScope scope = env.Build(state.StateRoot);

                    Address senderAddress = tx.SenderAddress!;
                    if (!scope.WorldState.AccountExists(senderAddress))
                    {
                        scope.WorldState.CreateAccountIfNotExists(senderAddress, UInt256.Zero);
                    }

                    UInt256 nonceDelta = UInt256.Zero;
                    for (int prev = 0; prev < i; prev++)
                    {
                        if (senderAddress == state.Block.Transactions[prev].SenderAddress)
                        {
                            nonceDelta++;
                        }
                    }

                    if (!nonceDelta.IsZero)
                    {
                        scope.WorldState.IncrementNonce(senderAddress, nonceDelta);
                    }

                    if (state.Spec.UseTxAccessLists)
                    {
                        scope.WorldState.WarmUp(tx.AccessList); // eip-2930
                    }
                    TransactionResult result = scope.TransactionProcessor.Warmup(tx, new BlockExecutionContext(state.BlockHeader), NullTxTracer.Instance);
                    if (state.PreWarmer._logger.IsTrace) state.PreWarmer._logger.Trace($"Finished pre-warming cache for tx[{i}] {tx.Hash} with {result}");
                }
                catch (Exception ex) when (ex is EvmException or OverflowException)
                {
                    // Ignore, regular tx processing exceptions
                }
                catch (Exception ex)
                {
                    if (state.PreWarmer._logger.IsDebug) state.PreWarmer._logger.Error($"DEBUG/ERROR Error pre-warming cache {tx?.Hash}", ex);
                }
                finally
                {
                    state.PreWarmer._envPool.Return(env);
                }

                return state;
            });
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

    private class AddressWarmer(ParallelOptions parallelOptions, Block block, Hash256 stateRoot, IReleaseSpec spec, ReadOnlySpan<IHasAccessList> systemAccessLists, BlockCachePreWarmer preWarmer)
        : IThreadPoolWorkItem
    {
        private readonly Block Block = block;
        private readonly Hash256 StateRoot = stateRoot;
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
            if (parallelOptions.CancellationToken.IsCancellationRequested) return;

            ObjectPool<IReadOnlyTxProcessorSource> envPool = PreWarmer._envPool;
            try
            {
                if (SystemTxAccessLists is not null)
                {
                    var env = envPool.Get();
                    try
                    {
                        using IReadOnlyTxProcessingScope scope = env.Build(StateRoot);

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

                AddressWarmingState baseState = new(envPool, block, StateRoot);

                ParallelUnbalancedWork.For(
                    0,
                    block.Transactions.Length,
                    parallelOptions,
                    baseState.InitThreadState,
                static (i, state) =>
                {
                    Transaction tx = state.Block.Transactions[i];
                    Address? sender = tx.SenderAddress;

                    if (sender is not null)
                    {
                        state.Scope.WorldState.WarmUp(sender);
                    }
                    Address to = tx.To;
                    if (to is not null)
                    {
                        state.Scope.WorldState.WarmUp(to);
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

    private readonly struct AddressWarmingState(ObjectPool<IReadOnlyTxProcessorSource> envPool, Block block, Hash256 stateRoot) : IDisposable
    {
        public static Action<AddressWarmingState> FinallyAction { get; } = DisposeThreadState;

        public readonly ObjectPool<IReadOnlyTxProcessorSource> EnvPool = envPool;
        public readonly Block Block = block;
        public readonly Hash256 StateRoot = stateRoot;
        public readonly IReadOnlyTxProcessorSource? Env;
        public readonly IReadOnlyTxProcessingScope? Scope;

        public AddressWarmingState(ObjectPool<IReadOnlyTxProcessorSource> envPool, Block block, Hash256 stateRoot, IReadOnlyTxProcessorSource env, IReadOnlyTxProcessingScope scope) : this(envPool, block, stateRoot)
        {
            Env = env;
            Scope = scope;
        }

        public AddressWarmingState InitThreadState()
        {
            IReadOnlyTxProcessorSource env = EnvPool.Get();
            return new(EnvPool, Block, StateRoot, env, scope: env.Build(StateRoot));
        }

        public void Dispose()
        {
            Scope.Dispose();
            EnvPool.Return(Env);
        }

        private static void DisposeThreadState(AddressWarmingState state) => state.Dispose();
    }

    private class ReadOnlyTxProcessingEnvPooledObjectPolicy(ReadOnlyTxProcessingEnvFactory envFactory) : IPooledObjectPolicy<IReadOnlyTxProcessorSource>
    {
        public IReadOnlyTxProcessorSource Create() => envFactory.Create();
        public bool Return(IReadOnlyTxProcessorSource obj) => true;
    }

    private readonly struct BlockState(BlockCachePreWarmer preWarmer, Block block, Hash256 stateRoot, IReleaseSpec spec)
    {
        public readonly BlockCachePreWarmer PreWarmer = preWarmer;
        public readonly Block Block = block;
        public readonly Hash256 StateRoot = stateRoot;
        public readonly IReleaseSpec Spec = spec;
        public readonly BlockHeader BlockHeader => Block.Header;
    }
}

