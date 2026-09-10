// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Messages;
using Nethermind.Consensus.Qbft.Validation;
using Nethermind.Consensus.Qbft.Validators;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Consensus.Qbft.StateMachine;

/// <summary>Creates the height manager for the next block: a full one for validators, a no-op one otherwise.</summary>
public sealed class QbftBlockHeightManagerFactory(
    IQbftFinalState finalState,
    QbftRoundFactory roundFactory,
    MessageValidatorFactory messageValidatorFactory,
    MessageFactory messageFactory,
    QbftMessageTransmitter transmitter,
    IValidatorProvider validatorProvider,
    BftBlockInterface blockInterface,
    ValidatorModeTransitionLogger validatorModeTransitionLogger,
    ILogManager logManager)
{
    private readonly ILogger _logger = logManager.GetClassLogger<QbftBlockHeightManagerFactory>();

    public bool IsEarlyRoundChangeEnabled { get; set; }

    public IBlockHeightManager Create(BlockHeader parentHeader)
    {
        validatorModeTransitionLogger.LogTransitionChange(parentHeader);
        if (finalState.IsLocalNodeValidator)
        {
            if (_logger.IsDebug) _logger.Debug("Local node is a validator");
            return CreateFullBlockHeightManager(parentHeader);
        }

        if (_logger.IsDebug) _logger.Debug("Local node is a non-validator");
        return CreateNoOpBlockHeightManager(parentHeader);
    }

    public IBlockHeightManager CreateNoOpBlockHeightManager(BlockHeader parentHeader) => new NoOpBlockHeightManager(parentHeader);

    private QbftBlockHeightManager CreateFullBlockHeightManager(BlockHeader parentHeader)
    {
        int validatorCount = finalState.Validators.Count;
        long chainHeight = (long)parentHeader.Number + 1;
        RoundChangeMessageValidator roundChangeValidator = messageValidatorFactory.CreateRoundChangeMessageValidator(chainHeight, parentHeader);
        RoundChangeManager roundChangeManager = new(
            BftHelpers.CalculateRequiredValidatorQuorum(validatorCount),
            roundChangeValidator,
            finalState.LocalAddress,
            logManager,
            IsEarlyRoundChangeEnabled ? BftHelpers.CalculateRequiredFutureRoundChangeQuorum(validatorCount) : 0);

        return new QbftBlockHeightManager(
            parentHeader,
            finalState,
            roundChangeManager,
            roundFactory,
            messageValidatorFactory,
            messageFactory,
            transmitter,
            validatorProvider,
            blockInterface,
            logManager,
            IsEarlyRoundChangeEnabled);
    }
}
