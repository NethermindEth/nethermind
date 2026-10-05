// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Test.StateTransition;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.Test.ForkChoice;

[HardTimeout(60_000)]
public class ForkChoiceRunnerGloasHeadTests
{
    public enum PayloadDecision
    {
        CurrentSlot,

        PtcQuorum,

        NoBoost,

        BoostBuildsOnFull,

        BoostBuildsOnEmpty,

        PtcTimelyDataUnavailable,
    }

    [Test]
    public void Payload_decision_of_a_verified_block_follows_should_extend_payload([Values] PayloadDecision decision)
    {
        GloasForkChoiceHarness harness = new();
        GloasForkChoiceHarness.Block first = harness.First;
        harness.TickTo(first.Slot);
        harness.Import(first);
        harness.Runner.OnExecutionPayloadVerified(first.Root);
        if (decision is PayloadDecision.PtcQuorum or PayloadDecision.PtcTimelyDataUnavailable)
            harness.AllPtcVote(first, payloadPresent: true, blobDataAvailable: decision == PayloadDecision.PtcQuorum);

        Hash256? child = null;
        if (decision != PayloadDecision.CurrentSlot)
        {
            harness.TickTo(first.Slot + 1);
            switch (decision)
            {
                case PayloadDecision.PtcQuorum:
                case PayloadDecision.PtcTimelyDataUnavailable:
                case PayloadDecision.BoostBuildsOnEmpty:
                case PayloadDecision.BoostBuildsOnFull:
                    GloasForkChoiceHarness.Block block = harness.Child(first, first.Slot + 1, full: decision == PayloadDecision.BoostBuildsOnFull, 0xC1);
                    harness.Import(block);
                    child = block.Root;
                    break;
            }
        }

        Assert.That(harness.Runner.ProposerBoostRoot == Hash256.Zero, Is.EqualTo(decision == PayloadDecision.NoBoost), "fixture bug");
        ForkChoiceNode expected = decision switch
        {
            PayloadDecision.BoostBuildsOnFull or PayloadDecision.BoostBuildsOnEmpty or PayloadDecision.PtcTimelyDataUnavailable => new ForkChoiceNode(child!, ForkChoicePayloadStatus.Empty),
            _ => new ForkChoiceNode(first.Root, ForkChoicePayloadStatus.Full),
        };
        Assert.That(harness.Runner.GetHeadNode(), Is.EqualTo(expected));
    }

    [Test]
    public void Payload_decision_ignores_a_boosted_block_on_another_parent()
    {
        GloasForkChoiceHarness harness = new();
        GloasForkChoiceHarness.Block first = harness.First;
        harness.TickTo(first.Slot + 1);
        harness.Import(first);
        GloasForkChoiceHarness.Block decided = harness.Child(first, first.Slot + 1, full: false, 0xC1);
        harness.Import(decided);
        harness.Runner.OnExecutionPayloadVerified(decided.Root);
        harness.CommitteeVotes(decided, decided.Slot, index: 0);

        harness.TickTo(decided.Slot + 1);
        GloasForkChoiceHarness.Block sibling = harness.Child(first, decided.Slot + 1, full: false, 0xD1);
        harness.Import(sibling);
        Assert.That(harness.Runner.ProposerBoostRoot, Is.EqualTo(sibling.Root), "fixture bug");

        Assert.That(harness.Runner.GetHeadNode(), Is.EqualTo(new ForkChoiceNode(decided.Root, ForkChoicePayloadStatus.Full)));
    }

    [Test]
    public void Payload_nodes_of_the_previous_slot_weigh_nothing([Values] bool previousSlot)
    {
        GloasForkChoiceHarness harness = new();
        GloasForkChoiceHarness.Block first = harness.First;
        harness.TickTo(first.Slot + 1);
        harness.Import(first);
        harness.Runner.OnExecutionPayloadVerified(first.Root);
        harness.CommitteeVotes(first, first.Slot + 1, index: 0);
        if (!previousSlot)
            harness.TickTo(first.Slot + 2);

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(harness.Runner.GetWeight(new ForkChoiceNode(first.Root, ForkChoicePayloadStatus.Empty)), Is.EqualTo(previousSlot ? 0 : GloasForkChoiceHarness.CommitteeWeight));
        Assert.That(harness.Runner.GetWeight(new ForkChoiceNode(first.Root, ForkChoicePayloadStatus.Pending)), Is.EqualTo(GloasForkChoiceHarness.CommitteeWeight));
        Assert.That(harness.Runner.GetHeadNode(), Is.EqualTo(new ForkChoiceNode(first.Root, previousSlot ? ForkChoicePayloadStatus.Full : ForkChoicePayloadStatus.Empty)));
    }

