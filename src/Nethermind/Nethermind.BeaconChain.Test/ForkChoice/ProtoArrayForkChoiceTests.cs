// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.Core.Crypto;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.ForkChoice.ProtoArrayTestBlocks;
using static Nethermind.BeaconChain.Test.ForkChoice.TestHashes;

namespace Nethermind.BeaconChain.Test.ForkChoice;

public class ProtoArrayForkChoiceTests
{
    private static IEnumerable<TestCaseData> Suites()
    {
        yield return new TestCaseData(NoVotesTestDefinition.Get()).SetName("no_votes");
        yield return new TestCaseData(VotesTestDefinition.Get()).SetName("votes");
        yield return new TestCaseData(FfgUpdatesTestDefinition.GetCase01()).SetName("ffg_case_01");
        yield return new TestCaseData(FfgUpdatesTestDefinition.GetCase02()).SetName("ffg_case_02");
        yield return new TestCaseData(ExecutionStatusTestDefinition.Get01()).SetName("execution_status_01");
        yield return new TestCaseData(ExecutionStatusTestDefinition.Get02()).SetName("execution_status_02");
        yield return new TestCaseData(ExecutionStatusTestDefinition.Get03()).SetName("execution_status_03");
    }

    [TestCaseSource(nameof(Suites))]
    public void Runs_lighthouse_fork_choice_suite(ForkChoiceTestDefinition definition) => definition.Run();

    [Test]
    public void Mid_epoch_bootstrap_finality_matches_only_the_anchor_checkpoint()
    {
        CheckpointRef bootstrap = new(1, GetRoot(0));
        CheckpointRef older = new(0, GetRoot(9));
        ProtoArray tree = new(slotsPerEpoch: 32, proposerScoreBoostPercent: 40);
        tree.OnBlock(CreateProtoBlock(33, GetRoot(0), null, bootstrap, bootstrap, ExecutionStatus.Optimistic, GetRoot(0)), 35, bootstrap, bootstrap);
        tree.OnBlock(CreateProtoBlock(35, GetRoot(1), GetRoot(0), older, older, ExecutionStatus.Optimistic, GetRoot(1)), 35, bootstrap, bootstrap);
        tree.OnBlock(CreateProtoBlock(34, GetRoot(2), GetRoot(8), older, older, ExecutionStatus.Optimistic, GetRoot(2)), 35, bootstrap, bootstrap);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(tree.IsFinalizedCheckpointOrDescendant(GetRoot(1), bootstrap), Is.True);
        Assert.That(tree.IsFinalizedCheckpointOrDescendant(GetRoot(9), bootstrap), Is.False);
        Assert.That(tree.IsFinalizedCheckpointOrDescendant(GetRoot(2), bootstrap), Is.False);
        Assert.That(tree.IsFinalizedCheckpointOrDescendant(GetRoot(1), new CheckpointRef(1, GetRoot(1))), Is.False);
        Assert.That(tree.IsFinalizedCheckpointOrDescendant(GetRoot(1), new CheckpointRef(2, GetRoot(0))), Is.False);
    }

    [Test]
    public void Pruning_does_not_make_a_mid_epoch_node_its_own_bootstrap_checkpoint()
    {
        CheckpointRef anchor = new(0, GetRoot(0));
        ProtoArray tree = new(slotsPerEpoch: 32, proposerScoreBoostPercent: 40) { PruneThreshold = 0 };
        tree.OnBlock(CreateProtoBlock(0, GetRoot(0), null, anchor, anchor, ExecutionStatus.Optimistic, GetRoot(0)), 35, anchor, anchor);
        tree.OnBlock(CreateProtoBlock(33, GetRoot(1), GetRoot(0), anchor, anchor, ExecutionStatus.Optimistic, GetRoot(1)), 35, anchor, anchor);
        tree.Prune(GetRoot(1));

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(tree.Nodes, Has.Count.EqualTo(1));
        Assert.That(tree.Nodes[0].Parent, Is.Null);
        Assert.That(tree.IsFinalizedCheckpointOrDescendant(GetRoot(1), new CheckpointRef(1, GetRoot(1))), Is.False);
    }

