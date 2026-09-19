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
            TargetRoot: Hash256.Zero,
            JustifiedCheckpoint: anchor,
            FinalizedCheckpoint: anchor,
            ExecutionStatus: ExecutionStatus.Optimistic,
            ExecutionBlockHash: root,
            UnrealizedJustifiedCheckpoint: null,
            UnrealizedFinalizedCheckpoint: null);
    }
}
