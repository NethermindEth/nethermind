// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Requests;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Merge.AuRa.Withdrawals;
using Nethermind.State;
using Nethermind.TxPool;

namespace Nethermind.Merge.AuRa;

public class AuRaMergeBlockProducerEnvFactory : BlockProducerEnvFactory
{
    private readonly AuRaNethermindApi _auraApi;
    private readonly IConsensusRequestsProcessor? _consensusRequestsProcessor;

    public AuRaMergeBlockProducerEnvFactory(
        AuRaNethermindApi auraApi,
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
        IConsensusRequestsProcessor? consensusRequestsProcessor = null) : base(
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
            logManager,
            consensusRequestsProcessor)
    {
        _auraApi = auraApi;
        _consensusRequestsProcessor = consensusRequestsProcessor;
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
        var withdrawalContractFactory = new WithdrawalContractFactory(_auraApi.ChainSpec!.AuRa, _auraApi.AbiEncoder);

        return new AuRaMergeBlockProcessor(
            specProvider,
            blockValidator,
            rewardCalculatorSource.Get(readOnlyTxProcessingEnv.TransactionProcessor),
            TransactionsExecutorFactory.Create(readOnlyTxProcessingEnv),
            readOnlyTxProcessingEnv.WorldState,
            receiptStorage,
            new BeaconBlockRootHandler(readOnlyTxProcessingEnv.TransactionProcessor),
            logManager,
            _blockTree,
            new Consensus.Withdrawals.BlockProductionWithdrawalProcessor(
                new AuraWithdrawalProcessor(
                    withdrawalContractFactory.Create(readOnlyTxProcessingEnv.TransactionProcessor),
                    logManager
                )
            ),
            readOnlyTxProcessingEnv.TransactionProcessor,
            null,
            consensusRequestsProcessor: _consensusRequestsProcessor);
    }

    protected override TxPoolTxSource CreateTxPoolTxSource(
        ReadOnlyTxProcessingEnv processingEnv,
        ITxPool txPool,
        IBlocksConfig blocksConfig,
        ITransactionComparerProvider transactionComparerProvider,
        ILogManager logManager)
    {
        return new StartBlockProducerAuRa(_auraApi).CreateTxPoolTxSource();
    }
}
