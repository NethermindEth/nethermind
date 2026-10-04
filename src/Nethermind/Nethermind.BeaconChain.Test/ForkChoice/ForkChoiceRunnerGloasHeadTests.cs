// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Test.StateTransition;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.ForkChoice;

/// <summary>
/// The Gloas <c>get_head</c> of <see cref="ForkChoiceRunner.GetHeadNode"/> (specs/gloas/fork-choice.md): the walk over PENDING,
/// EMPTY and FULL nodes, <c>get_weight</c>, <c>get_payload_status_tiebreaker</c>, <c>should_extend_payload</c> and
/// <c>should_apply_proposer_boost</c>.
/// </summary>
[HardTimeout(60_000)]
public class ForkChoiceRunnerGloasHeadTests
{
    public enum PayloadDecision
    {
        /// <summary>The block is from the current slot, so the tiebreaker is the status itself and FULL outranks EMPTY.</summary>
        CurrentSlot,

        /// <summary>The PTC voted the payload timely and its data available.</summary>
        PtcQuorum,

        /// <summary>No block holds the proposer boost.</summary>
        NoBoost,

        /// <summary>The boosted block builds on the block's FULL node.</summary>
        BoostBuildsOnFull,

        /// <summary>The boosted block builds on the block's EMPTY node, and the PTC did not vote the payload timely: an EMPTY outcome.</summary>
        BoostBuildsOnEmpty,

        /// <summary>As <see cref="BoostBuildsOnEmpty"/>, but the PTC voted the payload timely and its data unavailable: also EMPTY.</summary>
        PtcTimelyDataUnavailable,
    }

    /// <summary>
    /// specs/gloas/fork-choice.md <c>get_payload_status_tiebreaker</c> and <c>should_extend_payload</c>: the EMPTY and FULL nodes of
    /// a previous-slot block weigh nothing, so FULL is taken when the PTC saw a timely, available payload, when no boosted block
    /// builds on this block's EMPTY node, and EMPTY otherwise. The head then continues into the children of the chosen node.
    /// </summary>
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

    /// <summary>
    /// specs/gloas/fork-choice.md <c>should_extend_payload</c>: a boosted block that is not a child of the previous-slot block says
    /// nothing about its payload, so the payload is extended even though that boosted block builds on its own parent's EMPTY node.
    /// </summary>
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
        // The committee's votes keep the head on the decided block against its boosted sibling.
        harness.CommitteeVotes(decided, decided.Slot, index: 0);

        harness.TickTo(decided.Slot + 1);
        GloasForkChoiceHarness.Block sibling = harness.Child(first, decided.Slot + 1, full: false, 0xD1);
        harness.Import(sibling);
        Assert.That(harness.Runner.ProposerBoostRoot, Is.EqualTo(sibling.Root), "fixture bug");

