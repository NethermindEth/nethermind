// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Messages;
using Nethermind.Core;
using NUnit.Framework;

namespace Nethermind.Consensus.Qbft.Test.Integration;

/// <summary>Port of Besu's <c>LocalNodeNotProposerTest</c>.</summary>
[Parallelizable(ParallelScope.Self)]
public class LocalNodeNotProposerTests
{
    private const int NetworkSize = 4;
    private static readonly ConsensusRoundIdentifier RoundId = new(1, 0);

    private QbftTestContext _context = null!;
    private RoundSpecificPeers _peers = null!;
    private Block _blockToPropose = null!;
    private Prepare _expectedTxPrepare = null!;
    private Commit _expectedTxCommit = null!;

    [SetUp]
    public void Setup()
    {
        _context = new QbftTestContextBuilder().ValidatorCount(NetworkSize).IndexOfFirstLocallyProposedBlock(0).BuildAndStart();
        _peers = _context.RoundSpecificPeers(RoundId);
        _blockToPropose = _context.CreateBlockForProposalFromChainHead(15, _peers.RequiredProposer.Address);
        _expectedTxPrepare = _context.LocalMessageFactory.CreatePrepare(RoundId, _context.Digest(_blockToPropose));
        _expectedTxCommit = _context.CreateLocalCommit(RoundId, _blockToPropose);
    }

    [Test]
    public void PreparesReceivedFromNonProposerIsValid()
    {
        _peers.RequiredProposer.InjectProposal(RoundId, _blockToPropose);
        _peers.VerifyMessagesReceived(_expectedTxPrepare);

        _peers.GetNonProposing(0).InjectPrepare(RoundId, _context.Digest(_blockToPropose));
        _peers.VerifyNoMessagesReceived();

        _peers.GetNonProposing(1).InjectPrepare(RoundId, _context.Digest(_blockToPropose));
        _peers.VerifyMessagesReceived(_expectedTxCommit);
        Assert.That(_context.CurrentChainHeight, Is.EqualTo(0));

        _peers.GetNonProposing(1).InjectPrepare(RoundId, _context.Digest(_blockToPropose));
        _peers.VerifyNoMessagesReceived();

        _peers.GetNonProposing(0).InjectCommit(RoundId, _blockToPropose);
        _peers.VerifyNoMessagesReceived();
        Assert.That(_context.CurrentChainHeight, Is.EqualTo(0));

        _peers.GetNonProposing(1).InjectCommit(RoundId, _blockToPropose);
        _peers.VerifyNoMessagesReceived();
        Assert.That(_context.CurrentChainHeight, Is.EqualTo(1));

        _peers.RequiredProposer.InjectCommit(RoundId, _blockToPropose);
        _peers.VerifyNoMessagesReceived();
        Assert.That(_context.CurrentChainHeight, Is.EqualTo(1));
    }

    [Test]
    public void CommitMessagesReceivedBeforePrepareCorrectlyImports()
    {
        _peers.ClearReceivedMessages();
        _peers.Commit(RoundId, _blockToPropose);
        _peers.VerifyNoMessagesReceived();
        Assert.That(_context.CurrentChainHeight, Is.EqualTo(0));

        _peers.PrepareForNonProposing(RoundId, _context.Digest(_blockToPropose));
        _peers.VerifyNoMessagesReceived();
        Assert.That(_context.CurrentChainHeight, Is.EqualTo(0));

        _peers.RequiredProposer.InjectProposal(RoundId, _blockToPropose);
        // As in Besu, the commit goes out before the prepare when the proposal completes both quorums at once.
        _peers.VerifyMessagesReceived(_expectedTxCommit, _expectedTxPrepare);
        Assert.That(_context.CurrentChainHeight, Is.EqualTo(1));
    }

    [Test]
    public void CanImportABlockIfSufficientCommitsReceivedWithoutPreparesAndThatNoPacketsSentAfterImport()
    {
        _peers.Commit(RoundId, _blockToPropose);
        _peers.VerifyNoMessagesReceived();
        Assert.That(_context.CurrentChainHeight, Is.EqualTo(0));

        _peers.RequiredProposer.InjectProposal(RoundId, _blockToPropose);
        _peers.VerifyMessagesReceived(_expectedTxPrepare);
        Assert.That(_context.CurrentChainHeight, Is.EqualTo(1));

        _peers.GetNonProposing(0).InjectPrepare(RoundId, _context.Digest(_blockToPropose));
        _peers.VerifyNoMessagesReceived();
    }
}
