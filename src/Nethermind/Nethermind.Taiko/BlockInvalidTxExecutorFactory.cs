using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;

namespace Nethermind.Taiko;

public class BlockInvalidTxExecutorFactory : IBlockTransactionsExecutorFactory
{
    public IBlockProcessor.IBlockTransactionsExecutor Create(IReadOnlyTxProcessingScope readOnlyTxProcessingEnv) =>
        new BlockInvalidTxExecutor(
            new BuildUpTransactionProcessorAdapter(readOnlyTxProcessingEnv.TransactionProcessor),
            readOnlyTxProcessingEnv.WorldState);
}