    public enum BoostCase
    {
        NoEquivocation,

        EquivocationBeforePtcDeadline,

        EquivocationAtPtcDeadline,

        EquivocationWithStrongParent,

        EquivocationWithParentTwoSlotsBack,
    }

    [Test]
    public void Proposer_boost_is_withheld_only_after_an_early_equivocation_by_a_weak_previous_slot_parent([Values] BoostCase boostCase)
    {
        GloasForkChoiceHarness harness = new();
        GloasForkChoiceHarness.Block first = harness.First;
        ulong parentSlot = first.Slot + 1;
        harness.TickTo(first.Slot);
        harness.Import(first);

        harness.TickTo(parentSlot);
        GloasForkChoiceHarness.Block parent = harness.Child(first, parentSlot, full: false, 0xA1);
        harness.Import(parent);
        if (boostCase == BoostCase.EquivocationWithStrongParent)
            harness.CommitteeVotes(parent, parentSlot, index: 0);

        ulong boostedSlot = parentSlot + (boostCase == BoostCase.EquivocationWithParentTwoSlotsBack ? 2ul : 1ul);
        if (boostCase != BoostCase.NoEquivocation)
        {
            // Every proposal in this fixture is validator 0's, so a PTC-timely block of the slot before the boosted one is an equivocation of the parent's proposer.
            harness.TickTo(boostedSlot - 1, boostCase == BoostCase.EquivocationAtPtcDeadline ? 9ul : 8ul);
            GloasForkChoiceHarness.Block equivocation = harness.Child(first, boostedSlot - 1, full: false, 0xA2);
            Assert.That(equivocation.ProposerIndex, Is.EqualTo(parent.ProposerIndex), "fixture bug: both blocks are the same proposer's");
            harness.Import(equivocation);
        }

        harness.TickTo(boostedSlot);
        GloasForkChoiceHarness.Block boosted = harness.Child(parent, boostedSlot, full: false, 0xB1);
        harness.Import(boosted);
        Assert.That(harness.Runner.ProposerBoostRoot, Is.EqualTo(boosted.Root), "fixture bug");
        Assert.That(harness.Runner.EnumerateAncestors(boosted.Root).Skip(1).First().Root, Is.EqualTo(parent.Root), "fixture bug");

        harness.Runner.OnExecutionPayloadVerified(boosted.Root);

        bool applies = boostCase != BoostCase.EquivocationBeforePtcDeadline;
        ulong boost = applies ? GloasForkChoiceHarness.ProposerScore : 0;
        ulong parentVotes = boostCase == BoostCase.EquivocationWithStrongParent ? GloasForkChoiceHarness.CommitteeWeight : 0;
        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(harness.Runner.GetWeight(new ForkChoiceNode(boosted.Root, ForkChoicePayloadStatus.Pending)), Is.EqualTo(boost));
        Assert.That(harness.Runner.GetWeight(new ForkChoiceNode(boosted.Root, ForkChoicePayloadStatus.Empty)), Is.Zero);
        Assert.That(harness.Runner.GetWeight(new ForkChoiceNode(boosted.Root, ForkChoicePayloadStatus.Full)), Is.Zero);
        Assert.That(harness.Runner.GetWeight(new ForkChoiceNode(first.Root, ForkChoicePayloadStatus.Empty)), Is.EqualTo(parentVotes + boost));
        Assert.That(harness.Runner.GetWeight(new ForkChoiceNode(first.Root, ForkChoicePayloadStatus.Full)), Is.Zero);
    }

    [Test]
    public void A_boosted_child_of_a_weak_gloas_anchor_keeps_its_boost()
    {
        GloasForkChoiceHarness harness = new(gloasAnchor: true);
        GloasForkChoiceHarness.Block anchor = harness.First;
        harness.TickTo(anchor.Slot + 1);
        GloasForkChoiceHarness.Block child = harness.Child(anchor, anchor.Slot + 1, full: false, 0xC1);
        harness.Import(child);
        Assert.That(harness.Runner.ProposerBoostRoot, Is.EqualTo(child.Root), "fixture bug");

        Assert.That(harness.Runner.GetWeight(new ForkChoiceNode(child.Root, ForkChoicePayloadStatus.Pending)), Is.EqualTo(GloasForkChoiceHarness.ProposerScore));
    }

