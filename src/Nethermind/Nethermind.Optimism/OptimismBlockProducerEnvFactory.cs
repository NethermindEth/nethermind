// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;

namespace Nethermind.Optimism;

public class OptimismBlockProducerEnvFactory : BlockProducerEnvFactory
{
    private readonly IOptimismSpecHelper _specHelper;
    private readonly ICostHelper _l1CostHelper;

    public OptimismBlockProducerEnvFactory(
        IWorldStateManager worldStateManager,
        IReadOnlyTxProcessingEnvFactory txProcessingEnvFactory,
        IBlockTree blockTree,
        ISpecProvider specProvider,
        IBlockValidator blockValidator,
        IRewardCalculatorSource rewardCalculatorSource,
        IReceiptStorage receiptStorage,
        IBlockPreprocessorStep blockPreprocessorStep,
        ITxPool txPool,
        ITransactionComparerProvider transactionComparerProvider,
        IBlocksConfig blocksConfig,
        IOptimismSpecHelper specHelper,
        ICostHelper l1CostHelper,
        ILogManager logManager) : base(
            worldStateManager,
            txProcessingEnvFactory,
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
        _specHelper = specHelper;
        _l1CostHelper = l1CostHelper;
        TransactionsExecutorFactory = new OptimismTransactionsExecutorFactory(specProvider, blocksConfig.BlockProductionMaxTxKilobytes, logManager);
    }

    protected override ITxSource CreateTxSourceForProducer(ITxSource? additionalTxSource,
        IReadOnlyTxProcessorSource processingEnv,
        ITxPool txPool, IBlocksConfig blocksConfig, ITransactionComparerProvider transactionComparerProvider,
        ILogManager logManager)
    {
        ITxSource baseTxSource = base.CreateTxSourceForProducer(additionalTxSource, processingEnv, txPool, blocksConfig,
            transactionComparerProvider, logManager);

        return new OptimismTxPoolTxSource(baseTxSource);
    }

    protected override BlockProcessor CreateBlockProcessor(
        IReadOnlyTxProcessingScope readOnlyTxProcessingEnv,
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
            readOnlyTxProcessingEnv.WorldState,
            receiptStorage,
            new BlockhashStore(specProvider, readOnlyTxProcessingEnv.WorldState),
            new BeaconBlockRootHandler(readOnlyTxProcessingEnv.TransactionProcessor, readOnlyTxProcessingEnv.WorldState),
            logManager,
            _specHelper,
            new Create2DeployerContractRewriter(_specHelper, _specProvider, _blockTree),
            new BlockProductionWithdrawalProcessor(new WithdrawalProcessor(readOnlyTxProcessingEnv.WorldState, logManager)),
            new ExecutionRequestsProcessor(readOnlyTxProcessingEnv.TransactionProcessor));
    }
}
