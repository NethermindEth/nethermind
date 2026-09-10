// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Messages;
using Nethermind.Consensus.Qbft.StateMachine;
using Nethermind.Core;
using NUnit.Framework;

namespace Nethermind.Consensus.Qbft.Test.Integration;

/// <summary>Port of Besu's <c>ReceivedFutureProposalTest</c>.</summary>
[Parallelizable(ParallelScope.Self)]
public class ReceivedFutureProposalTests
{
    private const int NetworkSize = 5;
    private static readonly ConsensusRoundIdentifier RoundId = new(1, 0);

    private QbftTestContext _context = null!;
    private RoundSpecificPeers _peers = null!;
    private MessageFactory _local = null!;

    [SetUp]
    public void Setup()
    {
        _context = new QbftTestContextBuilder().ValidatorCount(NetworkSize).IndexOfFirstLocallyProposedBlock(0).BuildAndStart();
        _peers = _context.RoundSpecificPeers(RoundId);
        _local = _context.LocalMessageFactory;
    }

    [Test]
    public void ProposalWithEmptyPrepareCertificatesOfferNewBlock()
    {
        ConsensusRoundIdentifier targetRound = new(1, 1);
        List<SignedData<RoundChangePayload>> roundChanges = _peers.CreateSignedRoundChangePayload(targetRound);
        ValidatorPeer nextProposer = _context.RoundSpecificPeers(targetRound).RequiredProposer;
        Block blockToPropose = _context.CreateBlockForProposalFromChainHead(15, nextProposer.Address);
        nextProposer.InjectProposalForFutureRound(targetRound, roundChanges, [], blockToPropose);
        _peers.VerifyMessagesReceived(_local.CreatePrepare(targetRound, _context.Digest(blockToPropose)));
    }

    [Test]
    public void ProposalFromIllegalSenderIsDiscardedAndNoPrepareForNewRoundIsSent()
    {
        ConsensusRoundIdentifier nextRoundId = new(1, 1);
        Block blockToPropose = _context.CreateBlockForProposalFromChainHead(15, _peers.RequiredProposer.Address);
        List<SignedData<RoundChangePayload>> roundChanges = _peers.CreateSignedRoundChangePayload(nextRoundId);
        ValidatorPeer illegalProposer = _context.RoundSpecificPeers(nextRoundId).GetNonProposing(0);
        illegalProposer.InjectProposalForFutureRound(nextRoundId, roundChanges, [], blockToPropose);
        _peers.VerifyNoMessagesReceived();
    }

    [Test]
    public void ProposalWithPrepareCertificateResultsInNewRoundStartingWithExpectedBlock()
    {
        Block initialBlock = _context.CreateBlockForProposalFromChainHead(15, _peers.RequiredProposer.Address);
        Block reproposedBlock = _context.CreateBlockForProposalFromChainHead(15, _peers.RequiredProposer.Address);
        ConsensusRoundIdentifier nextRoundId = new(1, 1);
        PreparedCertificate certificate = _context.CreateValidPreparedCertificate(RoundId, initialBlock);
        List<SignedData<RoundChangePayload>> roundChanges = _peers.CreateSignedRoundChangePayload(nextRoundId, certificate);
        List<SignedData<PreparePayload>> prepares = _peers.CreateSignedPreparePayloadOfAllPeers(RoundId, _context.Digest(initialBlock));

        ValidatorPeer nextProposer = _context.RoundSpecificPeers(nextRoundId).RequiredProposer;
        nextProposer.InjectProposalForFutureRound(nextRoundId, roundChanges, prepares, reproposedBlock);
        _peers.VerifyMessagesReceived(_local.CreatePrepare(nextRoundId, _context.Digest(reproposedBlock)));
    }

