// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Config;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Merge.AuRa.Withdrawals;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;

namespace Nethermind.Merge.AuRa;

public class AuRaMergeBlockProducerEnvFactory : BlockProducerEnvFactory
{
    private readonly ChainSpec _chainSpec;
    private readonly IAbiEncoder _abiEncoder;

    public AuRaMergeBlockProducerEnvFactory(
        ChainSpec chainSpec,
        IAbiEncoder abiEncoder,
        IReadOnlyTxProcessingEnvFactory txProcessingEnvFactory,
        IWorldStateManager worldStateManager,
        IBlockTree blockTree,
        ISpecProvider specProvider,
        IBlockValidator blockValidator,
        IRewardCalculatorSource rewardCalculatorSource,
        IBlockPreprocessorStep blockPreprocessorStep,
        IBlocksConfig blocksConfig,
        IBlockProducerTxSourceFactory blockProducerTxSourceFactory,
        ILogManager logManager) : base(
            worldStateManager,
            txProcessingEnvFactory,
            blockTree,
            specProvider,
            blockValidator,
            rewardCalculatorSource,
            blockPreprocessorStep,
            blocksConfig,
            blockProducerTxSourceFactory,
            logManager)
    {
        _chainSpec = chainSpec;
        _abiEncoder = abiEncoder;
    }

    protected override BlockProcessor CreateBlockProcessor(IReadOnlyTxProcessingScope readOnlyTxProcessingEnv)
    {
        var withdrawalContractFactory = new WithdrawalContractFactory(
            _chainSpec.EngineChainSpecParametersProvider
                .GetChainSpecParameters<AuRaChainSpecEngineParameters>(), _abiEncoder);

        return new AuRaMergeBlockProcessor(
            _specProvider,
            _blockValidator,
            _rewardCalculatorSource.Get(readOnlyTxProcessingEnv.TransactionProcessor),
            TransactionsExecutorFactory.Create(readOnlyTxProcessingEnv),
            readOnlyTxProcessingEnv.WorldState,
            _receiptStorage,
            new BeaconBlockRootHandler(readOnlyTxProcessingEnv.TransactionProcessor, readOnlyTxProcessingEnv.WorldState),
            _logManager,
            _blockTree,
            new Consensus.Withdrawals.BlockProductionWithdrawalProcessor(
                new AuraWithdrawalProcessor(
                    withdrawalContractFactory.Create(readOnlyTxProcessingEnv.TransactionProcessor),
                    _logManager
                )
            ),
            ExecutionRequestsProcessorOverride ?? new ExecutionRequestsProcessor(readOnlyTxProcessingEnv.TransactionProcessor),
            null);
    }
}
