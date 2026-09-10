// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Messages;
using Nethermind.Core;
using NUnit.Framework;

namespace Nethermind.Consensus.Qbft.Test.Integration;

/// <summary>Port of Besu's <c>FutureRoundTest</c>.</summary>
[Parallelizable(ParallelScope.Self)]
public class FutureRoundTests
{
    private const int NetworkSize = 5;
    private static readonly ConsensusRoundIdentifier RoundId = new(1, 0);
    private static readonly ConsensusRoundIdentifier FutureRoundId = new(1, 5);

    private QbftTestContext _context = null!;
    private RoundSpecificPeers _peers = null!;
    private RoundSpecificPeers _futurePeers = null!;
    private MessageFactory _local = null!;

    [SetUp]
    public void Setup()
    {
        _context = new QbftTestContextBuilder()
            .ValidatorCount(NetworkSize)
            .IndexOfFirstLocallyProposedBlock(0)
            .Clock(new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(100)))
            .BuildAndStart();
        _peers = _context.RoundSpecificPeers(RoundId);
        _futurePeers = _context.RoundSpecificPeers(FutureRoundId);
        _local = _context.LocalMessageFactory;
    }

    [Test]
    public void MessagesForFutureRoundAreNotActionedUntilRoundIsActive()
    {
        Block futureBlock = _context.CreateBlockForProposalFromChainHead(60, _peers.RequiredProposer.Address);
        int quorum = BftHelpers.CalculateRequiredValidatorQuorum(NetworkSize);
        ConsensusRoundIdentifier subsequentRoundId = new(1, 6);
        RoundSpecificPeers subsequentPeers = _context.RoundSpecificPeers(subsequentRoundId);

        for (int i = 0; i < quorum - 2; i++) _futurePeers.GetNonProposing(i).InjectPrepare(FutureRoundId, _context.Digest(futureBlock));
        for (int i = 0; i < quorum - 2; i++) _futurePeers.GetNonProposing(i).InjectCommit(FutureRoundId, futureBlock);

        subsequentPeers.GetNonProposing(1).InjectPrepare(subsequentRoundId, _context.Digest(futureBlock));
        subsequentPeers.GetNonProposing(1).InjectCommit(subsequentRoundId, futureBlock);
        _peers.VerifyNoMessagesReceived();
        Assert.That(_context.CurrentChainHeight, Is.EqualTo(0));

        List<SignedData<RoundChangePayload>> roundChanges = _futurePeers.CreateSignedRoundChangePayload(FutureRoundId);
        _futurePeers.RequiredProposer.InjectProposalForFutureRound(FutureRoundId, roundChanges, [], futureBlock);
        _peers.VerifyMessagesReceived(_local.CreatePrepare(FutureRoundId, _context.Digest(futureBlock)));

        _futurePeers.GetNonProposing(quorum - 2).InjectPrepare(FutureRoundId, _context.Digest(futureBlock));
        _peers.VerifyMessagesReceived(_context.CreateLocalCommit(FutureRoundId, futureBlock));

        _futurePeers.GetNonProposing(quorum - 2).InjectCommit(FutureRoundId, futureBlock);
        Assert.That(_context.CurrentChainHeight, Is.EqualTo(1));
    }

    [Test]
    public void PriorRoundsCannotBeCompletedAfterReceptionOfNewRound()
    {
        Block initialBlock = _context.CreateBlockForProposalFromChainHead(30, _peers.RequiredProposer.Address);
        Block futureBlock = _context.CreateBlockForProposalFromChainHead(60, _peers.RequiredProposer.Address);

        _peers.RequiredProposer.InjectProposal(RoundId, initialBlock);
        _peers.PrepareForNonProposing(RoundId, _context.Digest(initialBlock));
        _peers.RequiredProposer.InjectCommit(RoundId, initialBlock);
        Assert.That(_context.CurrentChainHeight, Is.EqualTo(0));
        _peers.ClearReceivedMessages();

        List<SignedData<RoundChangePayload>> roundChanges = _futurePeers.CreateSignedRoundChangePayload(FutureRoundId);
        _futurePeers.RequiredProposer.InjectProposalForFutureRound(FutureRoundId, roundChanges, [], futureBlock);
        _peers.VerifyMessagesReceived(_local.CreatePrepare(FutureRoundId, _context.Digest(futureBlock)));

        _peers.GetNonProposing(0).InjectCommit(RoundId, initialBlock);
        _peers.VerifyNoMessagesReceived();
        Assert.That(_context.CurrentChainHeight, Is.EqualTo(0));
    }
}
