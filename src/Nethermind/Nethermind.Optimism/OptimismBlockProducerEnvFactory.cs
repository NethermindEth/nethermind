// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Config;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Optimism;

public class OptimismBlockProducerEnvFactory : BlockProducerEnvFactory
{
    private readonly IOptimismSpecHelper _specHelper;

    public OptimismBlockProducerEnvFactory(
        IWorldStateManager worldStateManager,
        IReadOnlyTxProcessingEnvFactory txProcessingEnvFactory,
        IBlockTree blockTree,
        ISpecProvider specProvider,
        IBlockValidator blockValidator,
        IRewardCalculatorSource rewardCalculatorSource,
        IBlockPreprocessorStep blockPreprocessorStep,
        IBlocksConfig blocksConfig,
        IOptimismSpecHelper specHelper,
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
        _specHelper = specHelper;
        TransactionsExecutorFactory = new OptimismTransactionsExecutorFactory(specProvider, blocksConfig.BlockProductionMaxTxKilobytes, logManager);
    }

    protected override BlockProcessor CreateBlockProcessor(IReadOnlyTxProcessingScope readOnlyTxProcessingEnv)
    {
        return new OptimismBlockProcessor(
            _specProvider,
            _blockValidator,
            _rewardCalculatorSource.Get(readOnlyTxProcessingEnv.TransactionProcessor),
            TransactionsExecutorFactory.Create(readOnlyTxProcessingEnv),
            readOnlyTxProcessingEnv.WorldState,
            _receiptStorage,
            new BlockhashStore(_specProvider, readOnlyTxProcessingEnv.WorldState),
            new BeaconBlockRootHandler(readOnlyTxProcessingEnv.TransactionProcessor, readOnlyTxProcessingEnv.WorldState),
            _logManager,
            _specHelper,
            new Create2DeployerContractRewriter(_specHelper, _specProvider, _blockTree),
            new BlockProductionWithdrawalProcessor(new WithdrawalProcessor(readOnlyTxProcessingEnv.WorldState, _logManager)),
            new ExecutionRequestsProcessor(readOnlyTxProcessingEnv.TransactionProcessor));
    }
}