        Assert.That(harness.Runner.GetHeadNode(), Is.EqualTo(new ForkChoiceNode(decided.Root, ForkChoicePayloadStatus.Full)));
    }

    /// <summary>
    /// specs/gloas/fork-choice.md <c>get_weight</c>: an EMPTY or FULL node of the previous slot's block weighs zero, so the votes
    /// for its EMPTY node cannot outweigh the tiebreaker, which extends the payload here. A slot later the same votes decide by weight.
    /// The PENDING node keeps them either way.
    /// </summary>
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

        /// <summary>The parent's proposer published a second block for the parent's slot 8 s in, before the 9 s PTC deadline.</summary>
        EquivocationBeforePtcDeadline,

        /// <summary>The second block arrived at 9 s, at the PTC deadline and so not PTC-timely.</summary>
        EquivocationAtPtcDeadline,

        /// <summary>The equivocating parent holds a committee's votes, so it is not weak.</summary>
        EquivocationWithStrongParent,

        /// <summary>The boosted block is two slots after its equivocating parent.</summary>
        EquivocationWithParentTwoSlotsBack,
    }

    /// <summary>
    /// specs/gloas/fork-choice.md <c>should_apply_proposer_boost</c> and <c>record_block_timeliness</c>: the boost counts unless the
    /// boosted block's parent is from the previous slot, is weak, and its proposer published another block for that slot before the
    /// PTC deadline. The boosted block has no votes, so its PENDING weight is the proposer score exactly when the boost applies.
    /// </summary>
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
        // is_ancestor: the boost reaches the payload nodes the boosted block descends through, never its own or those off the path.
        Assert.That(harness.Runner.GetWeight(new ForkChoiceNode(boosted.Root, ForkChoicePayloadStatus.Empty)), Is.Zero);
        Assert.That(harness.Runner.GetWeight(new ForkChoiceNode(boosted.Root, ForkChoicePayloadStatus.Full)), Is.Zero);
        Assert.That(harness.Runner.GetWeight(new ForkChoiceNode(first.Root, ForkChoicePayloadStatus.Empty)), Is.EqualTo(parentVotes + boost));
        Assert.That(harness.Runner.GetWeight(new ForkChoiceNode(first.Root, ForkChoicePayloadStatus.Full)), Is.Zero);
    }

    /// <summary>
    /// specs/gloas/fork-choice.md <c>should_apply_proposer_boost</c> reads the proposer of the boosted block's parent, and
    /// <c>get_forkchoice_store</c> records the anchor's. A boosted child of a weak Gloas anchor keeps its boost.
    /// </summary>
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

    /// <summary>
    /// specs/gloas/fork-choice.md <c>should_apply_proposer_boost</c> reads the proposer of the boosted block's parent, which can be
    /// the finalized block itself. Pruning drops proposer records by slot, but a block still in the tree keeps its own, so a weak
    /// finalized parent neither stalls the head nor loses the boost of its child.
    /// </summary>
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

    /// <summary>
    /// The spec's <c>store.block_timeliness</c> is never pruned, so a PTC-timely proposal on a fork that finality left behind still
    /// makes the parent's proposer an early equivocator, and the boost stays withheld, until finality passes that fork block's slot.
    /// The fork block (slot 321) is imported before the finalized block (slot 320), so the proto-array's prune removes it.
    /// </summary>
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

    /// <summary>
    /// specs/gloas/fork-choice.md <c>get_head</c>: among children of equal weight the greater root wins. Two late siblings with no
    /// votes and no boost weigh the same.
    /// </summary>
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

    /// <summary>
    /// specs/phase0/fork-choice.md <c>filter_block_tree</c>: once the first Gloas block is justified, a leaf whose voting source
    /// is still the genesis epoch is not viable, so the head skips it for the vote-less justifying chain however heavy it is.
    /// A stale block whose only child is such a leaf is dropped too, since no descendant of it is viable.
    /// </summary>
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

    /// <summary>
    /// The Gloas <c>get_head</c> starts at <c>GLOAS_FORK_EPOCH</c>. In the last slot before it the Fulu <c>get_head</c> counts the
    /// proposer boost (specs/phase0/fork-choice.md <c>get_weight</c>) where Gloas <c>should_apply_proposer_boost</c> would withhold it:
    /// the boosted block's parent is weak and its proposer equivocated before the PTC deadline.
    /// </summary>
    [Test]
    public void The_last_pre_gloas_slot_keeps_the_fulu_proposer_boost()
    {
        UnsignedChain chain = UnsignedChain.Create();
        BeaconChainSpec spec = GloasTestFixtures.SyntheticSpec(gloasForkEpoch: 1);
        ulong boostedSlot = spec.SlotsPerEpoch - 1;
        ForkChoiceRunner runner = new(spec, chain.Anchor.AnchorState, chain.Anchor.AnchorBlock.Message!, chain, chain.Anchor.Pubkeys);
        UnsignedChain.ChainBlock parent = chain.Extend(chain.AnchorRoot, boostedSlot - 1, 0xA1);
        // Validator 0 proposes every slot, so this is the parent's proposer equivocating; its root wins an equal-weight tie.
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

    /// <summary>
    /// specs/gloas/fork-choice.md <c>get_node_children</c>: a pre-Gloas block is never in <c>store.payloads</c>, so as the head it is
    /// its EMPTY node, before the fork (the Fulu <c>get_head</c>) and after it. A FULL head would lead attesters to an index-1 vote
    /// that <c>validate_on_attestation</c> refuses.
    /// </summary>
    [Test]
    public void A_pre_gloas_head_is_its_empty_node_on_both_sides_of_the_fork([Values] bool afterFork)
    {
        GloasForkChoiceHarness harness = new();
        harness.TickTo(afterFork ? harness.First.Slot : harness.First.Slot - 1);

        Assert.That(harness.Runner.GetHeadNode(), Is.EqualTo(new ForkChoiceNode(harness.Chain.AnchorRoot, ForkChoicePayloadStatus.Empty)));
    }

    /// <summary>
    /// specs/phase0/fork-choice.md <c>filter_block_tree</c> with specs/bellatrix/optimistic-sync.md: an invalidated block leaves the
    /// tree, so a parent whose only child was invalidated is a leaf again and heads the chain, rather than dropping out as a parent
    /// of no viable branch.
    /// </summary>
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