    [Test]
    public void A_boosted_child_of_the_finalized_block_keeps_its_boost_after_pruning()
    {
        GloasForkChoiceHarness harness = new();
        GloasForkChoiceHarness.Block first = harness.First;
        harness.TickTo(first.Slot);
        harness.Import(first);
        harness.TickTo(first.Slot + 1);
        GloasForkChoiceHarness.Block child = harness.Child(first, first.Slot + 1, full: false, 0xC1);
        BeaconStateGloas finalizing = child.PostState.Clone();
        Checkpoint finalized = new() { Epoch = ForkCrossingChain.ForkEpoch, Root = first.Root };
        finalizing.CurrentJustifiedCheckpoint = finalized;
        finalizing.FinalizedCheckpoint = finalized;
        harness.Runner.OnBlock(child.Signed, finalizing);
        Assert.That(harness.Runner.FinalizedCheckpoint, Is.EqualTo(new CheckpointRef(ForkCrossingChain.ForkEpoch, first.Root)), "fixture bug");
        Assert.That(harness.Runner.ProposerBoostRoot, Is.EqualTo(child.Root), "fixture bug");

        harness.Runner.Prune();

        Assert.That(harness.Runner.GetWeight(new ForkChoiceNode(child.Root, ForkChoicePayloadStatus.Pending)), Is.EqualTo(GloasForkChoiceHarness.ProposerScore));
    }

    [Test]
    public void A_pruned_fork_block_still_withholds_the_boost_until_finality_passes_its_slot()
    {
        const ulong FinalizedSlot = 10 * Presets.SlotsPerEpoch;
        GloasForkChoiceHarness harness = new();
        GloasForkChoiceHarness.Block first = harness.First;
        harness.TickTo(FinalizedSlot + 1, secondsIntoSlot: 8);
        harness.Import(first);

        GloasForkChoiceHarness.Block finalized = first;
        GloasForkChoiceHarness.Block fork = first;
        for (ulong slot = first.Slot + 1; slot <= FinalizedSlot; slot++)
        {
            if (slot == FinalizedSlot)
            {
                fork = harness.Child(finalized, FinalizedSlot + 1, full: false, 0xA2);
                harness.Import(fork);
            }

            finalized = harness.Child(finalized, slot, full: false, (byte)(0xC0 + slot % 0x20));
            harness.Import(finalized);
        }

        GloasForkChoiceHarness.Block parent = harness.Child(finalized, FinalizedSlot + 1, full: false, 0xA1);
        BeaconStateGloas finalizing = parent.PostState.Clone();
        Checkpoint checkpoint = new() { Epoch = 10, Root = finalized.Root };
        finalizing.CurrentJustifiedCheckpoint = checkpoint;
        finalizing.FinalizedCheckpoint = checkpoint;
        harness.Runner.OnBlock(parent.Signed, finalizing);
        Assert.That(harness.Runner.FinalizedCheckpoint, Is.EqualTo(new CheckpointRef(10, finalized.Root)), "fixture bug");
        Assert.That(fork.ProposerIndex, Is.EqualTo(parent.ProposerIndex), "fixture bug: both blocks are the same proposer's");

        harness.TickTo(FinalizedSlot + 2);
        GloasForkChoiceHarness.Block boosted = harness.Child(parent, FinalizedSlot + 2, full: false, 0xB1);
        harness.Import(boosted);
        Assert.That(harness.Runner.ProposerBoostRoot, Is.EqualTo(boosted.Root), "fixture bug");
        ForkChoiceNode boostedNode = new(boosted.Root, ForkChoicePayloadStatus.Pending);
        Assert.That(harness.Runner.GetWeight(boostedNode), Is.Zero, "fixture bug: the equivocation withholds the boost");

        harness.Runner.Prune();

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(harness.Runner.ContainsBlock(fork.Root), Is.False, "fixture bug: the proto-array must have pruned the fork block");
        Assert.That(harness.Runner.GetWeight(boostedNode), Is.Zero);
    }

    [Test]
    public void Equal_weight_siblings_are_decided_by_the_greater_root()
    {
        GloasForkChoiceHarness harness = new();
        GloasForkChoiceHarness.Block first = harness.First;
        harness.TickTo(first.Slot + 3);
        harness.Import(first);
        GloasForkChoiceHarness.Block a = harness.Child(first, first.Slot + 1, full: false, 0xC1);
        GloasForkChoiceHarness.Block b = harness.Child(first, first.Slot + 2, full: false, 0xC2);
        harness.Import(a);
        harness.Import(b);
        Assert.That(harness.Runner.ProposerBoostRoot, Is.EqualTo(Hash256.Zero), "fixture bug");

        Hash256 greater = a.Root.CompareTo(b.Root) > 0 ? a.Root : b.Root;
        Assert.That(harness.Runner.GetHeadNode(), Is.EqualTo(new ForkChoiceNode(greater, ForkChoicePayloadStatus.Empty)));
    }

