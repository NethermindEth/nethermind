// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Consensus.Clique;

internal sealed class CliqueBlockProducerFactory(
    ChainSpec chainSpec,
    IBlocksConfig blocksConfig,
    IMiningConfig miningConfig,
    IBlockProducerEnvFactory blockProducerEnvFactory,
    ISpecProvider specProvider,
    ITimestamper timestamper,
    ICryptoRandom cryptoRandom,
    ISnapshotManager snapshotManager,
    ISealer sealer,
    ICliqueConfig cliqueConfig,
    IBlockTree blockTree,
    IDisposableStack disposeStack,
    ILogManager logManager)
    : IBlockProducerFactory, IBlockProducerRunnerFactory
{
    public IBlockProducer InitBlockProducer()
    {
        if (chainSpec.SealEngineType != Nethermind.Core.SealEngineType.Clique)
        {
            return null;
        }

        if (!miningConfig.Enabled)
        {
            throw new InvalidOperationException("Request to start block producer while mining disabled.");
        }

        IBlockProducerEnv env = blockProducerEnvFactory.CreatePersistent();
        IGasLimitCalculator gasLimitCalculator = new TargetAdjustedGasLimitCalculator(specProvider, blocksConfig);

        return new CliqueBlockProducer(
            env.TxSource,
            env.ChainProcessor,
            env.ReadOnlyStateProvider,
            timestamper,
            cryptoRandom,
            snapshotManager,
            sealer,
            gasLimitCalculator,
            specProvider,
            cliqueConfig,
            logManager);
    }

    public IBlockProducerRunner InitBlockProducerRunner(IBlockProducer blockProducer)
    {
        CliqueBlockProducerRunner runner = new(
            blockTree,
            timestamper,
            cryptoRandom,
            snapshotManager,
            (CliqueBlockProducer)blockProducer,
            cliqueConfig,
            logManager);
        disposeStack.Push(runner);
        return runner;
    }
}
