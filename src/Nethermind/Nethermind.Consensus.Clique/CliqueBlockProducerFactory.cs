// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;

namespace Nethermind.Consensus.Clique;

internal sealed class CliqueBlockProducerFactory(
    IBlocksConfig blocksConfig,
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
