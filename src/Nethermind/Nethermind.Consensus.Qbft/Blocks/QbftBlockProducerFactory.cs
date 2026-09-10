// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Config;
using Nethermind.Consensus.Qbft.Messages;
using Nethermind.Consensus.Qbft.P2P;
using Nethermind.Consensus.Qbft.StateMachine;
using Nethermind.Consensus.Qbft.Validation;
using Nethermind.Consensus.Qbft.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.Consensus.Qbft.Blocks;

/// <summary>Assembles the QBFT state machine around the block producer and returns the runner that drives it.</summary>
public sealed class QbftBlockProducerFactory(
    IBlockProducerEnvFactory blockProducerEnvFactory,
    IBlockTree blockTree,
    ISealer sealer,
    ISigner signer,
    IGasLimitCalculator gasLimitCalculator,
    ITimestamper timestamper,
    ISpecProvider specProvider,
    IBlocksConfig blocksConfig,
    IValidatorProvider validatorProvider,
    IProposerSelector proposerSelector,
    QbftForksSchedule forksSchedule,
    QbftChainSpecEngineParameters parameters,
    IQbftConfig qbftConfig,
    IBftExtraDataCodecSelector codecs,
    QbftBlockInterface blockInterface,
    QbftMessageCodec messageCodec,
    IQbftBlockValidator blockValidator,
    IQbftBlockImporter blockImporter,
    BftEventQueue eventQueue,
    ValidatorPeers validatorPeers,
    ValidatorModeTransitionLogger validatorModeTransitionLogger,
    IEnumerable<IMinedBlockObserver> minedBlockObservers,
    Rpc.QbftConsensusStatus consensusStatus,
    IDisposableStack disposeStack,
    ILogManager logManager) : IBlockProducerFactory, IBlockProducerRunnerFactory
{
    private readonly ILogger _logger = logManager.GetClassLogger<QbftBlockProducerFactory>();

    public IBlockProducer InitBlockProducer()
    {
        IBlockProducerEnv env = blockProducerEnvFactory.CreatePersistent();
        return new QbftBlockProducer(
            env.TxSource,
            env.ChainProcessor,
            sealer,
            blockTree,
            env.ReadOnlyStateProvider,
            gasLimitCalculator,
            timestamper,
            specProvider,
            blocksConfig,
            validatorProvider,
            forksSchedule,
            codecs,
            logManager);
    }

    /// <remarks>
    /// The state machine is assembled here rather than registered in the container because every part of it
    /// belongs to one runner: the timers, the event queue reader, the round factory and the controller share a
    /// single consensus thread and are replaced together when production restarts. This mirrors Besu, which
    /// builds the same graph in its controller builder.
    /// </remarks>
    public IBlockProducerRunner InitBlockProducerRunner(IBlockProducer blockProducer)
    {
        if (blockProducer is not QbftBlockProducer qbftProducer)
        {
            throw new ArgumentException($"QBFT requires a {nameof(QbftBlockProducer)}, got {blockProducer.GetType().Name}.", nameof(blockProducer));
        }

        Address localAddress = signer.Address;
        UniqueMessageMulticaster multicaster = new(validatorPeers, parameters.GossipedHistoryLimit);
        MessageFactory messageFactory = new(signer, messageCodec, blockInterface, qbftConfig.LegacyMessageEncoding);
        QbftMessageTransmitter transmitter = new(messageFactory, messageCodec, multicaster, logManager);
        BftRoundExpiryTimeCalculator expiryCalculator = new(TimeSpan.FromSeconds(parameters.RequestTimeoutSeconds));
        RoundTimer roundTimer = new(eventQueue, expiryCalculator, ThreadingTimerScheduler.Instance, logManager);
        BlockTimer blockTimer = new(eventQueue, forksSchedule, ThreadingTimerScheduler.Instance, timestamper, logManager);
        QbftFinalState finalState = new(validatorProvider, localAddress, proposerSelector, multicaster, roundTimer, blockTimer, new QbftBlockCreatorFactory(qbftProducer), timestamper);
        MessageValidatorFactory messageValidatorFactory = new(proposerSelector, blockValidator, validatorProvider, blockInterface, logManager);
        QbftRoundFactory roundFactory = new(finalState, blockInterface, blockImporter, [.. minedBlockObservers], messageValidatorFactory, messageFactory, transmitter, logManager);
        QbftBlockHeightManagerFactory heightManagerFactory = new(
            finalState, roundFactory, messageValidatorFactory, messageFactory, transmitter, validatorProvider, blockInterface, validatorModeTransitionLogger, logManager)
        {
            IsEarlyRoundChangeEnabled = qbftConfig.EarlyRoundChange,
        };
        long headNumber = (long)(blockTree.Head?.Number ?? 0);
        FutureMessageBuffer futureMessageBuffer = new(parameters.FutureMessagesMaxDistance, parameters.FutureMessagesLimit, headNumber);
        QbftController controller = new(
            blockTree,
            finalState,
            heightManagerFactory,
            new QbftGossiper(multicaster),
            new MessageTracker(parameters.DuplicateMessageLimit),
            futureMessageBuffer,
            messageCodec,
            logManager);

        if (_logger.IsInfo) _logger.Info($"QBFT node address {localAddress}{(signer.CanSign ? "" : " (no signing key: observer only)")}");
        QbftBlockProducerRunner runner = new(blockTree, eventQueue, controller, logManager);
        consensusStatus.Attach(controller, finalState, () => runner.IsProducingBlocks(null));
        disposeStack.Push(runner);
        return runner;
    }
}
