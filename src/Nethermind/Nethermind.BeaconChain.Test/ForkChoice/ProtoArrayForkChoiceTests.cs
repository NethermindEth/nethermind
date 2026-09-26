// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.Core.Crypto;
using NUnit.Framework;
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

        ProtoBlock NewBlock(Hash256 root) => new(
            Slot: 1,
            Root: root,
            ParentRoot: GetRoot(0),
            StateRoot: Hash256.Zero,
            JustifiedCheckpoint: anchor,
            FinalizedCheckpoint: anchor,
            ExecutionStatus: ExecutionStatus.Optimistic,
            ExecutionBlockHash: root,
            UnrealizedJustifiedCheckpoint: null,
            UnrealizedFinalizedCheckpoint: null);
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
            new ProtoBlock(
                Slot: 1,
                Root: GetRoot(2),
                ParentRoot: GetRoot(0),
                StateRoot: Hash256.Zero,
                JustifiedCheckpoint: anchor,
                FinalizedCheckpoint: anchor,
                ExecutionStatus: ExecutionStatus.Optimistic,
                ExecutionBlockHash: GetRoot(2),
                UnrealizedJustifiedCheckpoint: unrealized,
                UnrealizedFinalizedCheckpoint: anchor),
            1, anchor, anchor);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(forkChoice.GetParentRoot(GetRoot(2)), Is.EqualTo(GetRoot(0)), "child's parent");
            Assert.That(forkChoice.GetParentRoot(GetRoot(0)), Is.Null, "anchor has no parent");
            Assert.That(forkChoice.GetParentRoot(GetRoot(9)), Is.Null, "unknown block");
            Assert.That(forkChoice.GetUnrealizedJustifiedCheckpoint(GetRoot(2)), Is.EqualTo(unrealized), "carried through from ProcessBlock");
            Assert.That(forkChoice.GetUnrealizedJustifiedCheckpoint(GetRoot(9)), Is.Null, "unknown block");
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(forkChoice.CalculateCommitteeFraction(balances, ForkChoiceRunner.ReorgHeadWeightThresholdPercent), Is.EqualTo(40ul));
            Assert.That(forkChoice.CalculateCommitteeFraction(balances, ForkChoiceRunner.ReorgParentWeightThresholdPercent), Is.EqualTo(320ul));
        }
    }
}
