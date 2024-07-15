using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;

namespace Nethermind.Taiko;

public class BlockInvalidTxExecutorFactory(IEthereumEcdsa ecdsa) : IBlockTransactionsExecutorFactory
{
    public IBlockProcessor.IBlockTransactionsExecutor Create(IReadOnlyTxProcessingScope readOnlyTxProcessingEnv) =>
        new BlockInvalidTxExecutor(
            new BuildUpTransactionProcessorAdapter(readOnlyTxProcessingEnv.TransactionProcessor),
            readOnlyTxProcessingEnv.WorldState, ecdsa);
}
