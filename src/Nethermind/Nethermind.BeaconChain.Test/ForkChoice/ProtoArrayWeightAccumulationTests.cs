// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.ForkChoice;
using Nethermind.Core.Crypto;
using static Nethermind.BeaconChain.Test.ForkChoice.ProtoArrayTestBlocks;
using static Nethermind.BeaconChain.Test.ForkChoice.TestHashes;

namespace Nethermind.BeaconChain.Test.ForkChoice;

/// <summary>Expected weights are hand-derived from effective balances, not production get_weight.</summary>
public class ProtoArrayWeightAccumulationTests
{
    private const ulong Gwei32Eth = 32_000_000_000;
    private const ulong SlotsPerEpoch = 32;

    private static readonly CheckpointRef Anchor = new(0, GetRoot(0));

    private static ProtoArrayForkChoice NewForkChoice(CheckpointRef? justified = null, CheckpointRef? finalized = null, ulong anchorSlot = 0) =>
        new(anchorSlot, anchorSlot, Hash256.Zero, justified ?? Anchor, finalized ?? Anchor, ExecutionStatus.Optimistic, Hash256.Zero, SlotsPerEpoch);

    private static ProtoBlock Block(ulong slot, Hash256 root, Hash256 parent, CheckpointRef? justified = null, CheckpointRef? finalized = null) =>
        CreateProtoBlock(slot, root, parent, justified ?? Anchor, finalized ?? Anchor, ExecutionStatus.Optimistic, root);

    private static ulong[] Balances(int count, ulong each = Gwei32Eth) => Enumerable.Repeat(each, count).ToArray();

    [Test]
    public void One_validator_one_vote_two_nodes()
    {
        ProtoArrayForkChoice fc = NewForkChoice();
        fc.ProcessBlock(Block(1, GetRoot(1), GetRoot(0)), 1, Anchor, Anchor);
        fc.ProcessAttestation(0, GetRoot(1), 1);

        Hash256 head = fc.GetHead(Anchor, Anchor, JustifiedBalances.FromEffectiveBalances(Balances(1)), null, 1);

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(head, Is.EqualTo(GetRoot(1)));
        Assert.That(fc.GetWeight(GetRoot(1)), Is.EqualTo(Gwei32Eth), "voted block");
        Assert.That(fc.GetWeight(GetRoot(0)), Is.EqualTo(Gwei32Eth), "ancestor receives the vote of its descendant");
    }

    [Test]
    public void Vote_moving_between_branches_is_deducted_from_the_old_branch()
    {
        ProtoArrayForkChoice fc = NewForkChoice();
        fc.ProcessBlock(Block(1, GetRoot(1), GetRoot(0)), 1, Anchor, Anchor);
        fc.ProcessBlock(Block(1, GetRoot(2), GetRoot(0)), 1, Anchor, Anchor);
        JustifiedBalances balances = JustifiedBalances.FromEffectiveBalances(Balances(1));

        fc.ProcessAttestation(0, GetRoot(1), 1);
        fc.GetHead(Anchor, Anchor, balances, null, 1);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(fc.GetWeight(GetRoot(1)), Is.EqualTo(Gwei32Eth));
            Assert.That(fc.GetWeight(GetRoot(2)), Is.EqualTo(0ul));
        }

