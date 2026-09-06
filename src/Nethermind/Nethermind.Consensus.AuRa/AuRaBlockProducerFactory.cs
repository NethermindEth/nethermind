// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.Consensus.AuRa;

internal sealed class AuRaBlockProducerFactory(
    AuRaBlockProducerEnvFactory blockProducerEnvFactory,
    IBlockTree blockTree,
    ISealer sealer,
    ITimestamper timestamper,
    IAuRaStepCalculator stepCalculator,
    IReportingValidator reportingValidator,
    IAuraConfig auraConfig,
    AuRaGasLimitOverrideFactory gasLimitOverrideFactory,
    ISpecProvider specProvider,
    IBlocksConfig blocksConfig,
    IBlockProcessingQueue blockProcessingQueue,
    IDisposableStack disposeStack,
    ILogManager logManager)
    : IBlockProducerFactory, IBlockProducerRunnerFactory
{
    public IBlockProducer InitBlockProducer()
    {
        ILogger logger = logManager.GetClassLogger<AuRaBlockProducerFactory>();
        if (logger.IsInfo) logger.Info("Starting AuRa block producer & sealer");

        IBlockProducerEnv producerEnv = blockProducerEnvFactory.CreatePersistent();

        return new AuRaBlockProducer(
            producerEnv.TxSource,
            producerEnv.ChainProcessor,
            producerEnv.ReadOnlyStateProvider,
            sealer,
            blockTree,
            timestamper,
            stepCalculator,
            reportingValidator,
            auraConfig,
            CreateGasLimitCalculator(),
            specProvider,
            logManager,
            blocksConfig);
    }

    public IBlockProducerRunner InitBlockProducerRunner(IBlockProducer blockProducer)
    {
        BuildBlocksOnAuRaSteps onAuRaSteps = new(stepCalculator, logManager);
        BuildBlocksOnlyWhenNotProcessing onlyWhenNotProcessing = new(
            onAuRaSteps,
            blockProcessingQueue,
            blockTree,
            logManager,
            !auraConfig.AllowAuRaPrivateChains);

        disposeStack.Push((IAsyncDisposable)onlyWhenNotProcessing);

        return new StandardBlockProducerRunner(onlyWhenNotProcessing, blockTree, blockProducer);
    }

    private IGasLimitCalculator CreateGasLimitCalculator() =>
        (IGasLimitCalculator?)gasLimitOverrideFactory.GetGasLimitCalculator()
        ?? new TargetAdjustedGasLimitCalculator(specProvider, blocksConfig);
}
