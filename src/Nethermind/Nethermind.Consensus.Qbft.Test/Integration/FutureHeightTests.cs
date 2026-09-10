// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Messages;
using Nethermind.Core;
using NUnit.Framework;

namespace Nethermind.Consensus.Qbft.Test.Integration;

/// <summary>Port of Besu's <c>FutureHeightTest</c>.</summary>
[Parallelizable(ParallelScope.Self)]
public class FutureHeightTests
{
    private const int NetworkSize = 5;
    private static readonly ConsensusRoundIdentifier RoundId = new(1, 0);
    private static readonly ConsensusRoundIdentifier FutureHeightRoundId = new(2, 0);

    private QbftTestContext _context = null!;
    private RoundSpecificPeers _peers = null!;
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
        _local = _context.LocalMessageFactory;
    }

    [Test]
    public void MessagesForFutureHeightAreBufferedUntilChainHeightCatchesUp()
    {
        Block currentHeightBlock = _context.CreateBlockForProposalFromChainHead(30);
        Block signedCurrentHeightBlock = _context.CreateSealedBlock(currentHeightBlock, 0, _peers.Sign(_context.Digest(currentHeightBlock)));
        Block futureHeightBlock = _context.CreateBlockForProposal(signedCurrentHeightBlock.Header, 60, _peers.RequiredProposer.Address, 0);

        _peers.RequiredProposer.InjectProposal(FutureHeightRoundId, futureHeightBlock);
        _peers.VerifyNoMessagesReceived();
        _peers.PrepareForNonProposing(FutureHeightRoundId, _context.Digest(futureHeightBlock));
        _peers.CommitForNonProposing(FutureHeightRoundId, futureHeightBlock);
        _peers.VerifyNoMessagesReceived();
        Assert.That(_context.CurrentChainHeight, Is.EqualTo(0));

        _context.AppendBlock(signedCurrentHeightBlock);
        Assert.That(_context.CurrentChainHeight, Is.EqualTo(1));
        _context.HandleNewChainHead();

        Prepare expectedPrepare = _local.CreatePrepare(FutureHeightRoundId, _context.Digest(futureHeightBlock));
        Commit expectedCommit = _context.CreateLocalCommit(FutureHeightRoundId, futureHeightBlock);
        _peers.VerifyMessagesReceived(expectedPrepare, expectedCommit);
        Assert.That(_context.CurrentChainHeight, Is.EqualTo(2));
    }

    [Test]
    public void MessagesFromPreviousHeightAreDiscarded()
    {
        Block currentHeightBlock = _context.CreateBlockForProposalFromChainHead(30, _peers.RequiredProposer.Address);
        Block signedCurrentHeightBlock = _context.CreateSealedBlock(currentHeightBlock, 0, _peers.Sign(_context.Digest(currentHeightBlock)));

        _peers.RequiredProposer.InjectProposal(RoundId, currentHeightBlock);
        _peers.GetNonProposing(0).InjectPrepare(RoundId, _context.Digest(currentHeightBlock));
        _peers.VerifyMessagesReceived(_local.CreatePrepare(RoundId, _context.Digest(currentHeightBlock)));

        _context.AppendBlock(signedCurrentHeightBlock);
        Assert.That(_context.CurrentChainHeight, Is.EqualTo(1));
        _context.HandleNewChainHead();

        _peers.PrepareForNonProposing(RoundId, _context.Digest(currentHeightBlock));
        _peers.CommitForNonProposing(RoundId, currentHeightBlock);
        _peers.VerifyNoMessagesReceived();
        Assert.That(_context.CurrentChainHeight, Is.EqualTo(1));
    }

    [Test]
    public void MultipleNewChainHeadEventsDoesNotRestartCurrentHeightManager()
    {
        Block currentHeightBlock = _context.CreateBlockForProposalFromChainHead(30, _peers.RequiredProposer.Address);
        _peers.RequiredProposer.InjectProposal(RoundId, currentHeightBlock);
        _peers.RequiredProposer.InjectPrepare(RoundId, _context.Digest(currentHeightBlock));
        _peers.GetNonProposing(0).InjectPrepare(RoundId, _context.Digest(currentHeightBlock));
        _peers.ClearReceivedMessages();

        _context.HandleNewChainHead();

        _peers.GetNonProposing(1).InjectPrepare(RoundId, _context.Digest(currentHeightBlock));
        _peers.VerifyMessagesReceived(_context.CreateLocalCommit(RoundId, currentHeightBlock));
    }

    [Test]
    public void CorrectMessagesAreExtractedFromFutureHeightBuffer()
    {
        Block currentHeightBlock = _context.CreateBlockForProposalFromChainHead(30);
        Block signedCurrentHeightBlock = _context.CreateSealedBlock(currentHeightBlock, 0, _peers.Sign(_context.Digest(currentHeightBlock)));
        Block nextHeightBlock = _context.CreateBlockForProposal(signedCurrentHeightBlock.Header, 60, _peers.RequiredProposer.Address, 0);
        Block signedNextHeightBlock = _context.CreateSealedBlock(nextHeightBlock, 0, _peers.Sign(_context.Digest(nextHeightBlock)));
        Block futureHeightBlock = _context.CreateBlockForProposal(signedNextHeightBlock.Header, 90, _peers.GetNonProposing(0).Address, 0);

        ConsensusRoundIdentifier nextHeightRoundId = new(2, 0);
        ConsensusRoundIdentifier futureHeightRoundId = new(3, 0);

        _peers.PrepareForNonProposing(futureHeightRoundId, _context.Digest(futureHeightBlock));
        _peers.CommitForNonProposing(futureHeightRoundId, futureHeightBlock);

        _context.AppendBlock(signedCurrentHeightBlock);
        Assert.That(_context.CurrentChainHeight, Is.EqualTo(1));
        _context.HandleNewChainHead();
        _peers.VerifyNoMessagesReceived();

        _peers.RequiredProposer.InjectProposal(nextHeightRoundId, nextHeightBlock);
        // Only a prepare: the height-3 messages must not have been replayed yet.
        _peers.VerifyMessagesReceived(_local.CreatePrepare(nextHeightRoundId, _context.Digest(nextHeightBlock)));

        _peers.GetNonProposing(0).InjectProposal(futureHeightRoundId, futureHeightBlock);

        _context.AppendBlock(signedNextHeightBlock);
        Assert.That(_context.CurrentChainHeight, Is.EqualTo(2));
        _context.HandleNewChainHead();

        Prepare expectedFuturePrepare = _local.CreatePrepare(futureHeightRoundId, _context.Digest(futureHeightBlock));
        Commit expectedCommit = _context.CreateLocalCommit(futureHeightRoundId, futureHeightBlock);
        _peers.VerifyMessagesReceived(expectedCommit, expectedFuturePrepare);
    }
}
