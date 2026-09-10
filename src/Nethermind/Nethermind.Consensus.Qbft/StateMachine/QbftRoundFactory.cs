// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Messages;
using Nethermind.Consensus.Qbft.Validation;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Consensus.Qbft.StateMachine;

public sealed class QbftRoundFactory(
    IQbftFinalState finalState,
    QbftBlockInterface blockInterface,
    IQbftBlockImporter blockImporter,
    IReadOnlyList<IMinedBlockObserver> minedBlockObservers,
    MessageValidatorFactory messageValidatorFactory,
    MessageFactory messageFactory,
    QbftMessageTransmitter transmitter,
    ILogManager logManager)
{
    public QbftRound CreateNewRound(BlockHeader parentHeader, int round)
    {
        ConsensusRoundIdentifier roundIdentifier = new((long)parentHeader.Number + 1, round);
        RoundState roundState = new(roundIdentifier, finalState.Quorum, messageValidatorFactory.CreateMessageValidator(roundIdentifier, parentHeader), logManager);
        return CreateNewRoundWithState(parentHeader, roundState);
    }

    public QbftRound CreateNewRoundWithState(BlockHeader parentHeader, RoundState roundState) =>
        new(
            roundState,
            finalState.BlockCreatorFactory.Create(roundState.RoundIdentifier.Round),
            blockInterface,
            blockImporter,
            minedBlockObservers,
            messageFactory,
            transmitter,
            finalState.RoundTimer,
            parentHeader,
            logManager);
}