    [Test]
    public void Attester_slashing_removes_the_vote_once_and_keeps_the_validator_out()
    {
        // The Lighthouse vectors never exercise equivocation, so cover it here: build
        // 0 <- (2 | 1) with one vote on each fork, then slash the validator voting for 2.
        CheckpointRef anchor = new(1, GetRoot(0));
        JustifiedBalances balances = JustifiedBalances.FromEffectiveBalances([1, 1]);
        ProtoArrayForkChoice forkChoice = new(0, 0, Hash256.Zero, anchor, anchor, ExecutionStatus.Optimistic, Hash256.Zero);

        forkChoice.ProcessBlock(NewBlock(GetRoot(2)), 1, anchor, anchor);
        forkChoice.ProcessBlock(NewBlock(GetRoot(1)), 1, anchor, anchor);
        forkChoice.ProcessAttestation(0, GetRoot(1), 2);
        forkChoice.ProcessAttestation(1, GetRoot(2), 2);

        Assert.That(forkChoice.GetHead(anchor, anchor, balances, null, 0), Is.EqualTo(GetRoot(2)), "tie broken towards the higher root");

        // Slashing validator #1 deducts its vote from 2, flipping the head to 1.
        forkChoice.OnAttesterSlashing([1ul]);
        Assert.That(forkChoice.GetHead(anchor, anchor, balances, null, 0), Is.EqualTo(GetRoot(1)), "slashed vote deducted");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(forkChoice.GetWeight(GetRoot(1)), Is.EqualTo(1ul), "remaining vote");
            Assert.That(forkChoice.GetWeight(GetRoot(2)), Is.EqualTo(0ul), "slashed vote removed");
        }

        // New attestations from the slashed validator are never counted again.
        forkChoice.ProcessAttestation(1, GetRoot(2), 3);
        Assert.That(forkChoice.GetHead(anchor, anchor, balances, null, 0), Is.EqualTo(GetRoot(1)), "slashed validator stays out");
        Assert.That(forkChoice.GetWeight(GetRoot(2)), Is.EqualTo(0ul), "no repeat counting");

