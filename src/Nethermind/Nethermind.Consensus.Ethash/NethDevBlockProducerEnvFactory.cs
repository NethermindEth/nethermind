// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Ethash;

public class NethDevBlockProducerEnvFactory(
    IWorldStateManager worldStateManager,
    IReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory,
    IBlockTree blockTree,
    ISpecProvider specProvider,
    IBlockValidator blockValidator,
    IRewardCalculatorSource rewardCalculatorSource,
    IBlockPreprocessorStep blockPreprocessorStep,
    IBlocksConfig blocksConfig,
    IBlockProducerTxSourceFactory blockProducerTxSourceFactory,
    ILogManager logManager)
    : BlockProducerEnvFactory(worldStateManager, readOnlyTxProcessingEnvFactory, blockTree, specProvider,
        blockValidator, rewardCalculatorSource, blockPreprocessorStep, blocksConfig, blockProducerTxSourceFactory,
        logManager)
{
    protected override ITxSource CreateTxSourceForProducer()
    {
        return base.CreateTxSourceForProducer().ServeTxsOneByOne();
    }
}
