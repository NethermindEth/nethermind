// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.P2P;
using Nethermind.Consensus.Qbft.Validators;
using Nethermind.Core;

namespace Nethermind.Consensus.Qbft.StateMachine;

public sealed class QbftFinalState(
    IValidatorProvider validatorProvider,
    Address localAddress,
    IProposerSelector proposerSelector,
    IValidatorMulticaster validatorMulticaster,
    RoundTimer roundTimer,
    BlockTimer blockTimer,
    IQbftBlockCreatorFactory blockCreatorFactory,
    ITimestamper clock) : IQbftFinalState
{
    public IReadOnlyList<Address> Validators => validatorProvider.GetValidatorsAtHead();
    public Address LocalAddress => localAddress;
    public bool IsLocalNodeValidator => Validators.ContainsAddress(localAddress);
    public int Quorum => BftHelpers.CalculateRequiredValidatorQuorum(Validators.Count);
    public bool IsLocalNodeProposerForRound(ConsensusRoundIdentifier roundIdentifier) => GetProposerForRound(roundIdentifier) == localAddress;
    public Address GetProposerForRound(ConsensusRoundIdentifier roundIdentifier) => proposerSelector.SelectProposerForRound(roundIdentifier);
    public IValidatorMulticaster ValidatorMulticaster => validatorMulticaster;
    public RoundTimer RoundTimer => roundTimer;
    public BlockTimer BlockTimer => blockTimer;
    public IQbftBlockCreatorFactory BlockCreatorFactory => blockCreatorFactory;
    public ITimestamper Clock => clock;
}
