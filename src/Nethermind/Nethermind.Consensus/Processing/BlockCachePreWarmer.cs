// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

public class BlockCachePreWarmer(ReadOnlyTxProcessingEnvFactory envFactory, ILogManager logManager, PreBlockCaches? preBlockCaches = null) : IBlockCachePreWarmer
{
    private readonly ObjectPool<ReadOnlyTxProcessingEnv> _envPool = new DefaultObjectPool<ReadOnlyTxProcessingEnv>(new ReadOnlyTxProcessingEnvPooledObjectPolicy(envFactory), Environment.ProcessorCount);
    private readonly ObjectPool<SystemTransaction> _systemTransactionPool = new DefaultObjectPool<SystemTransaction>(new DefaultPooledObjectPolicy<SystemTransaction>(), Environment.ProcessorCount);
    private readonly ILogger _logger = logManager.GetClassLogger<BlockCachePreWarmer>();

    public Task PreWarmCaches(Block suggestedBlock, Hash256 parentStateRoot, CancellationToken cancellationToken = default)
    {
        if (preBlockCaches is not null)
        {
            if (preBlockCaches.StateCache.Count > 0 || preBlockCaches.StorageCache.Count > 0)
            {
                _logger.Warn("Cashes are not empty. Clearing them.");
                preBlockCaches.Clear();
            }

            if (Environment.ProcessorCount > 2)
            {
                return Task.Run(() => PreWarmCachesParallel(suggestedBlock, parentStateRoot, cancellationToken), cancellationToken);
            }

        }

        return Task.CompletedTask;
    }

    private void PreWarmCachesParallel(Block suggestedBlock, Hash256 parentStateRoot, CancellationToken cancellationToken)
    {
        try
        {
            ParallelOptions parallelOptions = new() { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2), CancellationToken = cancellationToken };
            Parallel.ForEach(suggestedBlock.Transactions, parallelOptions, tx =>
            {
                using ThreadExtensions.Disposable handle = Thread.CurrentThread.BoostPriority();
                ReadOnlyTxProcessingEnv env = _envPool.Get();
                SystemTransaction systemTransaction = _systemTransactionPool.Get();
                try
                {
                    tx.CopyTo(systemTransaction);
                    using IReadOnlyTransactionProcessor? transactionProcessor = env.Build(parentStateRoot);
                    TransactionResult result = transactionProcessor.Trace(systemTransaction, new BlockExecutionContext(suggestedBlock.Header.Clone()), NullTxTracer.Instance);
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
            if (_logger.IsDebug) _logger.Debug($"Finished pre-warming caches for block {suggestedBlock.Number}.");
        }
        catch (OperationCanceledException)
        {
            if (_logger.IsDebug) _logger.Debug($"Pre-warming caches cancelled for block {suggestedBlock.Number}.");
        }
    }

    private class ReadOnlyTxProcessingEnvPooledObjectPolicy(ReadOnlyTxProcessingEnvFactory envFactory) : IPooledObjectPolicy<ReadOnlyTxProcessingEnv>
    {
        public ReadOnlyTxProcessingEnv Create() => envFactory.Create();
        public bool Return(ReadOnlyTxProcessingEnv obj) => true;
    }
}
