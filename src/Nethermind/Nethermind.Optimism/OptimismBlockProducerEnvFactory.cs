// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;

namespace Nethermind.Optimism;

public class OptimismBlockProducerEnvFactory(
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
    OptimismSpecHelper specHelper,
    OPL1CostHelper l1CostHelper,
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
    protected override ReadOnlyTxProcessingEnv CreateReadonlyTxProcessingEnv(IWorldStateManager worldStateManager,
        ReadOnlyBlockTree readOnlyBlockTree) =>
        new OptimismReadOnlyTxProcessingEnv(worldStateManager, readOnlyBlockTree, _specProvider, _logManager, l1CostHelper, specHelper);

    protected override ITxSource CreateTxSourceForProducer(ITxSource? additionalTxSource,
        ReadOnlyTxProcessingEnv processingEnv,
        ITxPool txPool, IBlocksConfig blocksConfig, ITransactionComparerProvider transactionComparerProvider,
        ILogManager logManager)
    {
        ITxSource baseTxSource = base.CreateTxSourceForProducer(additionalTxSource, processingEnv, txPool, blocksConfig,
            transactionComparerProvider, logManager);

        return new OptimismTxPoolTxSource(baseTxSource);
    }

    protected override BlockProcessor CreateBlockProcessor(
        ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv,
        ISpecProvider specProvider,
        IBlockValidator blockValidator,
        IRewardCalculatorSource rewardCalculatorSource,
        IReceiptStorage receiptStorage,
        ILogManager logManager,
        IBlocksConfig blocksConfig)
    {
        return new OptimismBlockProcessor(specProvider,
            blockValidator,
            rewardCalculatorSource.Get(readOnlyTxProcessingEnv.TransactionProcessor),
            TransactionsExecutorFactory.Create(readOnlyTxProcessingEnv),
            readOnlyTxProcessingEnv.StateProvider,
            receiptStorage,
            new BlockhashStore(_blockTree, specProvider, readOnlyTxProcessingEnv.StateProvider),
            logManager,
            specHelper,
            new Create2DeployerContractRewriter(specHelper, _specProvider, _blockTree),
            new BlockProductionWithdrawalProcessor(new WithdrawalProcessor(readOnlyTxProcessingEnv.StateProvider, logManager)));
    }
}
