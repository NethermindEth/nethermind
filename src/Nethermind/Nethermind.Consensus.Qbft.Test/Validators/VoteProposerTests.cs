// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Validators;
using Nethermind.Core;
using NUnit.Framework;

namespace Nethermind.Consensus.Qbft.Test.Validators;

/// <summary>Port of Besu's <c>VoteProposerTest</c>.</summary>
[Parallelizable(ParallelScope.All)]
public class VoteProposerTests
{
    private static readonly Address Local = QbftTestData.Addr(0);
    private static readonly Address A1 = QbftTestData.Addr(1);
    private static readonly Address A2 = QbftTestData.Addr(2);
    private static readonly Address A3 = QbftTestData.Addr(3);
    private static readonly Address A4 = QbftTestData.Addr(4);

    private static ValidatorVote Add(Address recipient) => new(VoteType.Add, Local, recipient);
    private static ValidatorVote Drop(Address recipient) => new(VoteType.Drop, Local, recipient);

    [Test]
    public void EmptyProposerReturnsNoVotes()
    {
        VoteProposer proposer = new();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(proposer.GetVote(Local, new VoteTally([])), Is.Null);
            Assert.That(proposer.GetVote(Local, new VoteTally([Local, A1, A2])), Is.Null);
        }
    }

    [Test]
    public void DemoteVotes()
    {
        VoteProposer proposer = new();
        proposer.Drop(A1);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(proposer.GetVote(Local, new VoteTally([])), Is.Null);
            Assert.That(proposer.GetVote(Local, new VoteTally([A1])), Is.EqualTo(Drop(A1)));
            Assert.That(proposer.GetVote(Local, new VoteTally([A1, A2, A3])), Is.EqualTo(Drop(A1)));
            Assert.That(proposer.GetVote(Local, new VoteTally([A2, A3])), Is.Null);
        }
    }

    [Test]
    public void PromoteVotes()
    {
        VoteProposer proposer = new();
        proposer.Auth(A1);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(proposer.GetVote(Local, new VoteTally([])), Is.EqualTo(Add(A1)));
            Assert.That(proposer.GetVote(Local, new VoteTally([A1])), Is.Null);
            Assert.That(proposer.GetVote(Local, new VoteTally([A1, A2, A3])), Is.Null);
            Assert.That(proposer.GetVote(Local, new VoteTally([A2, A3])), Is.EqualTo(Add(A1)));
        }
    }

    [Test]
    public void DiscardVotes()
    {
        VoteProposer proposer = new();
        proposer.Auth(A1);
        proposer.Auth(A2);
        proposer.Discard(A2);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(proposer.GetVote(Local, new VoteTally([])), Is.EqualTo(Add(A1)));
            Assert.That(proposer.GetVote(Local, new VoteTally([A1])), Is.Null);
            Assert.That(proposer.GetVote(Local, new VoteTally([A1, A2, A3])), Is.Null);
            Assert.That(proposer.GetVote(Local, new VoteTally([A2, A3])), Is.EqualTo(Add(A1)));
            Assert.That(proposer.GetProposals().Keys, Is.EquivalentTo(new[] { A1 }));
        }
    }

    [Test]
    public void GetVoteCyclesAllOptionsAndSkipsInvalidVotes()
    {
        VoteProposer proposer = new();
        proposer.Auth(A1);
        proposer.Auth(A2);
        proposer.Auth(A3);
        proposer.Drop(A4); // A4 is not a validator so the drop is never castable.

        ValidatorVote[] votes = [proposer.GetVote(Local, new VoteTally([]))!, proposer.GetVote(Local, new VoteTally([]))!, proposer.GetVote(Local, new VoteTally([]))!, proposer.GetVote(Local, new VoteTally([]))!];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(votes, Has.All.Property(nameof(ValidatorVote.Type)).EqualTo(VoteType.Add));
            Assert.That(new[] { votes[0].Recipient, votes[1].Recipient, votes[2].Recipient }, Is.EquivalentTo(new[] { A1, A2, A3 }), "every pending vote is cast once per cycle");
            Assert.That(votes[3], Is.EqualTo(votes[0]), "the cycle repeats");
        }
    }

    [Test]
    public void RevokesAuthVotesInTally()
    {
        VoteProposer proposer = new();
        proposer.Drop(A1);
        VoteTally tally = new([A2, A3]);
        tally.AddVote(Add(A1));
        Assert.That(proposer.GetVote(Local, tally), Is.EqualTo(Drop(A1)));
    }

    [Test]
    public void RevokesDropVotesInTally()
    {
        VoteProposer proposer = new();
        proposer.Auth(A1);
        VoteTally tally = new([A2, A3]);
        tally.AddVote(Drop(A1));
        Assert.That(proposer.GetVote(Local, tally), Is.EqualTo(Add(A1)));
    }

    [Test]
    public void RevokesAuthVotesInTallyWhenValidatorIsInValidatorList()
    {
        VoteProposer proposer = new();
        VoteTally tally = new([A1, A2, A3]);
        tally.AddVote(Add(A1));
        proposer.Drop(A1);
        Assert.That(proposer.GetVote(Local, tally), Is.EqualTo(Drop(A1)));
    }

    [Test]
    public void RevokesDropVotesInTallyWhenValidatorIsInValidatorList()
    {
        VoteProposer proposer = new();
        VoteTally tally = new([A1, A2, A3]);
        tally.AddVote(Drop(A1));
        proposer.Auth(A1);
        Assert.That(proposer.GetVote(Local, tally), Is.EqualTo(Add(A1)));
    }
}
