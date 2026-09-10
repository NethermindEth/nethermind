// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Messages;
using Nethermind.Consensus.Qbft.StateMachine;
using Nethermind.Consensus.Qbft.Validation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Qbft.Test.StateMachine;

/// <summary>Port of Besu's <c>RoundStateTest</c>.</summary>
[Parallelizable(ParallelScope.Self)]
public class RoundStateTests
{
    private static readonly ConsensusRoundIdentifier RoundIdentifier = new(1, 1);
    private readonly List<PrivateKey> _keys = QbftTestData.Keys(3);
    private readonly List<MessageFactory> _factories = [];
    private readonly IMessageValidator _validator = Substitute.For<IMessageValidator>();
    private Block _block = null!;
    private Hash256 _digest = null!;

    [SetUp]
    public void Setup()
    {
        _factories.Clear();
        foreach (PrivateKey key in _keys) _factories.Add(QbftTestMessages.Factory(key));
        _block = QbftTestMessages.Block(1, _keys[0].Address, QbftTestMessages.Addresses(_keys), RoundIdentifier.Round);
        _digest = QbftTestMessages.Digest(_block);
    }

    private Proposal Proposal(int factory = 0) => _factories[factory].CreateProposal(RoundIdentifier, _block, null, [], []);
    private Prepare Prepare(int factory) => _factories[factory].CreatePrepare(RoundIdentifier, _digest);
    private Commit Commit(int factory) => _factories[factory].CreateCommit(RoundIdentifier, _digest, _factories[factory].CreateCommitSeal(_block, RoundIdentifier.Round));

    private RoundState CreateRoundState(int quorum) => new(RoundIdentifier, quorum, _validator, LimboLogs.Instance);

