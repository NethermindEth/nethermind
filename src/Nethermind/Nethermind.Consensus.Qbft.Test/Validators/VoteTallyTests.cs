// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Validators;
using Nethermind.Core;
using NUnit.Framework;

namespace Nethermind.Consensus.Qbft.Test.Validators;

/// <summary>Port of Besu's <c>VoteTallyTest</c>.</summary>
[Parallelizable(ParallelScope.All)]
public class VoteTallyTests
{
    private static readonly Address Validator1 = new("0x000d836201318ec6899a67540690382780743280");
    private static readonly Address Validator2 = new("0x001762430ea9c3a26e5749afdb70da5f78ddbb8c");
    private static readonly Address Validator3 = new("0x001d14804b399c6ef80e64576f657660804fec0b");
    private static readonly Address Validator4 = new("0x0032403587947b9f15622a68d104d54d33dbd1cd");
    private static readonly Address Validator5 = new("0x00497e92cdc0e0b963d752b2296acb87da828b24");

    private static VoteTally FourValidators() => new([Validator1, Validator2, Validator3, Validator4]);

    private static ValidatorVote Add(Address proposer, Address recipient) => new(VoteType.Add, proposer, recipient);
    private static ValidatorVote Drop(Address proposer, Address recipient) => new(VoteType.Drop, proposer, recipient);

    [Test]
    public void ValidatorsAreNotAddedBeforeRequiredVoteCountReached()
    {
        VoteTally tally = FourValidators();
        tally.AddVote(Add(Validator1, Validator5));
        tally.AddVote(Add(Validator2, Validator5));
        Assert.That(tally.Validators, Is.EqualTo(new[] { Validator1, Validator2, Validator3, Validator4 }));
    }

    [Test]
    public void ValidatorAddedToListWhenMoreThanHalfOfProposersVoteToAdd()
    {
        VoteTally tally = FourValidators();
        tally.AddVote(Add(Validator1, Validator5));
        tally.AddVote(Add(Validator2, Validator5));
        tally.AddVote(Add(Validator3, Validator5));
        Assert.That(tally.Validators, Is.EqualTo(new[] { Validator1, Validator2, Validator3, Validator4, Validator5 }));
    }

    [Test]
    public void ValidatorsAreAddedInCorrectOrder()
    {
        VoteTally tally = new([Validator1, Validator2, Validator3, Validator5]);
        tally.AddVote(Add(Validator1, Validator4));
        tally.AddVote(Add(Validator2, Validator4));
        tally.AddVote(Add(Validator3, Validator4));
        Assert.That(tally.Validators, Is.EqualTo(new[] { Validator1, Validator2, Validator3, Validator4, Validator5 }));
    }

    [Test]
    public void DuplicateVotesFromSameProposerAreIgnored()
    {
        VoteTally tally = FourValidators();
        tally.AddVote(Add(Validator1, Validator5));
        tally.AddVote(Add(Validator2, Validator5));
        tally.AddVote(Add(Validator2, Validator5));
        Assert.That(tally.Validators, Is.EqualTo(new[] { Validator1, Validator2, Validator3, Validator4 }));
    }

    [Test]
    public void ProposerChangingAddVoteToDropBeforeLimitReachedDiscardsAddVote()
    {
        VoteTally tally = FourValidators();
        tally.AddVote(Add(Validator1, Validator5));
        tally.AddVote(Drop(Validator1, Validator5));
        tally.AddVote(Add(Validator2, Validator5));
        tally.AddVote(Add(Validator3, Validator5));
        Assert.That(tally.Validators, Is.EqualTo(new[] { Validator1, Validator2, Validator3, Validator4 }));
    }

    [Test]
    public void ProposerChangingAddVoteToDropAfterLimitReachedPreservesAddVote()
    {
        VoteTally tally = FourValidators();
        tally.AddVote(Add(Validator1, Validator5));
        tally.AddVote(Add(Validator2, Validator5));
        tally.AddVote(Add(Validator3, Validator5));
        tally.AddVote(Drop(Validator1, Validator5));
        Assert.That(tally.Validators, Is.EqualTo(new[] { Validator1, Validator2, Validator3, Validator4, Validator5 }));
    }

