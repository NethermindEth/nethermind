// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Messages;
using Nethermind.Consensus.Qbft.StateMachine;
using Nethermind.Core;
using NUnit.Framework;

namespace Nethermind.Consensus.Qbft.Test.Integration;

/// <summary>Port of Besu's <c>LocalNodeIsProposerTest</c>.</summary>
[Parallelizable(ParallelScope.Self)]
public class LocalNodeIsProposerTests
{
    private const ulong BlockTimestamp = 100;
    private const int NetworkSize = 4;
    private static readonly ConsensusRoundIdentifier RoundId = new(1, 0);

    private QbftTestContext _context = null!;
    private RoundSpecificPeers _peers = null!;
    private Block _expectedProposedBlock = null!;
    private Proposal _expectedTxProposal = null!;
    private Prepare _expectedTxPrepare = null!;
    private Commit _expectedTxCommit = null!;

    [SetUp]
    public void Setup()
    {
        _context = new QbftTestContextBuilder()
            .ValidatorCount(NetworkSize)
            .IndexOfFirstLocallyProposedBlock(1)
            .Clock(new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(BlockTimestamp)))
            .BuildAndStart();
        _peers = _context.RoundSpecificPeers(RoundId);
        Assert.That(_peers.Proposer, Is.Null, "the local node proposes block 1");

        _expectedProposedBlock = _context.CreateBlockForProposalFromChainHead(BlockTimestamp);
        _expectedTxProposal = _context.LocalMessageFactory.CreateProposal(RoundId, _expectedProposedBlock, null, [], []);
        _expectedTxPrepare = _context.LocalMessageFactory.CreatePrepare(RoundId, _context.Digest(_expectedProposedBlock));
        _expectedTxCommit = _context.CreateLocalCommit(RoundId, _expectedProposedBlock);

        _context.Controller.HandleBlockTimerExpiry(new BlockTimerExpiryEvent(RoundId));
    }

    [Test]
    public void BasicCase()
    {
        _peers.VerifyMessagesReceived(_expectedTxProposal, _expectedTxPrepare);

        _peers.GetNonProposing(0).InjectPrepare(RoundId, _context.Digest(_expectedProposedBlock));
        _peers.VerifyNoMessagesReceived();

        _peers.GetNonProposing(1).InjectPrepare(RoundId, _context.Digest(_expectedProposedBlock));
        _peers.VerifyMessagesReceived(_expectedTxCommit);

        _peers.GetNonProposing(1).InjectCommit(RoundId, _expectedProposedBlock);
        Assert.That(_context.CurrentChainHeight, Is.EqualTo(0));
        _peers.VerifyNoMessagesReceived();

        _peers.GetNonProposing(2).InjectCommit(RoundId, _expectedProposedBlock);
        Assert.That(_context.CurrentChainHeight, Is.EqualTo(1));
        _peers.VerifyNoMessagesReceived();
    }

    [Test]
    public void ImportsToChainWithoutReceivingPrepareMessages()
    {
        _peers.VerifyMessagesReceived(_expectedTxProposal, _expectedTxPrepare);

        _peers.GetNonProposing(1).InjectCommit(RoundId, _expectedProposedBlock);
        Assert.That(_context.CurrentChainHeight, Is.EqualTo(0));
        _peers.VerifyNoMessagesReceived();

        _peers.GetNonProposing(2).InjectCommit(RoundId, _expectedProposedBlock);
        Assert.That(_context.CurrentChainHeight, Is.EqualTo(1));
        _peers.VerifyNoMessagesReceived();
    }

    [Test]
    public void NodeDoesNotSendRoundChangeIfRoundTimesOutAfterBlockImportButBeforeNewBlock()
    {
        _peers.VerifyMessagesReceived(_expectedTxProposal, _expectedTxPrepare);
        _peers.GetNonProposing(0).InjectCommit(RoundId, _expectedProposedBlock);
        Assert.That(_context.CurrentChainHeight, Is.EqualTo(0));
        _peers.VerifyNoMessagesReceived();

        _peers.GetNonProposing(1).InjectCommit(RoundId, _expectedProposedBlock);
        Assert.That(_context.CurrentChainHeight, Is.EqualTo(1));
        _peers.VerifyNoMessagesReceived();

        _context.Controller.HandleRoundExpiry(new RoundExpiryEvent(RoundId));
        _peers.VerifyNoMessagesReceived();

        _context.Controller.HandleNewBlockEvent(new NewChainHeadEvent(_expectedProposedBlock.Header));
        _peers.VerifyNoMessagesReceived();
    }

    [Test]
    public void NodeDoesNotSendCommitMessageAfterBlockIsImportedAndBeforeNewBlockEvent()
    {
        _peers.VerifyMessagesReceived(_expectedTxProposal, _expectedTxPrepare);
        _peers.GetNonProposing(0).InjectCommit(RoundId, _expectedProposedBlock);
        _peers.GetNonProposing(1).InjectCommit(RoundId, _expectedProposedBlock);
        Assert.That(_context.CurrentChainHeight, Is.EqualTo(1));
        _peers.VerifyNoMessagesReceived();

        _peers.GetNonProposing(0).InjectPrepare(RoundId, _context.Digest(_expectedProposedBlock));
        _peers.GetNonProposing(1).InjectPrepare(RoundId, _context.Digest(_expectedProposedBlock));
        _peers.GetNonProposing(2).InjectPrepare(RoundId, _context.Digest(_expectedProposedBlock));
        _peers.VerifyNoMessagesReceived();
    }
}
