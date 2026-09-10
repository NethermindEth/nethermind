// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Messages;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Consensus.Qbft.Test.Integration;

/// <summary>Port of Besu's <c>GossipTest</c>.</summary>
[Parallelizable(ParallelScope.Self)]
public class GossipTests
{
    private const int NetworkSize = 5;
    private static readonly ConsensusRoundIdentifier RoundId = new(1, 0);

    private QbftTestContext _context = null!;
    private RoundSpecificPeers _peers = null!;
    private Block _block = null!;
    private ValidatorPeer _sender = null!;

    [SetUp]
    public void Setup()
    {
        _context = new QbftTestContextBuilder()
            .ValidatorCount(NetworkSize)
            .IndexOfFirstLocallyProposedBlock(0)
            .Clock(new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(100)))
            .UseGossip(true)
            .BuildAndStart();
        _peers = _context.RoundSpecificPeers(RoundId);
        _sender = _peers.RequiredProposer;
        _block = _context.CreateBlockForProposalFromChainHead(30, _sender.Address);
    }

    [Test]
    public void GossipMessagesToPeers()
    {
        Prepare localPrepare = _context.LocalMessageFactory.CreatePrepare(RoundId, _context.Digest(_block));
        _peers.VerifyNoMessagesReceivedNonProposing();

        Proposal proposal = _sender.InjectProposal(RoundId, _block);
        _peers.VerifyMessagesReceivedNonProposing(proposal, localPrepare);
        _peers.VerifyMessagesReceivedProposer(localPrepare);

        Prepare prepare = _sender.InjectPrepare(RoundId, _context.Digest(_block));
        _peers.VerifyMessagesReceivedNonProposing(prepare);
        _peers.VerifyNoMessagesReceivedProposer();

        Commit commit = _sender.InjectCommit(RoundId, _block);
        _peers.VerifyMessagesReceivedNonProposing(commit);
        _peers.VerifyNoMessagesReceivedProposer();

        RoundChange roundChange = _sender.MessageFactory.CreateRoundChange(RoundId, null);
        Proposal nextRoundProposal = _sender.InjectProposalForFutureRound(RoundId, [roundChange.SignedPayload], roundChange.Prepares, _block);
        _peers.VerifyMessagesReceivedNonProposing(nextRoundProposal);
        _peers.VerifyNoMessagesReceivedProposer();

        _sender.InjectRoundChange(RoundId, null);
        _peers.VerifyMessagesReceivedNonProposing(roundChange);
        _peers.VerifyNoMessagesReceivedProposer();
    }

    [Test]
    public void OnlyGossipOnce()
    {
        Prepare prepare = _sender.InjectPrepare(RoundId, _context.Digest(_block));
        _peers.VerifyMessagesReceivedNonProposing(prepare);
        _sender.InjectPrepare(RoundId, _context.Digest(_block));
        _peers.VerifyNoMessagesReceivedNonProposing();
        _sender.InjectPrepare(RoundId, _context.Digest(_block));
        _peers.VerifyNoMessagesReceivedNonProposing();
    }

    [Test]
    public void MessageWithUnknownValidatorIsNotGossiped()
    {
        using PrivateKey unknown = new PrivateKeyGenerator().Generate();
        MessageFactory unknownFactory = new(new Signer(1, unknown, LimboLogs.Instance), _context.Codec, _context.BlockInterface);
        _sender.InjectMessage(unknownFactory.CreateProposal(RoundId, _block, null, [], []));
        _peers.VerifyNoMessagesReceived();
    }

    [Test]
    public void MessageIsNotGossipedToSenderOrCreator()
    {
        ValidatorPeer creator = _peers.FirstNonProposer;
        Proposal proposalFromPeer = creator.MessageFactory.CreateProposal(RoundId, _block, null, [], []);
        _sender.InjectMessage(proposalFromPeer);
        _peers.VerifyMessagesReceivedNonProposingExcluding(creator, proposalFromPeer);
        _peers.VerifyNoMessagesReceivedProposer();
        creator.ClearReceivedMessages();
    }

    [Test]
    public void FutureMessageIsNotGossipedImmediately()
    {
        _sender.InjectProposal(new ConsensusRoundIdentifier(2, 0), _block);
        _peers.VerifyNoMessagesReceived();
    }

    [Test]
    public void PreviousHeightMessageIsNotGossiped()
    {
        _sender.InjectProposal(new ConsensusRoundIdentifier(0, 0), _block);
        _peers.VerifyNoMessagesReceived();
    }

    [Test]
    public void FutureMessageGetGossipedLater()
    {
        Block signedCurrentHeightBlock = _context.CreateSealedBlock(_block, 0, _peers.Sign(_context.Digest(_block)));
        ConsensusRoundIdentifier futureRoundId = new(2, 0);
        Prepare futurePrepare = _sender.InjectPrepare(futureRoundId, _context.Digest(_block));
        _peers.VerifyNoMessagesReceivedNonProposing();

        _context.AppendBlock(signedCurrentHeightBlock);
        _context.HandleNewChainHead();
        _peers.VerifyMessagesReceivedNonProposing(futurePrepare);
    }
}