    [Test]
    public void ClearVotesAboutAValidatorWhenItIsAdded()
    {
        VoteTally tally = FourValidators();
        tally.AddVote(Add(Validator1, Validator5));
        tally.AddVote(Add(Validator2, Validator5));
        tally.AddVote(Add(Validator3, Validator5));
        Assert.That(tally.Validators, Is.EqualTo(new[] { Validator1, Validator2, Validator3, Validator4, Validator5 }));

        tally.AddVote(Drop(Validator2, Validator5));
        tally.AddVote(Drop(Validator3, Validator5));
        tally.AddVote(Drop(Validator4, Validator5));
        Assert.That(tally.Validators, Is.EqualTo(new[] { Validator1, Validator2, Validator3, Validator4 }));

        // Validator1's earlier add vote was discarded, so two fresh votes are not enough.
        tally.AddVote(Add(Validator2, Validator5));
        tally.AddVote(Add(Validator3, Validator5));
        Assert.That(tally.Validators, Is.EqualTo(new[] { Validator1, Validator2, Validator3, Validator4 }));
    }

    [Test]
    public void RequiresASingleVoteWhenThereIsOnlyOneValidator()
    {
        VoteTally tally = new([Validator1]);
        tally.AddVote(Add(Validator1, Validator2));
        Assert.That(tally.Validators, Is.EqualTo(new[] { Validator1, Validator2 }));
    }

    [Test]
    public void RequiresTwoVotesWhenThereAreTwoValidators()
    {
        VoteTally tally = new([Validator1, Validator2]);
        tally.AddVote(Add(Validator1, Validator3));
        Assert.That(tally.Validators, Is.EqualTo(new[] { Validator1, Validator2 }));
        tally.AddVote(Add(Validator2, Validator3));
        Assert.That(tally.Validators, Is.EqualTo(new[] { Validator1, Validator2, Validator3 }));
    }

    [TestCase(VoteType.Add)]
    [TestCase(VoteType.Drop)]
    public void DiscardOutstandingVotesResetsTheCount(VoteType type)
    {
        Address recipient = type == VoteType.Add ? Validator5 : Validator4;
        VoteTally tally = FourValidators();
        tally.AddVote(new ValidatorVote(type, Validator1, recipient));
        tally.AddVote(new ValidatorVote(type, Validator2, recipient));
        tally.DiscardOutstandingVotes();
        tally.AddVote(new ValidatorVote(type, Validator3, recipient));
        Assert.That(tally.Validators, Is.EqualTo(new[] { Validator1, Validator2, Validator3, Validator4 }));
    }

    [Test]
    public void ValidatorsAreNotRemovedBeforeRequiredVoteCountReached()
    {
        VoteTally tally = FourValidators();
        tally.AddVote(Drop(Validator1, Validator4));
        tally.AddVote(Drop(Validator2, Validator4));
        Assert.That(tally.Validators, Is.EqualTo(new[] { Validator1, Validator2, Validator3, Validator4 }));
    }

    [Test]
    public void ValidatorRemovedFromListWhenMoreThanHalfOfProposersVoteToDrop()
    {
        VoteTally tally = FourValidators();
        tally.AddVote(Drop(Validator1, Validator4));
        tally.AddVote(Drop(Validator2, Validator4));
        tally.AddVote(Drop(Validator3, Validator4));
        Assert.That(tally.Validators, Is.EqualTo(new[] { Validator1, Validator2, Validator3 }));
    }

    [Test]
    public void DroppingANonValidatorChangesNothing()
    {
        VoteTally tally = new([Validator1, Validator2, Validator4]);
        tally.AddVote(Drop(Validator1, Validator3));
        tally.AddVote(Drop(Validator2, Validator3));
        tally.AddVote(Drop(Validator4, Validator3));
        Assert.That(tally.Validators, Is.EqualTo(new[] { Validator1, Validator2, Validator4 }));
    }

    [Test]
    public void ProposerChangingDropVoteToAddBeforeLimitReachedDiscardsDropVote()
    {
        VoteTally tally = FourValidators();
        tally.AddVote(Drop(Validator1, Validator4));
        tally.AddVote(Add(Validator1, Validator4));
        tally.AddVote(Drop(Validator2, Validator4));
        tally.AddVote(Drop(Validator3, Validator4));
        Assert.That(tally.Validators, Is.EqualTo(new[] { Validator1, Validator2, Validator3, Validator4 }));
    }

