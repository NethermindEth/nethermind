// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.Xdc;

public class StartXdcBlockProducer(
    INethermindApi nethermindApi,
    IEpochSwitchManager epochSwitchManager,
    ISnapshotManager snapshotManager,
    IXdcConsensusContext xdcConsensusContext,
    ISpecProvider specProvider,
    IBlocksConfig blocksConfig,
    IBlockTree blockTree,
    ISealer sealer,
    ITimestamper timestamper,
    IGasLimitCalculator gasLimitCalculator,
    IDifficultyCalculator difficultyCalculator,
    ILogManager logManager)
{

    public IBlockProducer BuildProducer()
    {
        ILogger logger = logManager.GetClassLogger();
        if (logger.IsDebug) logger.Debug("Starting XDC block producer & sealer");

        IBlockProducerEnv env = nethermindApi.BlockProducerEnvFactory.Create();

        return new XdcBlockProducer(
            epochSwitchManager,
            snapshotManager,
            xdcConsensusContext,
            env.TxSource,
            env.ChainProcessor,
            sealer,
            blockTree,
            env.ReadOnlyStateProvider,
            gasLimitCalculator,
            timestamper,
            specProvider,
            logManager,
            difficultyCalculator,
            blocksConfig
            );
    }
}

