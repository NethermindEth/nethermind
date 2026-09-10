// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.P2P;
using Nethermind.Core;

namespace Nethermind.Consensus.Qbft.StateMachine;

/// <summary>An input to the consensus state machine; all inputs are serialised through one queue.</summary>
public abstract record BftEvent;

public sealed record ReceivedMessageEvent(QbftReceivedMessage Message) : BftEvent;

public sealed record NewChainHeadEvent(BlockHeader NewChainHeadHeader) : BftEvent;

public sealed record BlockTimerExpiryEvent(ConsensusRoundIdentifier RoundIdentifier) : BftEvent;

public sealed record RoundExpiryEvent(ConsensusRoundIdentifier View) : BftEvent;

/// <summary>Accepts events from the network, timers and the block tree for the consensus loop.</summary>
public interface IBftEventQueue
{
    void Add(BftEvent bftEvent);
}