    [Test]
    public void A_subtree_with_a_stale_voting_source_is_not_the_head([Values] bool staleHasChild)
    {
        GloasForkChoiceHarness harness = new();
        ForkCrossingChain chain = harness.Chain;
        GloasForkChoiceHarness.Block first = harness.First;
        harness.TickTo(2 * Presets.SlotsPerEpoch + 2);
        harness.Import(first);
        GloasForkChoiceHarness.Block stale = harness.Child(first, first.Slot + 1, full: false, 0xE1);
        harness.Import(stale);
        GloasForkChoiceHarness.Block voted = stale;
        if (staleHasChild)
        {
            voted = harness.Child(stale, first.Slot + 2, full: false, 0xE2);
            harness.Import(voted);
        }
        foreach (ForkCrossingChain.ChainBlock block in chain.Voting)
            harness.Runner.OnBlock(block.Block, block.PostState);
        harness.CommitteeVotes(voted, 2 * Presets.SlotsPerEpoch + 1, index: 0);

        harness.TickTo(3 * Presets.SlotsPerEpoch);

        Assert.That(harness.Runner.JustifiedCheckpoint, Is.EqualTo(new CheckpointRef(ForkCrossingChain.ForkEpoch, first.Root)), "fixture bug");
        Assert.That(harness.Runner.GetHeadNode(), Is.EqualTo(new ForkChoiceNode(chain.Voting[^1].Root, ForkChoicePayloadStatus.Empty)));
    }

    [Test]
    public void The_last_pre_gloas_slot_keeps_the_fulu_proposer_boost()
    {
        UnsignedChain chain = UnsignedChain.Create();
        BeaconChainSpec spec = GloasTestFixtures.SyntheticSpec(gloasForkEpoch: 1);
        ulong boostedSlot = spec.SlotsPerEpoch - 1;
        ForkChoiceRunner runner = new(spec, chain.Anchor.AnchorState, chain.Anchor.AnchorBlock.Message!, chain, chain.Anchor.Pubkeys);
        UnsignedChain.ChainBlock parent = chain.Extend(chain.AnchorRoot, boostedSlot - 1, 0xA1);
        UnsignedChain.ChainBlock equivocation = Enumerable.Range(0xB1, 16)
            .Select(fill => chain.Extend(chain.AnchorRoot, boostedSlot - 1, (byte)fill))
            .First(block => block.Root.CompareTo(parent.Root) > 0);
        UnsignedChain.ChainBlock boosted = chain.Extend(parent.Root, boostedSlot, 0xC1);

        runner.OnTick(runner.GenesisTime + (boostedSlot - 1) * Presets.SecondsPerSlot);
        ImportFulu(runner, parent);
        runner.OnTick(runner.GenesisTime + (boostedSlot - 1) * Presets.SecondsPerSlot + 8);
        ImportFulu(runner, equivocation);
        runner.OnTick(runner.GenesisTime + boostedSlot * Presets.SecondsPerSlot);
        ImportFulu(runner, boosted);
        Assert.That(runner.ProposerBoostRoot, Is.EqualTo(boosted.Root), "fixture bug");

        Assert.That(runner.GetHeadNode(), Is.EqualTo(new ForkChoiceNode(boosted.Root, ForkChoicePayloadStatus.Empty)));
    }

    [Test]
    public void A_pre_gloas_head_is_its_empty_node_on_both_sides_of_the_fork([Values] bool afterFork)
    {
        GloasForkChoiceHarness harness = new();
        harness.TickTo(afterFork ? harness.First.Slot : harness.First.Slot - 1);

        Assert.That(harness.Runner.GetHeadNode(), Is.EqualTo(new ForkChoiceNode(harness.Chain.AnchorRoot, ForkChoicePayloadStatus.Empty)));
    }

    [Test]
    public void A_block_whose_only_child_is_invalid_is_a_viable_leaf()
    {
        GloasForkChoiceHarness harness = new();
        GloasForkChoiceHarness.Block first = harness.First;
        harness.TickTo(first.Slot + 2);
        harness.Import(first);
        GloasForkChoiceHarness.Block child = harness.Child(first, first.Slot + 1, full: false, 0xC1);
        harness.Import(child);

        harness.Runner.OnInvalidExecutionPayload(child.Root);

        Assert.That(harness.Runner.GetHeadNode(), Is.EqualTo(new ForkChoiceNode(first.Root, ForkChoicePayloadStatus.Empty)));
    }

    private static void ImportFulu(ForkChoiceRunner runner, UnsignedChain.ChainBlock block) =>
        runner.OnBlock(block.Block, block.PostState, ExecutionStatus.Valid, (IReadOnlyList<DataColumnSidecar>?)null);
}
