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

public class BlockCachePreWarmer(ReadOnlyTxProcessingEnvFactory envFactory, ISpecProvider specProvider, ILogManager logManager, IWorldState? targetWorldState = null) : IBlockCachePreWarmer
{
    private readonly ObjectPool<IReadOnlyTxProcessorSource> _envPool = new DefaultObjectPool<IReadOnlyTxProcessorSource>(new ReadOnlyTxProcessingEnvPooledObjectPolicy(envFactory), Environment.ProcessorCount);
    private readonly ObjectPool<SystemTransaction> _systemTransactionPool = new DefaultObjectPool<SystemTransaction>(new DefaultPooledObjectPolicy<SystemTransaction>(), Environment.ProcessorCount);
    private readonly ILogger _logger = logManager.GetClassLogger<BlockCachePreWarmer>();

    public Task PreWarmCaches(Block suggestedBlock, Hash256? parentStateRoot, CancellationToken cancellationToken = default)
    {
        if (targetWorldState is not null)
        {
            if (targetWorldState.ClearCache())
            {
                if (_logger.IsWarn) _logger.Warn("Cashes are not empty. Clearing them.");
            }

            if (!IsGenesisBlock(parentStateRoot) && Environment.ProcessorCount > 2 && !cancellationToken.IsCancellationRequested)
            {
                // Do not pass cancellation token to the task, we don't want exceptions to be thrown in main processing thread
                return Task.Run(() => PreWarmCachesParallel(suggestedBlock, parentStateRoot, cancellationToken));
            }
        }

        return Task.CompletedTask;
    }

    // Parent state root is null for genesis block
    private bool IsGenesisBlock(Hash256? parentStateRoot) => parentStateRoot is null;

    public void ClearCaches() => targetWorldState?.ClearCache();

    private void PreWarmCachesParallel(Block suggestedBlock, Hash256 parentStateRoot, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return;

        try
        {
            ParallelOptions parallelOptions = new() { MaxDegreeOfParallelism = Math.Max(1, RuntimeInformation.PhysicalCoreCount - 2), CancellationToken = cancellationToken };
            IReleaseSpec spec = specProvider.GetSpec(suggestedBlock.Header);

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
                        using IReadOnlyTxProcessingScope scope = env.Build(stateRoot);
                        try
                        {
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