    [Test]
    public void DefaultRoundIsNotPreparedOrCommittedAndHasNoPreparedCertificate()
    {
        RoundState roundState = CreateRoundState(1);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(roundState.IsPrepared, Is.False);
            Assert.That(roundState.IsCommitted, Is.False);
            Assert.That(roundState.ConstructPreparedCertificate(), Is.Null);
        }
    }

    [Test]
    public void IfProposalMessageFailsValidationMethodReturnsFalse()
    {
        _validator.ValidateProposal(Arg.Any<Proposal>()).Returns(false);
        RoundState roundState = CreateRoundState(1);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(roundState.SetProposedBlock(Proposal()), Is.False);
            Assert.That(roundState.IsPrepared, Is.False);
            Assert.That(roundState.IsCommitted, Is.False);
            Assert.That(roundState.ConstructPreparedCertificate(), Is.Null);
        }
    }

    [Test]
    public void SingleValidatorRequiresCommitMessageToBeCommitted()
    {
        _validator.ValidateProposal(Arg.Any<Proposal>()).Returns(true);
        _validator.ValidateCommit(Arg.Any<Commit>()).Returns(true);
        RoundState roundState = CreateRoundState(1);
        Assert.That(roundState.SetProposedBlock(Proposal()), Is.True);
        // Unlike IBFT 2.0 the proposal does not count as a prepare: the proposer sends its own prepare.
        Assert.That(roundState.IsPrepared, Is.False);
        Assert.That(roundState.IsCommitted, Is.False);

        roundState.AddCommitMessage(Commit(0));
        Assert.That(roundState.IsPrepared, Is.False);
        Assert.That(roundState.IsCommitted, Is.True);
        Assert.That(roundState.CommitSeals, Has.Count.EqualTo(1));
    }

    [Test]
    public void PrepareMessagesCanBeReceivedPriorToProposal()
    {
        _validator.ValidateProposal(Arg.Any<Proposal>()).Returns(true);
        _validator.ValidatePrepare(Arg.Any<Prepare>()).Returns(true);
        RoundState roundState = CreateRoundState(3);
        roundState.AddPrepareMessage(Prepare(1));
        roundState.AddPrepareMessage(Prepare(2));
        Assert.That(roundState.IsPrepared, Is.False);

        Assert.That(roundState.SetProposedBlock(Proposal()), Is.True);
        Assert.That(roundState.IsPrepared, Is.False, "two prepares with quorum three");

        roundState.AddPrepareMessage(Prepare(0));
        Assert.That(roundState.IsPrepared, Is.True);
        Assert.That(roundState.ConstructPreparedCertificate()!.Prepares, Has.Count.EqualTo(3));
    }

    [Test]
    public void InvalidPriorPrepareMessagesAreDiscardedUponSubsequentProposal()
    {
        Prepare firstPrepare = Prepare(1);
        Prepare secondPrepare = Prepare(2);
        _validator.ValidateProposal(Arg.Any<Proposal>()).Returns(true);
        _validator.ValidatePrepare(firstPrepare).Returns(true);
        _validator.ValidatePrepare(secondPrepare).Returns(false);
        RoundState roundState = CreateRoundState(2);
        roundState.AddPrepareMessage(firstPrepare);
        roundState.AddPrepareMessage(secondPrepare);
        Assert.That(roundState.IsPrepared, Is.False);

        Assert.That(roundState.SetProposedBlock(Proposal()), Is.True);
        Assert.That(roundState.IsPrepared, Is.False, "the invalid prepare was dropped");
        Assert.That(roundState.ConstructPreparedCertificate(), Is.Null);
    }

    [Test]
    public void PrepareMessageIsValidatedAgainstExistingProposal()
    {
        Prepare firstPrepare = Prepare(1);
        Prepare secondPrepare = Prepare(2);
        Prepare thirdPrepare = Prepare(0);
        _validator.ValidateProposal(Arg.Any<Proposal>()).Returns(true);
        _validator.ValidatePrepare(firstPrepare).Returns(true);
        _validator.ValidatePrepare(secondPrepare).Returns(false);
        _validator.ValidatePrepare(thirdPrepare).Returns(true);
        RoundState roundState = CreateRoundState(2);
        roundState.SetProposedBlock(Proposal());
        roundState.AddPrepareMessage(firstPrepare);
        Assert.That(roundState.IsPrepared, Is.False);
        roundState.AddPrepareMessage(secondPrepare);
        Assert.That(roundState.IsPrepared, Is.False);
        roundState.AddPrepareMessage(thirdPrepare);
        Assert.That(roundState.IsPrepared, Is.True);
    }

    [Test]
    public void CommitSealsAreExtractedFromReceivedMessages()
    {
        _validator.ValidateProposal(Arg.Any<Proposal>()).Returns(true);
        _validator.ValidateCommit(Arg.Any<Commit>()).Returns(true);
        Commit firstCommit = Commit(1);
        Commit secondCommit = Commit(2);
        RoundState roundState = CreateRoundState(2);
        roundState.SetProposedBlock(Proposal());
        roundState.AddCommitMessage(firstCommit);
        Assert.That(roundState.IsCommitted, Is.False);
        roundState.AddCommitMessage(secondCommit);
        Assert.That(roundState.IsCommitted, Is.True);
        Assert.That(roundState.CommitSeals, Is.EqualTo(new[] { firstCommit.CommitSeal, secondCommit.CommitSeal }));
    }

    [Test]
    public void DuplicateAuthorsCountOnce()
    {
        _validator.ValidateProposal(Arg.Any<Proposal>()).Returns(true);
        _validator.ValidatePrepare(Arg.Any<Prepare>()).Returns(true);
        RoundState roundState = CreateRoundState(2);
        roundState.SetProposedBlock(Proposal());
        roundState.AddPrepareMessage(Prepare(1));
        roundState.AddPrepareMessage(Prepare(1));
        Assert.That(roundState.IsPrepared, Is.False);
    }

    [Test]
    public void SecondProposalIsRejected()
    {
        _validator.ValidateProposal(Arg.Any<Proposal>()).Returns(true);
        RoundState roundState = CreateRoundState(1);
        Assert.That(roundState.SetProposedBlock(Proposal()), Is.True);
        Assert.That(roundState.SetProposedBlock(Proposal(1)), Is.False);
    }
}
