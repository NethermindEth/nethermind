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
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.TxPool;

namespace Nethermind.Optimism;

public class OptimismBlockProducerEnvFactory : BlockProducerEnvFactory
{
    private readonly ChainSpec _chainSpec;
    private readonly OPSpecHelper _specHelper;
    private readonly OPL1CostHelper _l1CostHelper;

    public OptimismBlockProducerEnvFactory(
        IWorldStateManager worldStateManager,
        ChainSpec chainSpec,
        IBlockTree blockTree,
        ISpecProvider specProvider,
        IBlockValidator blockValidator,
        IRewardCalculatorSource rewardCalculatorSource,
        IReceiptStorage receiptStorage,
        IBlockPreprocessorStep blockPreprocessorStep,
        ITxPool txPool,
        ITransactionComparerProvider transactionComparerProvider,
        IBlocksConfig blocksConfig,
        OPSpecHelper specHelper,
        OPL1CostHelper l1CostHelper,
        ILogManager logManager) : base(worldStateManager,
        blockTree, specProvider, blockValidator,
        rewardCalculatorSource, receiptStorage, blockPreprocessorStep,
        txPool, transactionComparerProvider, blocksConfig, logManager)
    {
        _specHelper = specHelper;
        _l1CostHelper = l1CostHelper;
        _chainSpec = chainSpec;
        TransactionsExecutorFactory = new OptimismTransactionsExecutorFactory(specProvider, logManager);
    }

    protected override ReadOnlyTxProcessingEnv CreateReadonlyTxProcessingEnv(IWorldStateManager worldStateManager,
        ReadOnlyBlockTree readOnlyBlockTree) =>
        new OptimismReadOnlyTxProcessingEnv(worldStateManager, readOnlyBlockTree, _specProvider, _l1CostHelper, _specHelper, _logManager);

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
            new BlockhashStore(_blockTree, specProvider, readOnlyTxProcessingEnv.WorldState),
            logManager,
            _specHelper,
            new Create2DeployerContractRewriter(_specHelper, _specProvider, _blockTree),
            new BlockProductionWithdrawalProcessor(new WithdrawalProcessor(readOnlyTxProcessingEnv.WorldState, logManager)));
    }
}

public class OptimismReadOnlyTxProcessingEnv : ReadOnlyTxProcessingEnv
{
    private readonly IL1CostHelper _l1CostHelper;
    private readonly OPSpecHelper _specHelper;

    public OptimismReadOnlyTxProcessingEnv(
        IWorldStateManager worldStateManager,
        IBlockTree blockTree,
        ISpecProvider specProvider,
        IL1CostHelper l1CostHelper,
        OPSpecHelper specHelper,
        ILogManager logManager
    ) : base(
        worldStateManager,
        blockTree,
        specProvider,
        logManager
    )
    {
        _l1CostHelper = l1CostHelper;
        _specHelper = specHelper;
    }

    protected override TransactionProcessor CreateTransactionProcessor() => new OptimismTransactionProcessor(SpecProvider, StateProvider, Machine, _logManager, _l1CostHelper, _specHelper);
}