        fc.ProcessAttestation(0, GetRoot(2), 2);
        fc.GetHead(Anchor, Anchor, balances, null, 1);
        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(fc.GetWeight(GetRoot(1)), Is.EqualTo(0ul), "old branch loses the vote");
        Assert.That(fc.GetWeight(GetRoot(2)), Is.EqualTo(Gwei32Eth), "new branch gains it");
        Assert.That(fc.GetWeight(GetRoot(0)), Is.EqualTo(Gwei32Eth), "common ancestor unchanged");
    }

    [Test]
    public void Same_or_lower_target_epoch_does_not_replace_the_latest_message()
    {
        ProtoArrayForkChoice fc = NewForkChoice();
        fc.ProcessBlock(Block(1, GetRoot(1), GetRoot(0)), 1, Anchor, Anchor);
        fc.ProcessBlock(Block(1, GetRoot(2), GetRoot(0)), 1, Anchor, Anchor);
        JustifiedBalances balances = JustifiedBalances.FromEffectiveBalances(Balances(1));

        fc.ProcessAttestation(0, GetRoot(1), 3);
        fc.ProcessAttestation(0, GetRoot(2), 3);
        fc.ProcessAttestation(0, GetRoot(2), 2);
        fc.GetHead(Anchor, Anchor, balances, null, 1);

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(fc.GetWeight(GetRoot(1)), Is.EqualTo(Gwei32Eth), "first vote at the highest epoch sticks");
        Assert.That(fc.GetWeight(GetRoot(2)), Is.EqualTo(0ul));
    }

    [Test]
    public void Effective_balance_change_between_epochs_is_reflected_without_a_vote_change()
    {
        ProtoArrayForkChoice fc = NewForkChoice();
        fc.ProcessBlock(Block(1, GetRoot(1), GetRoot(0)), 1, Anchor, Anchor);
        fc.ProcessAttestation(0, GetRoot(1), 1);

        fc.GetHead(Anchor, Anchor, JustifiedBalances.FromEffectiveBalances([Gwei32Eth]), null, 1);
        Assert.That(fc.GetWeight(GetRoot(1)), Is.EqualTo(Gwei32Eth));

        fc.GetHead(Anchor, Anchor, JustifiedBalances.FromEffectiveBalances([31_000_000_000]), null, 1);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(fc.GetWeight(GetRoot(1)), Is.EqualTo(31_000_000_000ul));
            Assert.That(fc.GetWeight(GetRoot(0)), Is.EqualTo(31_000_000_000ul));
        }

        fc.GetHead(Anchor, Anchor, JustifiedBalances.FromEffectiveBalances([0]), null, 1);
        Assert.That(fc.GetWeight(GetRoot(1)), Is.EqualTo(0ul));

        fc.GetHead(Anchor, Anchor, JustifiedBalances.FromEffectiveBalances([Gwei32Eth]), null, 1);
        Assert.That(fc.GetWeight(GetRoot(1)), Is.EqualTo(Gwei32Eth));
    }

    [Test]
    public void Balance_change_and_vote_move_in_the_same_delta_use_old_and_new_balances_respectively()
    {
        ProtoArrayForkChoice fc = NewForkChoice();
        fc.ProcessBlock(Block(1, GetRoot(1), GetRoot(0)), 1, Anchor, Anchor);
        fc.ProcessBlock(Block(1, GetRoot(2), GetRoot(0)), 1, Anchor, Anchor);

        fc.ProcessAttestation(0, GetRoot(1), 1);
        fc.GetHead(Anchor, Anchor, JustifiedBalances.FromEffectiveBalances([Gwei32Eth]), null, 1);

        fc.ProcessAttestation(0, GetRoot(2), 2);
        fc.GetHead(Anchor, Anchor, JustifiedBalances.FromEffectiveBalances([30_000_000_000]), null, 1);
        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(fc.GetWeight(GetRoot(1)), Is.EqualTo(0ul), "old balance removed from the old root");
        Assert.That(fc.GetWeight(GetRoot(2)), Is.EqualTo(30_000_000_000ul), "new balance added to the new root");
    }

    [Test]
    public void Validator_voting_for_the_first_time_after_others_have_settled()
    {
        ProtoArrayForkChoice fc = NewForkChoice();
        fc.ProcessBlock(Block(1, GetRoot(1), GetRoot(0)), 1, Anchor, Anchor);
        fc.ProcessBlock(Block(2, GetRoot(2), GetRoot(1)), 2, Anchor, Anchor);
        JustifiedBalances balances = JustifiedBalances.FromEffectiveBalances(Balances(16));

        for (ulong v = 0; v < 8; v++) fc.ProcessAttestation(v, GetRoot(1), 1);
        fc.GetHead(Anchor, Anchor, balances, null, 2);
        Assert.That(fc.GetWeight(GetRoot(1)), Is.EqualTo(8 * Gwei32Eth));

        for (ulong v = 8; v < 16; v++) fc.ProcessAttestation(v, GetRoot(2), 1);
        fc.GetHead(Anchor, Anchor, balances, null, 2);
        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(fc.GetWeight(GetRoot(2)), Is.EqualTo(8 * Gwei32Eth));
        Assert.That(fc.GetWeight(GetRoot(1)), Is.EqualTo(16 * Gwei32Eth));
        Assert.That(fc.GetWeight(GetRoot(0)), Is.EqualTo(16 * Gwei32Eth));
    }

    [Test]
    public void New_validator_appearing_only_in_the_new_balances_is_counted_at_its_new_balance()
    {
        ProtoArrayForkChoice fc = NewForkChoice();
        fc.ProcessBlock(Block(1, GetRoot(1), GetRoot(0)), 1, Anchor, Anchor);

        fc.ProcessAttestation(0, GetRoot(1), 1);
        fc.GetHead(Anchor, Anchor, JustifiedBalances.FromEffectiveBalances([Gwei32Eth]), null, 1);

        fc.ProcessAttestation(1, GetRoot(1), 1);
        fc.GetHead(Anchor, Anchor, JustifiedBalances.FromEffectiveBalances([Gwei32Eth, Gwei32Eth]), null, 1);
        Assert.That(fc.GetWeight(GetRoot(1)), Is.EqualTo(2 * Gwei32Eth));
    }

    [Test]
    public void Deep_chain_propagates_a_tip_vote_to_every_ancestor()
    {
        const int depth = 40;
        ProtoArrayForkChoice fc = NewForkChoice();
        for (ulong i = 1; i <= depth; i++) fc.ProcessBlock(Block(i, GetRoot(i), GetRoot(i - 1)), i, Anchor, Anchor);

        fc.ProcessAttestation(0, GetRoot(depth), 2);
        fc.ProcessAttestation(1, GetRoot(depth / 2), 2);
        fc.GetHead(Anchor, Anchor, JustifiedBalances.FromEffectiveBalances(Balances(2)), null, depth);

        using (Assert.EnterMultipleScope())
        {
            for (ulong i = 0; i <= depth; i++)
            {
                ulong expected = i <= depth / 2 ? 2 * Gwei32Eth : Gwei32Eth;
                Assert.That(fc.GetWeight(GetRoot(i)), Is.EqualTo(expected), $"node {i}");
            }
        }
    }

    /// <summary>Independent weight oracle: two eight-member committees give E 512e9, above the 409.6e9 parent threshold; F has zero.</summary>
    [Test]
    public void Fixture_mirror_parent_collects_two_committees_and_clears_the_160_percent_threshold()
    {
        (ProtoArrayForkChoice fc, JustifiedBalances balances, CheckpointRef justified, CheckpointRef finalized) = BuildFixtureMirror(includeBlockBodyVotesForParent: true);

        Hash256 head = fc.GetHead(justified, finalized, balances, null, 132);
        ulong parentThreshold = fc.CalculateCommitteeFraction(balances, ForkChoiceRunner.ReorgParentWeightThresholdPercent);
        ulong headThreshold = fc.CalculateCommitteeFraction(balances, ForkChoiceRunner.ReorgHeadWeightThresholdPercent);

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(head, Is.EqualTo(GetRoot(131)), "F is the LMD head");
        Assert.That(parentThreshold, Is.EqualTo(409_600_000_000ul), "160% of one committee (8192e9 / 32 * 1.6)");
        Assert.That(headThreshold, Is.EqualTo(51_200_000_000ul), "20% of one committee");
        Assert.That(fc.GetWeight(GetRoot(131)), Is.EqualTo(0ul), "late head F has no votes");
        Assert.That(fc.GetWeight(GetRoot(130)), Is.EqualTo(512_000_000_000ul), "parent E: committees 130 and 131");
        Assert.That(fc.GetWeight(GetRoot(129)), Is.EqualTo(768_000_000_000ul), "D: committees 129, 130, 131");
        Assert.That(fc.GetWeight(GetRoot(128)), Is.EqualTo(768_000_000_000ul), "C: same as D");
        Assert.That(fc.GetWeight(GetRoot(127)), Is.EqualTo(256 * Gwei32Eth), "B: every latest message is B or a descendant");
        Assert.That(fc.GetWeight(GetRoot(96)), Is.EqualTo(256 * Gwei32Eth), "anchor A");
        Assert.That(fc.GetWeight(GetRoot(130)), Is.GreaterThan(parentThreshold), "parent_strong");
        Assert.That(fc.GetWeight(GetRoot(131)), Is.LessThan(headThreshold), "head_weak");
    }

    /// <summary>Omitting F's body votes leaves one gossip committee: E has 256e9.</summary>
    [Test]
    public void Fixture_mirror_without_block_body_votes_yields_exactly_one_committee()
    {
        (ProtoArrayForkChoice fc, JustifiedBalances balances, CheckpointRef justified, CheckpointRef finalized) = BuildFixtureMirror(includeBlockBodyVotesForParent: false);

        fc.GetHead(justified, finalized, balances, null, 132);
        ulong parentThreshold = fc.CalculateCommitteeFraction(balances, ForkChoiceRunner.ReorgParentWeightThresholdPercent);

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(fc.GetWeight(GetRoot(130)), Is.EqualTo(256_000_000_000ul), "parent E: committee 131 only");
        Assert.That(fc.GetWeight(GetRoot(130)), Is.LessThan(parentThreshold), "parent_strong fails, as observed in the failing vector");
    }

    private static (ProtoArrayForkChoice, JustifiedBalances, CheckpointRef, CheckpointRef) BuildFixtureMirror(bool includeBlockBodyVotesForParent)
    {
        CheckpointRef justified = new(3, GetRoot(96));
        CheckpointRef finalized = new(2, GetRoot(96));
        JustifiedBalances balances = JustifiedBalances.FromEffectiveBalances(Balances(256));
        ProtoArrayForkChoice fc = NewForkChoice(justified, finalized, anchorSlot: 96);

        void AddBlock(ulong slot, ulong parentSlot) =>
            fc.ProcessBlock(Block(slot, GetRoot(slot), GetRoot(parentSlot), justified, finalized), slot, justified, finalized);
        void Committee(int k, Hash256 root, ulong epoch)
        {
            for (ulong v = (ulong)k * 8; v < (ulong)(k + 1) * 8; v++) fc.ProcessAttestation(v, root, epoch);
        }
        void Settle(ulong slot, Hash256? boost = null)
        {
            if (boost is { } b) fc.SetProposerBoostRoot(b); else fc.ResetProposerBoostRoot();
            fc.GetHead(justified, finalized, balances, null, slot);
        }

        AddBlock(127, 96);
        for (int k = 0; k < 32; k++) Committee(k, GetRoot(127), 3);
        Settle(127);
        Assert.That(fc.GetWeight(GetRoot(127)), Is.EqualTo(256 * Gwei32Eth), "precondition: all epoch-3 votes on B");

        AddBlock(128, 127);
        Committee(31, GetRoot(127), 3);
        Settle(128);

        AddBlock(129, 128);
        Settle(129);

        AddBlock(130, 129);
        Committee(1, GetRoot(129), 4);
        Settle(130, boost: GetRoot(130));

        Settle(131);
        AddBlock(131, 130);
        if (includeBlockBodyVotesForParent) Committee(2, GetRoot(130), 4);
        Settle(131);

        Committee(3, GetRoot(130), 4);

        return (fc, balances, justified, finalized);
    }
}
