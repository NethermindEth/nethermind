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
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

public class BlockCachePreWarmer : IBlockCachePreWarmer
{
    private readonly ObjectPool<IReadOnlyTxProcessorSource> _envPool;
    private readonly ObjectPool<SystemTransaction> _systemTransactionPool = new DefaultObjectPool<SystemTransaction>(new DefaultPooledObjectPolicy<SystemTransaction>(), Environment.ProcessorCount);
    private readonly ILogger _logger;
    private Task _currentPreWarm = Task.CompletedTask;
    private Hash256? _parentStateRoot;
    private readonly Action _finishTask;
    private readonly ISpecProvider _specProvider;
    private readonly IWorldState? _targetWorldState;

    public BlockCachePreWarmer(ReadOnlyTxProcessingEnvFactory envFactory, ISpecProvider specProvider, ILogManager logManager, IWorldState? targetWorldState = null)
    {
        _specProvider = specProvider;
        _targetWorldState = targetWorldState;
        _envPool = new DefaultObjectPool<IReadOnlyTxProcessorSource>(new ReadOnlyTxProcessingEnvPooledObjectPolicy(envFactory), Environment.ProcessorCount);
        _logger = logManager.GetClassLogger<BlockCachePreWarmer>();
        _finishTask = () => _currentPreWarm = Task.CompletedTask;
    }

    public Task PreWarmCaches(Block suggestedBlock, Hash256? parentStateRoot, CancellationToken cancellationToken = default)
    {
        if (_currentPreWarm.IsCompleted)
        {
            _logger.Info("Starting pre-warming caches.");
            using CancellationTokenRegistration cancellationAction = cancellationToken.Register(_finishTask);
            if (_targetWorldState is not null)
            {
                bool stateRootChanged = _parentStateRoot != parentStateRoot;
                if (stateRootChanged)
                {
                    _logger.Info("New state root detected.");
                }
                else
                {
                    return _currentPreWarm;
                }

                if (stateRootChanged && _targetWorldState.ClearCache())
                {
                    if (_logger.IsWarn) _logger.Warn("Caches are not empty. Clearing them.");
                }

                if (!IsGenesisBlock(parentStateRoot)
                    && Environment.ProcessorCount > 2
                    && !cancellationToken.IsCancellationRequested)
                {
                    _logger.Info("Pre-warming caches in parallel.");
                    _parentStateRoot = parentStateRoot;
                    // Do not pass cancellation token to the task, we don't want exceptions to be thrown in main processing thread
                    return _currentPreWarm = Task.Run(() => PreWarmCachesParallel(suggestedBlock, parentStateRoot!, cancellationToken));
                }
            }
        }
        else
        {
            _logger.Info("Pre-warming caches already in progress.");
        }

        return _currentPreWarm;
    }

    // Parent state root is null for genesis block
    private bool IsGenesisBlock(Hash256? parentStateRoot) => parentStateRoot is null;

    public void ClearCaches() => _targetWorldState?.ClearCache();

    private void PreWarmCachesParallel(Block suggestedBlock, Hash256 parentStateRoot, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return;

        try
        {
            if (_logger.IsDebug) _logger.Debug($"Started pre-warming caches for block {suggestedBlock.Number}.");

            ParallelOptions parallelOptions = new() { MaxDegreeOfParallelism = Math.Max(1, RuntimeInformation.PhysicalCoreCount - 2), CancellationToken = cancellationToken };
            IReleaseSpec spec = _specProvider.GetSpec(suggestedBlock.Header);

            WarmupTransactions(parallelOptions, spec, suggestedBlock, parentStateRoot);
            WarmupWithdrawals(parallelOptions, spec, suggestedBlock, parentStateRoot);

            if (_logger.IsDebug) _logger.Debug($"Finished pre-warming caches for block {suggestedBlock.Number}.");
        }
        catch (OperationCanceledException)
        {
            if (_logger.IsDebug) _logger.Debug($"Pre-warming caches cancelled for block {suggestedBlock.Number}.");
        }

        void WarmupWithdrawals(ParallelOptions parallelOptions, IReleaseSpec spec, Block block, Hash256 stateRoot)
        {
            if (parallelOptions.CancellationToken.IsCancellationRequested) return;
            if (spec.WithdrawalsEnabled && block.Withdrawals is not null)
            {
                Parallel.For(0, block.Withdrawals.Length, parallelOptions,
                    i =>
                    {
                        IReadOnlyTxProcessorSource env = _envPool.Get();
                        try
                        {
                            using IReadOnlyTxProcessingScope scope = env.Build(stateRoot);
                            scope.WorldState.WarmUp(block.Withdrawals[i].Address);
                        }
                        catch (Exception ex)
                        {
                            if (_logger.IsDebug) _logger.Error($"Error pre-warming withdrawal {i}", ex);
                        }
                        finally
                        {
                            _envPool.Return(env);
                        }
                    });
            }
        }

        void WarmupTransactions(ParallelOptions parallelOptions, IReleaseSpec spec, Block block, Hash256 stateRoot)
        {
            if (parallelOptions.CancellationToken.IsCancellationRequested) return;
            Parallel.For(0, block.Transactions.Length, parallelOptions, i =>
            {
                // If the transaction has already been processed or being processed, exit early
                if (block.TransactionProcessed >= i) return;

                using ThreadExtensions.Disposable handle = Thread.CurrentThread.BoostPriority();
                Transaction tx = block.Transactions[i];
                IReadOnlyTxProcessorSource env = _envPool.Get();
                SystemTransaction systemTransaction = _systemTransactionPool.Get();
                try
                {
                    tx.CopyTo(systemTransaction);
                    using IReadOnlyTxProcessingScope scope = env.Build(stateRoot);
                    if (spec.UseTxAccessLists)
                    {
                        scope.WorldState.WarmUp(tx.AccessList); // eip-2930
                    }
                    TransactionResult result = scope.TransactionProcessor.Trace(systemTransaction, new BlockExecutionContext(block.Header.Clone()), NullTxTracer.Instance);
                    if (_logger.IsTrace) _logger.Trace($"Finished pre-warming cache for tx {tx.Hash} with {result}");
                }
                catch (Exception ex)
                {
                    if (_logger.IsDebug) _logger.Error($"Error pre-warming cache {tx.Hash}", ex);
                }
                finally
                {
                    _systemTransactionPool.Return(systemTransaction);
                    _envPool.Return(env);
                }
            });
        }
    }

    private class ReadOnlyTxProcessingEnvPooledObjectPolicy(ReadOnlyTxProcessingEnvFactory envFactory) : IPooledObjectPolicy<IReadOnlyTxProcessorSource>
    {
        public IReadOnlyTxProcessorSource Create() => envFactory.Create();
        public bool Return(IReadOnlyTxProcessorSource obj) => true;
    }
}
