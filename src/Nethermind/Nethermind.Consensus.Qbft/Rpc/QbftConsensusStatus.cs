// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.StateMachine;
using Nethermind.Core;

namespace Nethermind.Consensus.Qbft.Rpc;

/// <summary>
/// Read-only window onto the running state machine for diagnostics; populated by the block producer
/// factory once the controller exists.
/// </summary>
public sealed class QbftConsensusStatus
{
    private volatile QbftController? _controller;
    private volatile IQbftFinalState? _finalState;
    private volatile Func<bool>? _isRunning;

    public void Attach(QbftController controller, IQbftFinalState finalState, Func<bool> isRunning)
    {
        _controller = controller;
        _finalState = finalState;
        _isRunning = isRunning;
    }

    public bool IsRunning => _isRunning?.Invoke() ?? false;

    public ConsensusRoundIdentifier? CurrentRound
    {
        get
        {
            try
            {
                return IsRunning ? _controller?.CurrentRoundIdentifier : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    public Address? ProposerForRound(ConsensusRoundIdentifier round)
    {
        try
        {
            return _finalState?.GetProposerForRound(round);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
