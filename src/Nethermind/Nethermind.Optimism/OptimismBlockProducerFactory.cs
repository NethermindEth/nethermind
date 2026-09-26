// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.Optimism;

internal sealed class OptimismBlockProducerFactory(
    IBlockProducerEnvFactory blockProducerEnvFactory,
    ISealEngine sealEngine,
    ISpecProvider specProvider,
    IOptimismSpecHelper specHelper,
    IBlocksConfig blocksConfig,
    IBlockTree blockTree,
    ILogManager logManager)
    : IBlockProducerFactory, IBlockProducerRunnerFactory
{
    public IBlockProducer InitBlockProducer()
    {
        OptimismGasLimitCalculator gasLimitCalculator = new();

        IBlockProducerEnv producerEnv = blockProducerEnvFactory.CreatePersistent();

        return new OptimismPostMergeBlockProducer(
            new OptimismPayloadTxSource(),
            producerEnv.TxSource,
            producerEnv.ChainProcessor,
            producerEnv.BlockTree,
            producerEnv.ReadOnlyStateProvider,
            gasLimitCalculator,
            sealEngine,
            new ManualTimestamper(),
            specProvider,
            specHelper,
            logManager,
            blocksConfig);
    }

    public IBlockProducerRunner InitBlockProducerRunner(IBlockProducer blockProducer) =>
        new StandardBlockProducerRunner(NeverProduceTrigger.Instance, blockTree, blockProducer);
}
