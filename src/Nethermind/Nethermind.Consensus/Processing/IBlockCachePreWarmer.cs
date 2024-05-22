// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
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
    Task PreWarmCaches(Block suggestedBlock, Hash256 parentStateRoot, CancellationToken cancellationToken = default);
    void CompareCaches(Block block);
}

public class BlockCachePreWarmer(ReadOnlyTxProcessingEnvFactory envFactory, ILogManager logManager, PreBlockCaches? preBlockCaches = null, PreBlockCaches? mainCaches = null) : IBlockCachePreWarmer
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

    public void CompareCaches(Block block)
    {
        if (preBlockCaches is not null && mainCaches is not null)
        {
            int missingAccounts = 0;
            int differentAccounts = 0;
            Dictionary<AddressAsKey, (Account, Account)> accountDifferences = new();
            foreach (KeyValuePair<AddressAsKey, Account> account in mainCaches.StateCache)
            {
                if (preBlockCaches.StateCache.TryGetValue(account.Key, out Account? preBlockAccount))
                {
                    if (!account.Value.Equals(preBlockAccount))
                    {
                        differentAccounts++;
                        accountDifferences[account.Key] = (account.Value, preBlockAccount);
                    }
                }
                else
                {
                    missingAccounts++;
                }
            }

            int missingSorage = 0;
            int differentStorage = 0;
            Dictionary<StorageCell, (byte[], byte[])> storageDifferences = new();
            foreach (KeyValuePair<StorageCell,byte[]> storage in mainCaches.StorageCache)
            {
                if (preBlockCaches.StorageCache.TryGetValue(storage.Key, out byte[]? preBlockStorage))
                {
                    if (!storage.Value.SequenceEqual(preBlockStorage))
                    {
                        differentStorage++;
                        storageDifferences[storage.Key] = (storage.Value, preBlockStorage);
                    }
                }
                else
                {
                    missingSorage++;
                }
            }

            _logger.Warn($"For block {block.Number} found {differentAccounts} different Accounts, {missingAccounts} missing Accounts, {differentStorage} different Storages, {missingSorage} missing Storages.");
        }

        mainCaches?.Clear();
    }

    private void PreWarmCachesParallel(Block suggestedBlock, Hash256 parentStateRoot, CancellationToken cancellationToken)
    {
        try
        {
            ParallelOptions parallelOptions = new() { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2), CancellationToken = cancellationToken };
            Parallel.ForEach(suggestedBlock.Transactions, parallelOptions, tx =>
            {
                // ReadOnlyTxProcessingEnv env = _envPool.Get();
                //SystemTransaction systemTransaction = _systemTransactionPool.Get();
                SystemTransaction systemTransaction = new();
                ReadOnlyTxProcessingEnv env = envFactory.Create();
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
                    //_systemTransactionPool.Return(systemTransaction);
                    // env.Reset();
                    // _envPool.Return(env);
                }
            });
            _logger.Info($"Finished pre-warming caches for block {suggestedBlock.Number}.");
        }
        catch (OperationCanceledException)
        {
            _logger.Info($"Pre-warming caches cancelled for block {suggestedBlock.Number}.");
        }
    }

    private class ReadOnlyTxProcessingEnvPooledObjectPolicy(ReadOnlyTxProcessingEnvFactory envFactory) : IPooledObjectPolicy<ReadOnlyTxProcessingEnv>
    {
        public ReadOnlyTxProcessingEnv Create() => envFactory.Create();
        public bool Return(ReadOnlyTxProcessingEnv obj) => true;
    }
}
