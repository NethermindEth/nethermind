
// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;

namespace Nethermind.Taiko;

public class TaikoBlockProducerEnvFactory(
    IWorldStateManager worldStateManager,
    IBlockTree blockTree,
    ISpecProvider specProvider,
    IBlockValidator blockValidator,
    IRewardCalculatorSource rewardCalculatorSource,
    IReceiptStorage receiptStorage,
    IBlockPreprocessorStep blockPreprocessorStep,
    ITxPool txPool,
    ITransactionComparerProvider transactionComparerProvider,
    IBlocksConfig blocksConfig,
    ILogManager logManager) : BlockProducerEnvFactory(
        worldStateManager,
        blockTree,
        specProvider,
        blockValidator,
        rewardCalculatorSource,
        receiptStorage,
        blockPreprocessorStep,
        txPool,
        transactionComparerProvider,
        blocksConfig,
        logManager)
{
    private readonly IBlockTransactionsExecutorFactory _blockInvalidTxExecutorFactory = new BlockInvalidTxExecutorFactory();

    protected override ReadOnlyTxProcessingEnv CreateReadonlyTxProcessingEnv(
        IWorldStateManager worldStateManager, ReadOnlyBlockTree readOnlyBlockTree) =>
        new TaikoReadOnlyTxProcessingEnv(worldStateManager, readOnlyBlockTree, _specProvider, _logManager);

    public BlockProducerEnv CreateWithInvalidTxExecutor(ITxSource? additionalTxSource = null)
    {
        IBlockTransactionsExecutorFactory originalTransactionsExecutorFactory = TransactionsExecutorFactory;
        TransactionsExecutorFactory = _blockInvalidTxExecutorFactory;
        BlockProducerEnv env = base.Create(additionalTxSource);
        TransactionsExecutorFactory = originalTransactionsExecutorFactory;
        return env;
    }
}
