// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Messages;
using Nethermind.Consensus.Qbft.P2P;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Consensus.Qbft.StateMachine;

/// <summary>Encodes and multicasts the local node's consensus messages to the validators.</summary>
public sealed class QbftMessageTransmitter(MessageFactory messageFactory, QbftMessageCodec codec, IValidatorMulticaster multicaster, ILogManager logManager)
{
    private readonly ILogger _logger = logManager.GetClassLogger<QbftMessageTransmitter>();

    public void MulticastProposal(Proposal proposal) => Multicast(proposal);

    public void MulticastPrepare(Prepare prepare) => Multicast(prepare);

    public void MulticastCommit(ConsensusRoundIdentifier roundIdentifier, Hash256 digest, Signature commitSeal) =>
        Multicast(() => messageFactory.CreateCommit(roundIdentifier, digest, commitSeal));

    public void MulticastRoundChange(ConsensusRoundIdentifier roundIdentifier, PreparedCertificate? preparedCertificate) =>
        Multicast(() => messageFactory.CreateRoundChange(roundIdentifier, preparedCertificate));

    private void Multicast(Func<BftMessage> create)
    {
        BftMessage message;
        try
        {
            message = create();
        }
        catch (InvalidOperationException e)
        {
            if (_logger.IsWarn) _logger.Warn($"Failed to generate signature for QBFT message (not sent): {e.Message}");
            return;
        }

        Multicast(message);
    }

    private void Multicast(BftMessage message) => multicaster.Send(message.MessageType, codec.Encode(message));
}
