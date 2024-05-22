// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Microsoft.Extensions.ObjectPool;
using Microsoft.FSharp.Collections;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

public interface IBlockCachePreWarmer
{
    void PreWarmCaches(Block suggestedBlock, Hash256 parentStateRoot);
}

public class BlockCachePreWarmer(ReadOnlyTxProcessingEnvFactory envFactory, ILogManager logManager, PreBlockCaches? preBlockCaches = null) : IBlockCachePreWarmer
{
    private readonly ObjectPool<ReadOnlyTxProcessingEnv> _envPool = new DefaultObjectPool<ReadOnlyTxProcessingEnv>(new ReadOnlyTxProcessingEnvPooledObjectPolicy(envFactory), Environment.ProcessorCount);
    private readonly ObjectPool<SystemTransaction> _systemTransactionPool = new DefaultObjectPool<SystemTransaction>(new DefaultPooledObjectPolicy<SystemTransaction>(), Environment.ProcessorCount);
    private readonly ParallelOptions _options = new() { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2) };
    private readonly ILogger _logger = logManager.GetClassLogger<BlockCachePreWarmer>();

    public void PreWarmCaches(Block suggestedBlock, Hash256 parentStateRoot)
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
                Task.Run(() => PreWarmCachesParallel(suggestedBlock, parentStateRoot));
            }
        }
    }

    private void PreWarmCachesParallel(Block suggestedBlock, Hash256 parentStateRoot)
    {
        Parallel.ForEach(suggestedBlock.Transactions, _options, tx =>
        {
            ReadOnlyTxProcessingEnv env = _envPool.Get();
            SystemTransaction systemTransaction = _systemTransactionPool.Get();
            try
            {
                tx.CopyTo(systemTransaction);
                using IReadOnlyTransactionProcessor? transactionProcessor = env.Build(parentStateRoot);
                TransactionResult result = transactionProcessor.Trace(systemTransaction, new BlockExecutionContext(suggestedBlock.Header.Clone()), NullTxTracer.Instance);
                if (_logger.IsDebug) _logger.Debug($"Finished pre-warming cache for tx {tx.Hash} with {result}");
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

    private class ReadOnlyTxProcessingEnvPooledObjectPolicy(ReadOnlyTxProcessingEnvFactory envFactory) : IPooledObjectPolicy<ReadOnlyTxProcessingEnv>
    {
        public ReadOnlyTxProcessingEnv Create() => envFactory.Create();
        public bool Return(ReadOnlyTxProcessingEnv obj) => true;
    }
}
