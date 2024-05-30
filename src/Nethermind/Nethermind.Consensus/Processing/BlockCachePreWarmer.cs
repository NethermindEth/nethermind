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
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

public class BlockCachePreWarmer(ReadOnlyTxProcessingEnvFactory envFactory, ISpecProvider specProvider, ILogManager logManager, PreBlockCaches? preBlockCaches = null) : IBlockCachePreWarmer
{
    private readonly ObjectPool<ReadOnlyTxProcessingEnv> _envPool = new DefaultObjectPool<ReadOnlyTxProcessingEnv>(new ReadOnlyTxProcessingEnvPooledObjectPolicy(envFactory), Environment.ProcessorCount);
    private readonly ObjectPool<SystemTransaction> _systemTransactionPool = new DefaultObjectPool<SystemTransaction>(new DefaultPooledObjectPolicy<SystemTransaction>(), Environment.ProcessorCount);
    private readonly ILogger _logger = logManager.GetClassLogger<BlockCachePreWarmer>();

    public Task PreWarmCaches(Block suggestedBlock, Hash256? parentStateRoot, CancellationToken cancellationToken = default)
    {
        if (preBlockCaches is not null)
        {
            if (preBlockCaches.IsDirty)
            {
                if (_logger.IsWarn) _logger.Warn("Cashes are not empty. Clearing them.");
                preBlockCaches.Clear();
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

    public void ClearCaches() => preBlockCaches?.Clear();

    private void PreWarmCachesParallel(Block suggestedBlock, Hash256 parentStateRoot, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return;

        try
        {
            ParallelOptions parallelOptions = new() { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2), CancellationToken = cancellationToken };
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
            if (spec.WithdrawalsEnabled && block.Withdrawals is not null)
            {
                ReadOnlyTxProcessingEnv env = _envPool.Get();
                try
                {
                    using IReadOnlyTransactionProcessor transactionProcessor = env.Build(stateRoot);
                    Parallel.For(0, block.Withdrawals.Length, parallelOptions,
                        i => env.StateProvider.WarmUp(block.Withdrawals[i].Address)
                    );
                }
                finally
                {
                    env.Reset();
                    _envPool.Return(env);
                }
            }
        }

        void WarmupTransactions(ParallelOptions parallelOptions, IReleaseSpec spec, Block block, Hash256 stateRoot)
        {
            Parallel.For(0, block.Transactions.Length, parallelOptions, i =>
            {
                using ThreadExtensions.Disposable handle = Thread.CurrentThread.BoostPriority();
                Transaction tx = block.Transactions[i];
                ReadOnlyTxProcessingEnv env = _envPool.Get();
                SystemTransaction systemTransaction = _systemTransactionPool.Get();
                try
                {
                    tx.CopyTo(systemTransaction);
                    using IReadOnlyTransactionProcessor transactionProcessor = env.Build(stateRoot);
                    if (spec.UseTxAccessLists)
                    {
                        env.StateProvider.WarmUp(tx.AccessList); // eip-2930
                    }
                    TransactionResult result = transactionProcessor.Trace(systemTransaction, new BlockExecutionContext(block.Header.Clone()), NullTxTracer.Instance);
                    if (_logger.IsTrace) _logger.Trace($"Finished pre-warming cache for tx {tx.Hash} with {result}");
                }
                catch (Exception ex)
                {
                    if (_logger.IsDebug) _logger.Error($"Error pre-warming cache {tx.Hash}", ex);
                }
                finally
                {
                    _systemTransactionPool.Return(systemTransaction);
                    env.Reset();
                    _envPool.Return(env);
                }
            });
        }
    }

    private class ReadOnlyTxProcessingEnvPooledObjectPolicy(ReadOnlyTxProcessingEnvFactory envFactory) : IPooledObjectPolicy<ReadOnlyTxProcessingEnv>
    {
        public ReadOnlyTxProcessingEnv Create() => envFactory.Create();
        public bool Return(ReadOnlyTxProcessingEnv obj) => true;
    }
}
