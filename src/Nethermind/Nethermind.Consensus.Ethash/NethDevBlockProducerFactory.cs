// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Ethash;

internal sealed class NethDevBlockProducerFactory(
    IBlockProducerEnvFactory blockProducerEnvFactory,
    IBlockTree blockTree,
    ITimestamper timestamper,
    ISpecProvider specProvider,
    IBlocksConfig blocksConfig,
    ITxPool txPool,
    IManualBlockProductionTrigger manualBlockProductionTrigger,
    ILogManager logManager)
    : IBlockProducerFactory, IBlockProducerRunnerFactory
{
    public IBlockProducer InitBlockProducer()
    {
        ILogger logger = logManager.GetClassLogger<NethDevBlockProducerFactory>();
        if (logger.IsInfo) logger.Info("Starting Neth Dev block producer & sealer");

        IBlockProducerEnv env = blockProducerEnvFactory.CreatePersistent();
        return new DevBlockProducer(
            env.TxSource,
            env.ChainProcessor,
            env.ReadOnlyStateProvider,
            blockTree,
            timestamper,
            specProvider,
            blocksConfig,
            logManager);
    }

    public IBlockProducerRunner InitBlockProducerRunner(IBlockProducer blockProducer)
    {
        IBlockProductionTrigger trigger = new BuildBlocksRegularly(TimeSpan.FromMilliseconds(200))
            .IfPoolIsNotEmpty(txPool)
            .Or(manualBlockProductionTrigger);
        return new StandardBlockProducerRunner(trigger, blockTree, blockProducer);
    }
}