        ProtoBlock NewBlock(Hash256 root) => CreateProtoBlock(1, root, GetRoot(0), anchor, anchor, ExecutionStatus.Optimistic, root);
    }

    /// <summary>
    /// The proposer reorg tie-break (<see cref="ForkChoiceRunner.GetProposerHead"/>) reads a
    /// block's parent and unrealized-justified checkpoint straight from the proto-array rather than keeping
    /// a second copy, so these accessors are the only thing standing between it and silently reading the
    /// wrong node.
    /// </summary>
    [Test]
    public void GetParentRoot_and_GetUnrealizedJustifiedCheckpoint_read_the_registered_node()
    {
        CheckpointRef anchor = new(0, GetRoot(0));
        CheckpointRef unrealized = new(1, GetRoot(1));
        ProtoArrayForkChoice forkChoice = new(0, 0, Hash256.Zero, anchor, anchor, ExecutionStatus.Optimistic, Hash256.Zero);

        forkChoice.ProcessBlock(
            CreateProtoBlock(1, GetRoot(2), GetRoot(0), anchor, anchor, ExecutionStatus.Optimistic, GetRoot(2),
                unrealizedJustified: unrealized, unrealizedFinalized: anchor),
            1, anchor, anchor);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(forkChoice.GetParentRoot(GetRoot(2)), Is.EqualTo(GetRoot(0)), "child's parent");
        Assert.That(forkChoice.GetParentRoot(GetRoot(0)), Is.Null, "anchor has no parent");
        Assert.That(forkChoice.GetParentRoot(GetRoot(9)), Is.Null, "unknown block");
        Assert.That(forkChoice.GetUnrealizedJustifiedCheckpoint(GetRoot(2)), Is.EqualTo(unrealized), "carried through from ProcessBlock");
        Assert.That(forkChoice.GetUnrealizedJustifiedCheckpoint(GetRoot(9)), Is.Null, "unknown block");
    }

    /// <summary>
    /// <see cref="ProtoArrayForkChoice.CalculateCommitteeFraction"/> is the only source the proposer reorg's
    /// head-weak/parent-strong thresholds have for "a percentage of one committee's share of the total
    /// active balance" - a wrong formula here silently shifts every reorg decision without failing any
    /// existing proposer-boost test, since boost already exercises it at a single fixed percentage (40).
    /// </summary>
    [Test]
    public void CalculateCommitteeFraction_is_percent_of_one_committees_share()
    {
        CheckpointRef anchor = new(0, GetRoot(0));
        ProtoArrayForkChoice forkChoice = new(
            0, 0, Hash256.Zero, anchor, anchor, ExecutionStatus.Optimistic, Hash256.Zero,
            slotsPerEpoch: 32);
        JustifiedBalances balances = JustifiedBalances.FromEffectiveBalances([3200, 3200]);

        // total = 6400; one committee's share = 6400 / 32 = 200; 20% of that = 40, 160% of that = 320.
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(forkChoice.CalculateCommitteeFraction(balances, ForkChoiceRunner.ReorgHeadWeightThresholdPercent), Is.EqualTo(40ul));
        Assert.That(forkChoice.CalculateCommitteeFraction(balances, ForkChoiceRunner.ReorgParentWeightThresholdPercent), Is.EqualTo(320ul));
    }

    /// <summary>
    /// A pre-Gloas vote is kept at its target epoch's first slot, which a later Gloas-slot vote must exceed to replace it. A
    /// start slot that overflows would wrap to a small slot, so the vote is refused and the latest message stays unset.
    /// </summary>
    [Test]
    public void A_vote_whose_target_epoch_start_slot_overflows_is_refused()
    {
        CheckpointRef anchor = new(0, GetRoot(0));
        ProtoArrayForkChoice forkChoice = new(0, 0, Hash256.Zero, anchor, anchor, ExecutionStatus.Optimistic, Hash256.Zero, slotsPerEpoch: 32);

        Assert.That(() => forkChoice.ProcessAttestation(0, GetRoot(0), ulong.MaxValue / 2), Throws.TypeOf<ProtoArrayException>());
        Assert.That(forkChoice.LatestMessage(0), Is.Null);
    }

    /// <summary>The last epoch whose start slot fits in 64 bits: <c>ulong.MaxValue / 32</c>, written out so the bound cannot drift with the production constant.</summary>
    private const ulong LastEpochWithAStartSlot = 576460752303423487;

    private static ulong[] CheckpointEpochsAroundTheBound() => [LastEpochWithAStartSlot, LastEpochWithAStartSlot + 1, ulong.MaxValue];

    /// <summary>
    /// A block naming a checkpoint epoch whose start slot overflows
    /// would wrap the finality checks or throw <see cref="OverflowException"/> mid-import. It is refused whole with the tree unchanged, and the epoch at
    /// the bound is still accepted.
    /// </summary>
    [Test]
    public void A_block_naming_a_checkpoint_epoch_is_refused_beyond_the_bound_and_accepted_at_it(
        [Values(0, 1, 2, 3)] int field,
        [ValueSource(nameof(CheckpointEpochsAroundTheBound))] ulong epoch)
    {
        bool refused = epoch > LastEpochWithAStartSlot;
        if (!refused)
            Assert.That(() => checked(epoch * 32), Throws.Nothing, "fixture bug: the accepted epoch's start slot must fit");
        CheckpointRef anchor = new(0, GetRoot(0));
        CheckpointRef beyond = new(epoch, GetRoot(0));
        ProtoArrayForkChoice forkChoice = new(0, 0, Hash256.Zero, anchor, anchor, ExecutionStatus.Optimistic, Hash256.Zero, slotsPerEpoch: 32);
        ProtoBlock block = CreateProtoBlock(1, GetRoot(1), GetRoot(0), field == 0 ? beyond : anchor, field == 1 ? beyond : anchor, ExecutionStatus.Optimistic, GetRoot(1),
            unrealizedJustified: field == 2 ? beyond : anchor, unrealizedFinalized: field == 3 ? beyond : anchor);

        if (refused)
            Assert.That(() => forkChoice.ProcessBlock(block, 1, anchor, anchor), Throws.TypeOf<ProtoArrayException>());
        else
            Assert.That(() => forkChoice.ProcessBlock(block, 1, anchor, anchor), Throws.Nothing);
        Assert.That(forkChoice.ContainsBlock(GetRoot(1)), Is.Not.EqualTo(refused));
    }

    /// <summary>A default <see cref="ScoreDeltas"/> has no arrays: it is refused as a proto-array error before any weight moves, never a <see cref="NullReferenceException"/>.</summary>
    [Test]
    public void Score_changes_without_delta_arrays_are_refused()
    {
        CheckpointRef anchor = new(0, GetRoot(0));
        ProtoArray protoArray = new(slotsPerEpoch: 32, proposerScoreBoostPercent: 40);
        protoArray.OnBlock(
            new ProtoBlock(0, GetRoot(0), null, Hash256.Zero, anchor, anchor, ExecutionStatus.Optimistic, Hash256.Zero, anchor, anchor),
            0, anchor, anchor);

        Assert.That(
            () => protoArray.ApplyScoreChanges(default, anchor, anchor, JustifiedBalances.FromEffectiveBalances([1]), Hash256.Zero, 0),
            Throws.TypeOf<ProtoArrayException>());
    }
}