    [Test]
    public void FutureProposalWithInsufficientPreparesDoesNotTriggerNextRound()
    {
        Block initialBlock = _context.CreateBlockForProposalFromChainHead(15, _peers.RequiredProposer.Address);
        Block reproposedBlock = _context.CreateBlockForProposalFromChainHead(15, _peers.RequiredProposer.Address);
        ConsensusRoundIdentifier nextRoundId = new(1, 1);
        PreparedCertificate certificate = _context.CreateValidPreparedCertificate(RoundId, initialBlock);
        List<SignedData<RoundChangePayload>> roundChanges = _peers.CreateSignedRoundChangePayload(nextRoundId, certificate);
        List<SignedData<PreparePayload>> prepares = _peers.CreateSignedPreparePayloadOfAllPeers(RoundId, _context.Digest(initialBlock));

        ValidatorPeer nextProposer = _context.RoundSpecificPeers(nextRoundId).RequiredProposer;
        nextProposer.InjectProposalForFutureRound(nextRoundId, roundChanges, prepares.GetRange(0, 2), reproposedBlock);
        _peers.VerifyNoMessagesReceived();
    }

    [Test]
    public void FutureProposalWithInvalidPrepareDoesNotTriggerNextRound()
    {
        Block initialBlock = _context.CreateBlockForProposalFromChainHead(15, _peers.RequiredProposer.Address);
        Block reproposedBlock = _context.CreateBlockForProposalFromChainHead(15);
        ConsensusRoundIdentifier nextRoundId = new(1, 1);
        PreparedCertificate certificate = _context.CreateValidPreparedCertificate(RoundId, initialBlock);
        List<SignedData<RoundChangePayload>> roundChanges = _peers.CreateSignedRoundChangePayload(nextRoundId, certificate);

        List<SignedData<PreparePayload>> prepares = [];
        foreach (SignedData<PreparePayload> prepare in _peers.CreateSignedPreparePayloadOfAllPeers(RoundId, _context.Digest(initialBlock)))
        {
            if (prepare.Author != _peers.FirstNonProposer.Address) prepares.Add(prepare);
        }

        // A prepare for the wrong round poisons the certificate.
        prepares.Add(_peers.FirstNonProposer.MessageFactory.CreatePrepare(nextRoundId, _context.Digest(initialBlock)).SignedPayload);

        ValidatorPeer nextProposer = _context.RoundSpecificPeers(nextRoundId).RequiredProposer;
        nextProposer.InjectProposalForFutureRound(nextRoundId, roundChanges, prepares, reproposedBlock);
        _peers.VerifyNoMessagesReceived();
    }

    [Test]
    public void ProposalMessageForPriorRoundIsNotActioned()
    {
        ConsensusRoundIdentifier futureRound = new(1, 2);
        _peers.RoundChange(futureRound);
        ConsensusRoundIdentifier interimRound = new(1, 1);
        List<SignedData<RoundChangePayload>> roundChanges = _peers.CreateSignedRoundChangePayload(interimRound);
        ValidatorPeer interimProposer = _context.RoundSpecificPeers(interimRound).RequiredProposer;
        interimProposer.InjectProposalForFutureRound(interimRound, roundChanges, [], _context.CreateBlockForProposalFromChainHead(30));
        _peers.VerifyNoMessagesReceived();
    }

    [Test]
    public void ReceiveRoundStateIsNotLostIfASecondProposalMessageIsReceivedForCurrentRound()
    {
        Block block = _context.CreateBlockForProposalFromChainHead(15, _peers.RequiredProposer.Address);
        ConsensusRoundIdentifier nextRoundId = new(1, 1);
        PreparedCertificate certificate = _context.CreateValidPreparedCertificate(RoundId, block);
        List<SignedData<RoundChangePayload>> roundChanges = _peers.CreateSignedRoundChangePayload(nextRoundId, certificate);
        RoundSpecificPeers nextPeers = _context.RoundSpecificPeers(nextRoundId);
        ValidatorPeer nextProposer = nextPeers.RequiredProposer;

        nextProposer.InjectProposalForFutureRound(nextRoundId, roundChanges, certificate.Prepares, block);
        _peers.VerifyMessagesReceived(_local.CreatePrepare(nextRoundId, _context.Digest(block)));

        nextPeers.GetNonProposing(0).InjectPrepare(nextRoundId, _context.Digest(block));
        nextProposer.InjectProposalForFutureRound(nextRoundId, roundChanges, certificate.Prepares, block);
        nextProposer.InjectPrepare(nextRoundId, _context.Digest(block));
        _peers.VerifyNoMessagesReceived();

        nextPeers.GetNonProposing(1).InjectPrepare(nextRoundId, _context.Digest(block));
        _peers.VerifyMessagesReceived(_context.CreateLocalCommit(nextRoundId, block));
    }
}