    [Test]
    public void ProposerChangingDropVoteToAddAfterLimitReachedPreservesDropVote()
    {
        VoteTally tally = FourValidators();
        tally.AddVote(Drop(Validator1, Validator4));
        tally.AddVote(Drop(Validator2, Validator4));
        tally.AddVote(Drop(Validator3, Validator4));
        tally.AddVote(Add(Validator1, Validator4));
        Assert.That(tally.Validators, Is.EqualTo(new[] { Validator1, Validator2, Validator3 }));
    }

    [Test]
    public void RemovedValidatorsVotesAreDiscarded()
    {
        VoteTally tally = FourValidators();
        tally.AddVote(Add(Validator4, Validator5));
        tally.AddVote(Drop(Validator4, Validator3));
        tally.AddVote(Drop(Validator1, Validator4));
        tally.AddVote(Drop(Validator2, Validator4));
        tally.AddVote(Drop(Validator3, Validator4));
        Assert.That(tally.Validators, Is.EqualTo(new[] { Validator1, Validator2, Validator3 }));

        // Adding now needs 2 votes (>50% of 3) but validator4's votes no longer count.
        tally.AddVote(Add(Validator1, Validator5));
        tally.AddVote(Drop(Validator1, Validator3));
        Assert.That(tally.Validators, Is.EqualTo(new[] { Validator1, Validator2, Validator3 }));
    }

    [Test]
    public void ClearVotesAboutAValidatorWhenItIsDropped()
    {
        VoteTally tally = new([Validator1, Validator2, Validator3, Validator4, Validator5]);
        tally.AddVote(Drop(Validator1, Validator5));
        tally.AddVote(Drop(Validator2, Validator5));
        tally.AddVote(Drop(Validator3, Validator5));
        Assert.That(tally.Validators, Is.EqualTo(new[] { Validator1, Validator2, Validator3, Validator4 }));

        tally.AddVote(Add(Validator2, Validator5));
        tally.AddVote(Add(Validator3, Validator5));
        tally.AddVote(Add(Validator4, Validator5));
        Assert.That(tally.Validators, Is.EqualTo(new[] { Validator1, Validator2, Validator3, Validator4, Validator5 }));

        tally.AddVote(Drop(Validator2, Validator5));
        tally.AddVote(Drop(Validator3, Validator5));
        Assert.That(tally.Validators, Is.EqualTo(new[] { Validator1, Validator2, Validator3, Validator4, Validator5 }));
    }

    [Test]
    public void TrackMultipleOngoingVotesIndependently()
    {
        VoteTally tally = FourValidators();
        tally.AddVote(Add(Validator1, Validator5));
        tally.AddVote(Drop(Validator1, Validator3));
        tally.AddVote(Add(Validator2, Validator5));
        tally.AddVote(Drop(Validator2, Validator1));
        Assert.That(tally.Validators, Is.EqualTo(new[] { Validator1, Validator2, Validator3, Validator4 }));

        tally.AddVote(Add(Validator3, Validator5));
        tally.AddVote(Drop(Validator3, Validator1));
        Assert.That(tally.Validators, Is.EqualTo(new[] { Validator1, Validator2, Validator3, Validator4, Validator5 }));

        tally.AddVote(Add(Validator4, Validator5));
        tally.AddVote(Drop(Validator4, Validator1));
        Assert.That(tally.Validators, Is.EqualTo(new[] { Validator2, Validator3, Validator4, Validator5 }));
    }

    [Test]
    public void CopyIsIndependent()
    {
        VoteTally tally = FourValidators();
        tally.AddVote(Add(Validator1, Validator5));
        VoteTally copy = tally.Copy();
        copy.AddVote(Add(Validator2, Validator5));
        copy.AddVote(Add(Validator3, Validator5));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(copy.Validators, Does.Contain(Validator5));
            Assert.That(tally.Validators, Does.Not.Contain(Validator5));
            Assert.That(tally.GetOutstandingAddVotesFor(Validator5), Is.EqualTo(new[] { Validator1 }));
        }
    }
}
