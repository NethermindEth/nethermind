// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Requests;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Test.Synchronization;

public class MergeTestBlockProducerEnvFactory(
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
    ILogManager logManager,
    IConsensusRequestsProcessor? consensusRequestsProcessor = null)
    : BlockProducerEnvFactory(worldStateManager, blockTree, specProvider, blockValidator, rewardCalculatorSource,
        receiptStorage, blockPreprocessorStep, txPool, transactionComparerProvider, blocksConfig, logManager)
{
    protected override BlockProcessor CreateBlockProcessor(ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv, ISpecProvider specProvider,
        IBlockValidator blockValidator, IRewardCalculatorSource rewardCalculatorSource, IReceiptStorage receiptStorage,
        ILogManager logManager, IBlocksConfig blocksConfig)
    {
        return new(specProvider,
            blockValidator,
            rewardCalculatorSource.Get(readOnlyTxProcessingEnv.TransactionProcessor),
            TransactionsExecutorFactory.Create(readOnlyTxProcessingEnv),
            readOnlyTxProcessingEnv.StateProvider,
            receiptStorage,
            NullWitnessCollector.Instance,
            logManager,
            new BlockProductionWithdrawalProcessor(
                new WithdrawalProcessor(
                    readOnlyTxProcessingEnv.StateProvider,
                logManager)),
            null,
            consensusRequestsProcessor);
    }
}
