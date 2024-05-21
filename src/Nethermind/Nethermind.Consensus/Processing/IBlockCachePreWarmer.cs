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

namespace Nethermind.Consensus.Processing;

public interface IBlockCachePreWarmer
{
    void PreWarmCaches(Block suggestedBlock, Hash256 parentStateRoot);
}

public class BlockCachePreWarmer(ReadOnlyTxProcessingEnvFactory envFactory) : IBlockCachePreWarmer
{
    private readonly ObjectPool<ReadOnlyTxProcessingEnv> _envPool = new DefaultObjectPool<ReadOnlyTxProcessingEnv>(new ReadOnlyTxProcessingEnvPooledObjectPolicy(envFactory), Environment.ProcessorCount);

    public void PreWarmCaches(Block suggestedBlock, Hash256 parentStateRoot)
    {
        Parallel.ForEach(suggestedBlock.Transactions, tx =>
        {
            ReadOnlyTxProcessingEnv env = _envPool.Get();
            try
            {
                using IReadOnlyTransactionProcessor? transactionProcessor = env.Build(parentStateRoot);
                transactionProcessor.Trace(tx, new BlockExecutionContext(suggestedBlock.Header.Clone()), NullTxTracer.Instance);
            }
            finally
            {
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
