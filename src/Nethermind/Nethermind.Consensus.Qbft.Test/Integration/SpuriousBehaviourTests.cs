// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Messages;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using NUnit.Framework;

namespace Nethermind.Consensus.Qbft.Test.Integration;

/// <summary>Port of Besu's <c>SpuriousBehaviourTest</c>.</summary>
[Parallelizable(ParallelScope.Self)]
public class SpuriousBehaviourTests
{
    private const int NetworkSize = 5;
    private static readonly ConsensusRoundIdentifier RoundId = new(1, 0);

    private QbftTestContext _context = null!;
    private RoundSpecificPeers _peers = null!;
    private Block _proposedBlock = null!;
    private Prepare _expectedPrepare = null!;
    private Commit _expectedCommit = null!;

    [SetUp]
    public void Setup()
    {
        _context = new QbftTestContextBuilder()
            .ValidatorCount(NetworkSize)
            .IndexOfFirstLocallyProposedBlock(0)
            .Clock(new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(100)))
            .BuildAndStart();
        _peers = _context.RoundSpecificPeers(RoundId);
        _proposedBlock = _context.CreateBlockForProposalFromChainHead(30, _peers.RequiredProposer.Address);
        _expectedPrepare = _context.LocalMessageFactory.CreatePrepare(RoundId, _context.Digest(_proposedBlock));
        _expectedCommit = _context.CreateLocalCommit(RoundId, _proposedBlock);
    }

    [Test]
    public void BadlyFormedRlpDoesNotPreventOngoingBftOperation()
    {
        _peers.GetNonProposing(0).InjectMessage(QbftMessageCode.Prepare, []);
        _peers.RequiredProposer.InjectProposal(RoundId, _proposedBlock);
        _peers.VerifyMessagesReceived(_expectedPrepare);
    }

    [Test]
    public void MessageWithIllegalMessageCodeAreDiscardedAndDoNotPreventOngoingBftOperation()
    {
        _peers.GetNonProposing(0).InjectMessage(QbftMessageCode.MessageSpace, []);
        _peers.RequiredProposer.InjectProposal(RoundId, _proposedBlock);
        _peers.VerifyMessagesReceived(_expectedPrepare);
    }

    [Test]
    public void NonValidatorsCannotTriggerResponses()
    {
        using PrivateKey nonValidatorKey = new PrivateKeyGenerator().Generate();
        ValidatorPeer nonValidator = new(nonValidatorKey, _context);
        nonValidator.InjectProposal(RoundId, _proposedBlock);
        _peers.VerifyNoMessagesReceived();
    }

    [Test]
    public void PreparesWithMisMatchedDigestAreNotRespondedTo()
    {
        _peers.RequiredProposer.InjectProposal(RoundId, _proposedBlock);
        _peers.VerifyMessagesReceived(_expectedPrepare);

        _peers.PrepareForNonProposing(RoundId, Keccak.Zero);
        _peers.VerifyNoMessagesReceived();

        _peers.PrepareForNonProposing(RoundId, _context.Digest(_proposedBlock));
        _peers.VerifyMessagesReceived(_expectedCommit);

        _peers.PrepareForNonProposing(RoundId, Keccak.Zero);
        Assert.That(_context.CurrentChainHeight, Is.EqualTo(0));

        _peers.CommitForNonProposing(RoundId, _proposedBlock);
        Assert.That(_context.CurrentChainHeight, Is.EqualTo(1));
    }

    [Test]
    public void OneCommitSealIsIllegalPreventsImport()
    {
        _peers.RequiredProposer.InjectProposal(RoundId, _proposedBlock);
        _peers.VerifyMessagesReceived(_expectedPrepare);
        _peers.PrepareForNonProposing(RoundId, _context.Digest(_proposedBlock));

        // For a network of 5, 4 seals are required (local + 3 remote).
        _peers.GetNonProposing(0).InjectCommit(RoundId, _proposedBlock);
        _peers.GetNonProposing(1).InjectCommit(RoundId, _proposedBlock);

        ValidatorPeer badSealPeer = _peers.GetNonProposing(2);
        Signature illegalSeal = badSealPeer.Sign(Keccak.Zero);
        badSealPeer.InjectCommit(RoundId, _context.Digest(_proposedBlock), illegalSeal);
        Assert.That(_context.CurrentChainHeight, Is.EqualTo(0));

        badSealPeer.InjectCommit(RoundId, _proposedBlock);
        Assert.That(_context.CurrentChainHeight, Is.EqualTo(1));
    }
}
