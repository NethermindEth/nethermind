// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Taiko.BlockTransactionExecutors;

public class BlockInvalidTxExecutorFactory : IBlockTransactionsExecutorFactory
{
    public IBlockProcessor.IBlockTransactionsExecutor Create(IReadOnlyTxProcessingScope readOnlyTxProcessingEnv) =>
        new BlockInvalidTxExecutor(
            new BuildUpTransactionProcessorAdapter(readOnlyTxProcessingEnv.TransactionProcessor),
            readOnlyTxProcessingEnv.WorldState);
}
