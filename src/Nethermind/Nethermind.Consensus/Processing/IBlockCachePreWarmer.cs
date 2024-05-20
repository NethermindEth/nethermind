// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Microsoft.FSharp.Collections;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

public interface IBlockCachePreWarmer
{
    void PreWarmCaches(Block suggestedBlock, Hash256 parentStateRoot);
}

public class BlockCachePreWarmer(ReadOnlyTxProcessingEnvFactory envFactory) : IBlockCachePreWarmer
{
    public void PreWarmCaches(Block suggestedBlock, Hash256 parentStateRoot)
    {
        ReadOnlyTxProcessingEnv env = envFactory.Create();
        Parallel.ForEach(suggestedBlock.Transactions, tx =>
        {
            using IReadOnlyTransactionProcessor? transactionProcessor = env.Build(parentStateRoot);
            transactionProcessor.Execute(tx, new BlockExecutionContext(suggestedBlock.Header.Clone()), NullTxTracer.Instance);
        });

    }
}
