// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Messages;
using Nethermind.Consensus.Qbft.StateMachine;
using Nethermind.Core;
using NUnit.Framework;

namespace Nethermind.Consensus.Qbft.Test.Integration;

/// <summary>Port of Besu's <c>RoundChangeTest</c>.</summary>
[Parallelizable(ParallelScope.Self)]
public class RoundChangeTests
{
    private const ulong BlockTimestamp = 100;
    private const int NetworkSize = 5;
    private static readonly ConsensusRoundIdentifier RoundId = new(1, 0);

    private QbftTestContext _context = null!;
    private RoundSpecificPeers _peers = null!;
    private MessageFactory _local = null!;
    private Block _blockToPropose = null!;

    [SetUp]
    public void Setup()
    {
        _context = new QbftTestContextBuilder()
            .ValidatorCount(NetworkSize)
            .IndexOfFirstLocallyProposedBlock(0)
            .Clock(new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(BlockTimestamp)))
            .BuildAndStart();
        _peers = _context.RoundSpecificPeers(RoundId);
        _local = _context.LocalMessageFactory;
        _blockToPropose = _context.CreateBlockForProposalFromChainHead(15, _peers.RequiredProposer.Address);
    }

    [Test]
    public void OnRoundChangeTimerExpiryEventRoundChangeMessageIsSent()
    {
        ConsensusRoundIdentifier targetRound = new(1, 1);
        RoundChange expected = _local.CreateRoundChange(targetRound, null);
        _context.Controller.HandleBlockTimerExpiry(new BlockTimerExpiryEvent(RoundId));
        _context.Controller.HandleRoundExpiry(new RoundExpiryEvent(RoundId));
        _peers.VerifyMessagesReceived(expected);
    }

    [Test]
    public void RoundChangeHasEmptyCertificateIfNoPrepareMessagesReceived()
    {
        ConsensusRoundIdentifier targetRound = new(1, 1);
        RoundChange expected = _local.CreateRoundChange(targetRound, null);
        _peers.RequiredProposer.InjectProposal(RoundId, _blockToPropose);
        _peers.ClearReceivedMessages();
        _context.Controller.HandleRoundExpiry(new RoundExpiryEvent(RoundId));
        _peers.VerifyMessagesReceived(expected);
    }

    [Test]
    public void RoundChangeHasEmptyCertificateIfInsufficientPreparesAreReceived()
    {
        ConsensusRoundIdentifier targetRound = new(1, 1);
        RoundChange expected = _local.CreateRoundChange(targetRound, null);
        _peers.RequiredProposer.InjectProposal(RoundId, _blockToPropose);
        _peers.GetNonProposing(1).InjectPrepare(RoundId, _context.Digest(_blockToPropose));
        _peers.ClearReceivedMessages();
        _context.Controller.HandleRoundExpiry(new RoundExpiryEvent(RoundId));
        _peers.VerifyMessagesReceived(expected);
    }

    [Test]
    public void RoundChangeHasPopulatedCertificateIfQuorumPrepareMessagesAndProposalAreReceived()
    {
        ConsensusRoundIdentifier targetRound = new(1, 1);
        Prepare localPrepare = _local.CreatePrepare(RoundId, _context.Digest(_blockToPropose));
        _peers.RequiredProposer.InjectProposal(RoundId, _blockToPropose);
        Prepare p0 = _peers.RequiredProposer.InjectPrepare(RoundId, _context.Digest(_blockToPropose));
        Prepare p1 = _peers.GetNonProposing(0).InjectPrepare(RoundId, _context.Digest(_blockToPropose));
        Prepare p2 = _peers.GetNonProposing(1).InjectPrepare(RoundId, _context.Digest(_blockToPropose));
        _peers.ClearReceivedMessages();

        PreparedCertificate certificate = new(_blockToPropose, [localPrepare.SignedPayload, p0.SignedPayload, p1.SignedPayload, p2.SignedPayload], RoundId.Round);
        RoundChange expected = _local.CreateRoundChange(targetRound, certificate);
        _context.Controller.HandleRoundExpiry(new RoundExpiryEvent(RoundId));
        _peers.VerifyMessagesReceived(expected);
    }

    [Test]
    public void WhenSufficientRoundChangeMessagesAreReceivedForNewRoundLocalNodeCreatesProposalMsg()
    {
        // Round 4 is the next round for which the local node is the proposer.
        ConsensusRoundIdentifier targetRound = new(1, 4);
        Block locallyProposedBlock = _context.CreateBlockForProposalFromChainHead(BlockTimestamp, 4);
        RoundChange rc1 = _peers.GetNonProposing(0).InjectRoundChange(targetRound, null);
        RoundChange rc2 = _peers.GetNonProposing(1).InjectRoundChange(targetRound, null);
        RoundChange rc3 = _peers.GetNonProposing(2).InjectRoundChange(targetRound, null);
        RoundChange rc4 = _peers.RequiredProposer.InjectRoundChange(targetRound, null);

        Proposal expectedProposal = _local.CreateProposal(targetRound, locallyProposedBlock, null, [rc1.SignedPayload, rc2.SignedPayload, rc3.SignedPayload, rc4.SignedPayload], []);
        Prepare expectedPrepare = _local.CreatePrepare(targetRound, _context.Digest(locallyProposedBlock));
        _peers.VerifyMessagesReceived(expectedProposal, expectedPrepare);
    }

    [Test]
    public void ProposalMessageContainsBlockOnWhichPeerPrepared()
    {
        const ulong arbitraryBlockTime = 1500;
        PreparedCertificate earlierCertificate = _context.CreateValidPreparedCertificate(new ConsensusRoundIdentifier(1, 1), _context.CreateBlockForProposalFromChainHead(arbitraryBlockTime / 2, _peers.RequiredProposer.Address, 1));
        PreparedCertificate bestCertificate = _context.CreateValidPreparedCertificate(new ConsensusRoundIdentifier(1, 2), _context.CreateBlockForProposalFromChainHead(arbitraryBlockTime, _peers.RequiredProposer.Address, 2));

        ConsensusRoundIdentifier targetRound = new(1, 4);
        RoundChange rc1 = _peers.GetNonProposing(0).InjectRoundChange(targetRound, null);
        RoundChange rc2 = _peers.GetNonProposing(1).InjectRoundChange(targetRound, earlierCertificate);
        RoundChange rc3 = _peers.GetNonProposing(2).InjectRoundChange(targetRound, earlierCertificate);
        RoundChange rc4 = _peers.RequiredProposer.InjectRoundChange(targetRound, bestCertificate);

        // The later prepared block is re-proposed with the target round number.
        Block expectedBlock = _context.CreateBlockForProposalFromChainHead(arbitraryBlockTime, _peers.RequiredProposer.Address, 4);
        Proposal expectedProposal = _local.CreateProposal(targetRound, expectedBlock, null, [rc1.SignedPayload, rc2.SignedPayload, rc3.SignedPayload, rc4.SignedPayload], bestCertificate.Prepares);
        Prepare expectedPrepare = _local.CreatePrepare(targetRound, _context.Digest(expectedBlock));
        _peers.VerifyMessagesReceived(expectedProposal, expectedPrepare);
    }

    [Test]
    public void CannotRoundChangeToAnEarlierRound()
    {
        ConsensusRoundIdentifier futureRound = new(1, 9);
        List<SignedData<RoundChangePayload>> roundChanges = _peers.RoundChange(futureRound);
        _peers.RoundChange(new ConsensusRoundIdentifier(1, 4));

        Block locallyProposedBlock = _context.CreateBlockForProposalFromChainHead(BlockTimestamp, 9);
        Proposal expectedProposal = _local.CreateProposal(futureRound, locallyProposedBlock, null, roundChanges, []);
        Prepare expectedPrepare = _local.CreatePrepare(futureRound, _context.Digest(locallyProposedBlock));
        _peers.VerifyMessagesReceived(expectedProposal, expectedPrepare);
    }

    [Test]
    public void MultipleRoundChangeMessagesFromSamePeerDoesNotTriggerRoundChange()
    {
        ConsensusRoundIdentifier targetRound = new(1, 4);
        ValidatorPeer transmitter = _peers.GetNonProposing(0);
        for (int i = 0; i < BftHelpers.CalculateRequiredValidatorQuorum(NetworkSize); i++)
        {
            transmitter.InjectRoundChange(targetRound, null);
        }

        _peers.VerifyNoMessagesReceived();
    }

    [Test]
    public void SubsequentRoundChangeMessagesFromPeerDoNotOverwritePriorMessage()
    {
        const ulong arbitraryBlockTime = 1500;
        ConsensusRoundIdentifier targetRound = new(1, 4);
        PreparedCertificate certificate = _context.CreateValidPreparedCertificate(new ConsensusRoundIdentifier(1, 2), _context.CreateBlockForProposalFromChainHead(arbitraryBlockTime, _peers.RequiredProposer.Address, 2));

        List<SignedData<RoundChangePayload>> roundChanges = [_peers.RequiredProposer.InjectRoundChange(targetRound, certificate).SignedPayload];
        _peers.RequiredProposer.InjectRoundChange(targetRound, null);
        roundChanges.AddRange(_peers.RoundChangeForNonProposing(targetRound));

        Block expectedBlock = _context.CreateBlockForProposalFromChainHead(arbitraryBlockTime, _peers.RequiredProposer.Address, 4);
        Proposal expectedProposal = _local.CreateProposal(targetRound, expectedBlock, null, roundChanges, certificate.Prepares);
        Prepare expectedPrepare = _local.CreatePrepare(targetRound, _context.Digest(expectedBlock));
        _peers.VerifyMessagesReceived(expectedProposal, expectedPrepare);
    }

    [Test]
    public void MessagesFromPreviousRoundAreDiscardedOnTransitionToFutureRound()
    {
        _peers.RequiredProposer.InjectProposal(RoundId, _blockToPropose);
        _context.Controller.HandleRoundExpiry(new RoundExpiryEvent(RoundId));
        _peers.ClearReceivedMessages();
        _peers.PrepareForNonProposing(RoundId, _context.Digest(_blockToPropose));
        _peers.VerifyNoMessagesReceived();
    }

    [Test]
    public void RoundChangeExpiryForNonCurrentRoundIsDiscarded()
    {
        _context.Controller.HandleRoundExpiry(new RoundExpiryEvent(new ConsensusRoundIdentifier(1, 1)));
        _peers.VerifyNoMessagesReceived();
    }

    [Test]
    public void IllegallyConstructedRoundChangeMessageIsDiscarded()
    {
        ConsensusRoundIdentifier targetRound = new(1, 4);
        _peers.GetNonProposing(0).InjectRoundChange(targetRound, null);
        _peers.GetNonProposing(1).InjectRoundChange(targetRound, null);
        _peers.GetNonProposing(2).InjectRoundChange(targetRound, null);

        // A prepared certificate with no prepares cannot have reached quorum.
        PreparedCertificate illegal = new(_blockToPropose, [], 1);
        _peers.RequiredProposer.InjectRoundChange(targetRound, illegal);
        _peers.VerifyNoMessagesReceived();
    }

    [Test]
    public void EarlyRoundChangeIsTriggeredByFPlusOnePeersAhead()
    {
        QbftTestContext context = new QbftTestContextBuilder()
            .ValidatorCount(NetworkSize)
            .IndexOfFirstLocallyProposedBlock(0)
            .Clock(new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(BlockTimestamp)))
            .EarlyRoundChange(true)
            .BuildAndStart();
        RoundSpecificPeers peers = context.RoundSpecificPeers(RoundId);
        context.Controller.HandleBlockTimerExpiry(new BlockTimerExpiryEvent(RoundId));

        // f + 1 = 2 validators already in round 3 pull the local node there without waiting for its timer.
        ConsensusRoundIdentifier targetRound = new(1, 3);
        peers.GetNonProposing(0).InjectRoundChange(targetRound, null);
        peers.VerifyNoMessagesReceived();
        peers.GetNonProposing(1).InjectRoundChange(targetRound, null);
        peers.VerifyMessagesReceived(context.LocalMessageFactory.CreateRoundChange(targetRound, null));
    }
}
