// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Hashing;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Test.StateTransition;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using NUnit.Framework;
using FuluStateTransition = Nethermind.BeaconChain.StateTransition.StateTransition;

namespace Nethermind.BeaconChain.Test.ForkChoice;

/// <summary>
/// The pure predicates behind <see cref="ForkChoiceRunner.GetProposerHead"/> (silent-wrong
/// risks: get_proposer_head returning the ordinary head instead of ever re-org'ing), and the way
/// <see cref="ForkChoiceRunner.OnBlock"/> hands <c>is_data_available</c> to whichever
/// <see cref="IDataAvailabilityRule"/> its caller chose: a column list means the supernode rule the
/// spec vectors need, a rule means exactly that rule, and neither is ever inferred from the other.
/// Also the body replay <see cref="ForkChoiceRunner.OnBlock(SignedBeaconBlock, BeaconStateFulu, ExecutionStatus, IDataAvailabilityRule)"/>
/// leaves to its caller, on a hand-built chain (<see cref="UnsignedChain"/>): no mainnet vector
/// carries a body attester slashing, so the vectors never show whether a replayed one is honored.
/// </summary>
[HardTimeout(60_000)]
public class ForkChoiceRunnerTests
{
    private const ulong EffectiveBalance = 32 * GloasTestFixtures.Gwei;

    /// <summary>With 2048 validators and 32 slots, each slot has one committee of 64.</summary>
    private const ulong CommitteeSize = 64;

    /// <summary>The current justified epoch of the doctored post-state in <see cref="FinalizedOnFirstGloasBlock"/>.</summary>
    private const ulong DoctoredJustifiedEpoch = 2;

    [TestCase(0ul, 0ul, true, TestName = "same_epoch_is_ok")]
    [TestCase(64ul, 0ul, true, TestName = "exactly_the_max_gap_is_ok")]
    [TestCase(96ul, 0ul, false, TestName = "one_epoch_past_the_max_gap_is_not_ok")]
    public void IsFinalizationOk_allows_up_to_the_configured_epoch_gap(ulong slot, ulong finalizedEpoch, bool expected) =>
        Assert.That(ForkChoiceRunner.IsFinalizationOk(slot, finalizedEpoch, reorgMaxEpochsSinceFinalization: 2), Is.EqualTo(expected));

    // finalizedEpoch (5) can never legitimately exceed compute_epoch_at_slot(slot) (0) in a consistent
    // store; the ulong subtraction underflows to a huge gap, which must deny the reorg rather than
    // wrap around into a false "ok".
    [Test]
    public void IsFinalizationOk_fails_closed_if_finalized_epoch_somehow_outran_the_slot() =>
        Assert.That(ForkChoiceRunner.IsFinalizationOk(slot: 0, finalizedEpoch: 5, reorgMaxEpochsSinceFinalization: 2), Is.False);

    [TestCase(1ul, 2ul, 3ul, true, TestName = "consecutive_parent_head_and_proposal_slots")]
    [TestCase(0ul, 2ul, 3ul, false, TestName = "parent_is_not_immediately_before_head")]
    [TestCase(1ul, 2ul, 4ul, false, TestName = "head_is_not_immediately_before_the_proposal_slot")]
    public void IsSingleSlotReorg_requires_three_consecutive_slots(ulong parentSlot, ulong headSlot, ulong proposalSlot, bool expected) =>
        Assert.That(ForkChoiceRunner.IsSingleSlotReorg(parentSlot, headSlot, proposalSlot), Is.EqualTo(expected));

    [Test]
    public void OnBlock_with_a_column_list_applies_the_supernode_rule_regardless_of_this_nodes_custody()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        (ForkChoiceRunner runner, BeaconStateFulu postState) = RunnerAt(chain);
        // The eight columns a base-custody node would hold: enough for the production rule, never for this one.
        DataColumnSidecar[] eightColumns = [.. chain.Columns.Take(8)];

        Assert.Multiple(() =>
        {
            Assert.That(() => runner.OnBlock(chain.Block, postState, ExecutionStatus.Valid, eightColumns),
                Throws.TypeOf<ForkChoiceException>().With.Message.Contains("blob data"));
            Assert.That(runner.ContainsBlock(chain.BlockRoot), Is.False, "a rejected block must not have been added to the tree");
            Assert.That(() => runner.OnBlock(chain.Block, postState, ExecutionStatus.Valid, chain.Columns), Throws.Nothing,
                "the spec vectors hand over the whole matrix and expect acceptance");
            Assert.That(runner.ContainsBlock(chain.BlockRoot), Is.True);
        });
    }

    [Test]
    public void OnBlock_with_a_rule_asks_that_rule_about_this_block_and_honors_its_verdict()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        (ForkChoiceRunner runner, BeaconStateFulu postState) = RunnerAt(chain);
        RecordingRule refusing = new(verdict: false);
        RecordingRule accepting = new(verdict: true);

        Assert.Multiple(() =>
        {
            Assert.That(() => runner.OnBlock(chain.Block, postState, ExecutionStatus.Valid, refusing), Throws.TypeOf<ForkChoiceException>());
            Assert.That(refusing.Asked, Is.EqualTo(new List<(BeaconBlock, Hash256)> { (chain.Block.Message!, chain.BlockRoot) }),
                "the rule is consulted for this block, keyed by its real root");
            Assert.That(runner.ContainsBlock(chain.BlockRoot), Is.False);

            Assert.That(() => runner.OnBlock(chain.Block, postState, ExecutionStatus.Valid, accepting), Throws.Nothing);
            Assert.That(accepting.Asked, Has.Count.EqualTo(1));
            Assert.That(runner.ContainsBlock(chain.BlockRoot), Is.True);
        });
    }

    /// <summary>
    /// The snapshot is what the debug API serves from another thread, so it must be a complete copy
    /// (checkpoints, boost root, every node with its parent resolved to a root) that later imports
    /// leave untouched: a live view would race the import worker.
    /// </summary>
    [Test]
    public void Snapshot_copies_the_store_and_later_blocks_do_not_change_an_earlier_copy()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        (ForkChoiceRunner runner, BeaconStateFulu postState) = RunnerAt(chain);
        ForkChoiceSnapshot anchorOnly = runner.Snapshot();

        runner.OnBlock(chain.Block, postState, ExecutionStatus.Optimistic, chain.Columns);
        ForkChoiceSnapshot withBlock = runner.Snapshot();

        Assert.Multiple(() =>
        {
            Assert.That(anchorOnly.Nodes, Has.Count.EqualTo(1), "the copy taken before the block still holds the anchor alone");
            Assert.That(anchorOnly.Nodes[0].ParentRoot, Is.Null, "the tree root's parent is outside the tree");
            Assert.That(anchorOnly.Nodes[0].Root, Is.EqualTo(chain.AnchorRoot));

            Assert.That(withBlock.Nodes.Select(n => n.Root), Is.EqualTo(new[] { chain.AnchorRoot, chain.BlockRoot }), "proto-array order, parent first");
            ForkChoiceSnapshotNode block = withBlock.Nodes[1];
            Assert.That(block.ParentRoot, Is.EqualTo(chain.AnchorRoot), "the parent index is resolved to its root");
            Assert.That(block.Slot, Is.EqualTo(chain.Block.Message!.Slot));
            Assert.That(block.ExecutionStatus, Is.EqualTo(ExecutionStatus.Optimistic));
            Assert.That(block.ExecutionBlockHash, Is.EqualTo(chain.Block.Message.Body!.ExecutionPayload!.BlockHash));
            Assert.That(block.JustifiedEpoch, Is.EqualTo(runner.JustifiedCheckpoint.Epoch));
            Assert.That(block.FinalizedEpoch, Is.EqualTo(runner.FinalizedCheckpoint.Epoch));

            Assert.That(withBlock.JustifiedCheckpoint, Is.EqualTo(runner.JustifiedCheckpoint));
            Assert.That(withBlock.FinalizedCheckpoint, Is.EqualTo(runner.FinalizedCheckpoint));
            Assert.That(withBlock.ProposerBoostRoot, Is.EqualTo(chain.BlockRoot), "a block imported at the start of its own slot holds the boost, and the copy says so");
        });
    }

    /// <summary>
    /// Two validators vote block A onto the head over block B's one; a later block on A's branch
    /// carries a slashing of those two for a double vote. Replayed the way the callers do (after
    /// OnBlock, signatures unverified), the slashing must pull their votes out of the weight so B
    /// wins - even though the slashing block itself extends A's branch. Without the replay, A's
    /// branch keeps its two votes and nothing ever reports the loss.
    /// </summary>
    [Test]
    public void Body_attester_slashing_replayed_after_OnBlock_discounts_the_equivocating_votes()
    {
        (ForkChoiceRunner runner, UnsignedChain.ChainBlock voted, UnsignedChain.ChainBlock b, UnsignedChain.ChainBlock slashing) = EquivocationScenario();
        Hash256 headBeforeSlashing = runner.GetHead();

        ImportWithBodyReplay(runner, slashing);

        Assert.Multiple(() =>
        {
            Assert.That(headBeforeSlashing, Is.EqualTo(voted.Root), "two votes on A's branch outweigh one on B");
            Assert.That(runner.GetHead(), Is.EqualTo(b.Root), "the slashed validators' votes no longer count, so B's lone vote wins");
        });
    }

    /// <summary>
    /// The replay flag is what admits a transition-verified slashing: the same body slashing with
    /// verification on is refused, and a refused slashing leaves no equivocating index behind.
    /// </summary>
    [Test]
    public void Body_attester_slashing_replay_with_verification_on_is_refused_whole()
    {
        (ForkChoiceRunner runner, _, _, UnsignedChain.ChainBlock slashing) = EquivocationScenario();
        runner.OnBlock(slashing.Block, slashing.PostState, ExecutionStatus.Valid, (IReadOnlyList<DataColumnSidecar>?)null);

        Assert.Multiple(() =>
        {
            Assert.That(() => runner.OnAttesterSlashing(slashing.Block.Message!.Body!.AttesterSlashings![0], verifySignatures: true),
                Throws.TypeOf<ForkChoiceException>().With.Message.Contains("invalid"));
            Assert.That(runner.GetHead(), Is.EqualTo(slashing.Root), "a refused slashing discounts nobody: the head stays on A's branch, at its new leaf");
        });
    }

    /// <summary>
    /// Once the epoch-2 votes are pulled up, the justified checkpoint is the first Gloas block, and
    /// weighing the head needs that root's state: only the Gloas provider holds it, and offering the root
    /// to the Fulu provider is the crash this guards against. The body votes are replayed in either
    /// container, because committees follow the target state's fork, not the container's; the weights
    /// prove every replayed vote counted, including the Gloas block's own last-Fulu-epoch vote.
    /// </summary>
    [Test]
    public void Justified_checkpoint_on_a_gloas_block_is_weighed_from_its_gloas_state([Values] bool replayInFuluContainer)
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = chain.CreateRunner();
        TickToSlot(runner, 2 * Presets.SlotsPerEpoch + 2);

        ImportGloas(runner, chain.First, replayInFuluContainer);
        Hash256 headAtFork = runner.GetHead();
        foreach (ForkCrossingChain.ChainBlock block in chain.Voting)
        {
            ImportGloas(runner, block, replayInFuluContainer);
        }

        CheckpointRef justifiedBeforePullUp = runner.JustifiedCheckpoint;
        TickToSlot(runner, 3 * Presets.SlotsPerEpoch);
        Hash256 head = runner.GetHead();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(headAtFork, Is.EqualTo(chain.First.Root));
            Assert.That(justifiedBeforePullUp, Is.EqualTo(new CheckpointRef(0, chain.AnchorRoot)), "fixture bug: the justification must arrive by the epoch-3 pull-up");
            Assert.That(runner.JustifiedCheckpoint, Is.EqualTo(new CheckpointRef(ForkCrossingChain.ForkEpoch, chain.First.Root)),
                "1536 of 2048 epoch-1 target votes justify the first Gloas block");
            Assert.That(head, Is.EqualTo(chain.Voting[^1].Root));
            Assert.That(Weight(runner, chain.First.Root), Is.EqualTo(ForkCrossingChain.VotingSlotCount * CommitteeSize * EffectiveBalance));
            Assert.That(chain.LastFuluVotesStanding, Is.GreaterThan(0), "fixture bug: some slot-31 vote must survive as a latest message");
            Assert.That(Weight(runner, chain.AnchorRoot) - Weight(runner, chain.First.Root), Is.EqualTo((ulong)chain.LastFuluVotesStanding * EffectiveBalance),
                "the slot-31 votes the Gloas block carried are weighed on the anchor");
        }
    }

    /// <summary>
    /// <c>store_target_checkpoint_state</c> past the fork must yield the state the state transition would
    /// carry there - Fulu slots to the boundary, the upgrade, then Gloas slots - composed here by hand
    /// from the fork's own pieces, never through the runner. A Fulu block's state run through Fulu slot
    /// processing straight past the boundary would be the wrong type and the wrong root.
    /// </summary>
    [TestCase(false, 1ul, TestName = "fulu_root_at_the_fork_epoch_is_upgraded")]
    [TestCase(false, 2ul, TestName = "fulu_root_past_the_fork_epoch_is_upgraded_then_advanced_as_gloas")]
    [TestCase(true, 2ul, TestName = "gloas_root_is_advanced_as_gloas")]
    public void Checkpoint_state_past_the_fork_is_carried_across_it_as_the_state_transition_does(bool gloasRoot, ulong epoch)
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = chain.CreateRunner();
        Hash256 root = chain.AnchorRoot;
        if (gloasRoot)
        {
            TickToSlot(runner, GloasTestFixtures.BoundarySlot);
            runner.OnBlock(chain.First.Block, chain.First.PostState);
            root = chain.First.Root;
        }

        Hash256 blockStateRoot = BlockStateRoot(chain, gloasRoot);
        ulong startSlot = epoch * Presets.SlotsPerEpoch;
        BeaconStateGloas expected = gloasRoot ? chain.First.PostState.Clone() : UpgradedAnchor(chain);
        if (expected.Slot < startSlot)
            GloasSlotProcessing.ProcessSlots(expected, startSlot, new EpochCache());

        ForkedBeaconState checkpointState = runner.GetCheckpointState(new CheckpointRef(epoch, root));

        ForkedBeaconState blockState = gloasRoot
            ? new ForkedBeaconState.OfGloas(chain.First.PostState.Clone())
            : new ForkedBeaconState.OfFulu(chain.AnchorState.Clone());
        BeaconStateGloas transitioned = ((ForkedBeaconState.OfGloas)ForkedStateTransition.CrossBoundaryIfNeeded(blockState, BeaconFork.Gloas, chain.Spec, new EpochCache())).State;
        if (transitioned.Slot < startSlot)
            GloasSlotProcessing.ProcessSlots(transitioned, startSlot, new EpochCache());

        Assert.That(checkpointState, Is.TypeOf<ForkedBeaconState.OfGloas>());
        BeaconStateGloas actual = ((ForkedBeaconState.OfGloas)checkpointState).State;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.Slot, Is.EqualTo(startSlot));
            Assert.That(SszRoots.HashTreeRoot(actual), Is.EqualTo(SszRoots.HashTreeRoot(expected)));
            Assert.That(SszRoots.HashTreeRoot(actual), Is.EqualTo(SszRoots.HashTreeRoot(transitioned)), "the state transition's own crossing lands on the same state");
            Assert.That(BlockStateRoot(chain, gloasRoot), Is.EqualTo(blockStateRoot), "the block state the providers froze must not be advanced in place");
        }
    }

    /// <summary>
    /// A vote names a checkpoint whose block lies <paramref name="distance"/> slots before the epoch start. At mainnet registry
    /// size a full state merkleization takes seconds, so one per skipped slot held the import worker for minutes. The first
    /// slot's root is the block's own <c>state_root</c>, so a late first block of an epoch costs no merkleization at all, and one
    /// incremental hasher serves every later slot; the advance must still land on the spec's state.
    /// </summary>
    [Test]
    public void Checkpoint_state_takes_the_first_slot_root_from_the_block_and_hashes_later_slots_through_one_incremental_hasher([Values(1, 2, 32)] int distance)
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        ulong blockSlot = Presets.SlotsPerEpoch - (ulong)distance;
        BeaconStateFulu blockState = chain.AnchorState.Clone();
        if (blockSlot > 0)
            SlotProcessing.ProcessSlots(blockState, blockSlot, new EpochCache());
        BeaconBlock anchor = chain.AnchorBlock.Message!;
        BeaconBlock block = new() { Slot = blockSlot, ProposerIndex = anchor.ProposerIndex, ParentRoot = anchor.ParentRoot, StateRoot = SszRoots.HashTreeRoot(blockState), Body = anchor.Body };
        Hash256 blockRoot = SszRoots.HashTreeRoot(block);
        InMemoryStates states = new();
        states.States[blockRoot] = blockState;
        ForkChoiceRunner runner = new(chain.Spec, blockState, block, states, chain.Pubkeys);
        IBeaconStateHasher defaultHasher = runner.CheckpointStateHasher();
        List<CountingHasher> made = [];
        runner.CheckpointStateHasher = () =>
        {
            CountingHasher hasher = new(new CachedBeaconStateHasher());
            made.Add(hasher);
            return hasher;
        };
        Hash256 blockStateRoot = SszRoots.HashTreeRoot(blockState);
        CheckpointRef checkpoint = new(1, blockRoot);
        BeaconStateFulu expected = blockState.Clone();
        SlotProcessing.ProcessSlots(expected, Presets.SlotsPerEpoch, new EpochCache());

        ForkedBeaconState first = runner.GetCheckpointState(checkpoint);
        ForkedBeaconState repeated = runner.GetCheckpointState(checkpoint);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(defaultHasher, Is.TypeOf<CachedBeaconStateHasher>(), "production advances must hash incrementally");
            Assert.That(made, Has.Count.EqualTo(1), "one hasher per advance, and none for the cached repeat");
            Assert.That(made.Single().Calls, Is.EqualTo(distance - 1), "every skipped slot's state root but the block's own comes from that hasher");
            Assert.That(repeated, Is.SameAs(first));
            Assert.That(SszRoots.HashTreeRoot(((ForkedBeaconState.OfFulu)first).State), Is.EqualTo(SszRoots.HashTreeRoot(expected)));
            Assert.That(SszRoots.HashTreeRoot(blockState), Is.EqualTo(blockStateRoot), "the block state must not be advanced in place");
        }
    }

    /// <summary>
    /// A gossip aggregate names its target, and the target state is needed before any signature can be checked. Each unsigned
    /// aggregate naming another known block before the epoch start used to build and keep a whole checkpoint state on the import
    /// worker. Targets that share a shuffling decision block share committees and domain, so only the first one may cost a build,
    /// no refused aggregate may leave a checkpoint state cached, and a signed aggregate for yet another such target still counts.
    /// </summary>
    [Test]
    public void Unsigned_aggregates_on_distinct_targets_of_one_shuffling_build_one_state_and_cache_none()
    {
        const ulong targetEpoch = 2;
        UnsignedChain chain = UnsignedChain.Create();
        ForkChoiceRunner runner = new(chain.Spec, chain.Anchor.AnchorState, chain.Anchor.AnchorBlock.Message!, chain, chain.Anchor.Pubkeys);
        BuildCounter builds = new(runner);
        ulong epochStart = targetEpoch * Presets.SlotsPerEpoch;
        TickToSlot(runner, epochStart + 6);
        // All past the decision slot 31, so the anchor fixes the epoch-2 shuffling of every one of them.
        List<UnsignedChain.ChainBlock> targets = ImportLine(chain, runner, chain.AnchorRoot, 33, 41, 50, 60, 63);
        (BeaconStateFulu signingState, ulong voteSlot, int member) = FirstCommitteeMember(targets[^1], targetEpoch);

        int refused = 0;
        foreach (UnsignedChain.ChainBlock target in targets.SkipLast(1))
        {
            try
            {
                runner.OnAggregateAndProof(GossipAggregate(chain, voteSlot, target.Root, targetEpoch, member, signingState: null));
            }
            catch (Exception e) when (e is ForkChoiceException or BeaconStateException)
            {
                refused++;
            }
        }

        int buildsAfterFlood = builds.Count;
        ulong weightBefore = Weight(runner, targets[^1].Root);
        runner.OnAggregateAndProof(GossipAggregate(chain, voteSlot, targets[^1].Root, targetEpoch, member, signingState));
        ulong weightAfter = Weight(runner, targets[^1].Root);
        int buildsAfterSignedVote = builds.Count;
        runner.GetCheckpointState(new CheckpointRef(targetEpoch, targets[0].Root));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(voteSlot, Is.LessThan(epochStart + 6), "fixture bug: the vote must be from a past slot so it applies at once");
            Assert.That(refused, Is.EqualTo(targets.Count - 1), "every unsigned aggregate is refused");
            Assert.That(buildsAfterFlood, Is.EqualTo(1), "only the first target of the shuffling costs a checkpoint state");
            Assert.That(buildsAfterSignedVote, Is.EqualTo(1), "a signed vote on another target of that shuffling needs no state of its own");
            Assert.That(weightAfter - weightBefore, Is.EqualTo(EffectiveBalance), "the signed aggregate's vote counts");
            Assert.That(builds.Count, Is.EqualTo(2), "the refused aggregate's state was not cached as its target's checkpoint state");
        }
    }

    /// <summary>
    /// p2p-interface.md authenticates an aggregate with the head state, so an unsigned one naming a target of another shuffling
    /// (A at the decision slot 31 is its own decision block, B at slot 32 has their parent) is refused with only the head's own
    /// target state built, never the one the peer named, and its vote does not count.
    /// </summary>
    [Test]
    public void Unsigned_gossip_aggregate_for_another_shuffling_builds_only_the_head_target_state()
    {
        const ulong targetEpoch = 2;
        UnsignedChain chain = UnsignedChain.Create();
        ForkChoiceRunner runner = new(chain.Spec, chain.Anchor.AnchorState, chain.Anchor.AnchorBlock.Message!, chain, chain.Anchor.Pubkeys);
        BuildCounter builds = new(runner);
        TickToSlot(runner, targetEpoch * Presets.SlotsPerEpoch + 6);
        UnsignedChain.ChainBlock parent = ImportLine(chain, runner, chain.AnchorRoot, 30)[0];
        UnsignedChain.ChainBlock a = ImportLine(chain, runner, parent.Root, 31)[0];
        UnsignedChain.ChainBlock b = ImportLine(chain, runner, parent.Root, 32)[0];
        Hash256 head = runner.GetHead();
        (UnsignedChain.ChainBlock headTip, UnsignedChain.ChainBlock other) = head == a.Root ? (a, b) : (b, a);
        (_, ulong voteSlot, int member) = FirstCommitteeMember(headTip, targetEpoch);
        ulong weightBefore = Weight(runner, other.Root);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(head, Is.AnyOf(a.Root, b.Root), "fixture bug: the head must be one of the two branches");
            Assert.That(() => runner.OnAggregateAndProof(GossipAggregate(chain, voteSlot, other.Root, targetEpoch, member, signingState: null)),
                Throws.InstanceOf<Exception>());
            Assert.That(builds.Count, Is.EqualTo(1), "only the head's target state is built for a peer's aggregate");
            Assert.That(Weight(runner, other.Root), Is.EqualTo(weightBefore), "the vote does not count");
        }
    }

    /// <summary>
    /// Two blocks a proposer equivocated at the decision slot carry the same RANDAO reveal, so their branches have different
    /// decision blocks but the same committees. An aggregate the head's committee signed for the other branch is valid gossip
    /// and valid for on_attestation with its own target state, so it must count, at the cost of building that state.
    /// </summary>
    [Test]
    public void Signed_gossip_aggregate_for_an_equivocated_sibling_with_the_same_committees_counts()
    {
        const ulong targetEpoch = 2;
        UnsignedChain chain = UnsignedChain.Create();
        ForkChoiceRunner runner = new(chain.Spec, chain.Anchor.AnchorState, chain.Anchor.AnchorBlock.Message!, chain, chain.Anchor.Pubkeys);
        BuildCounter builds = new(runner);
        TickToSlot(runner, targetEpoch * Presets.SlotsPerEpoch + 6);
        UnsignedChain.ChainBlock parent = ImportLine(chain, runner, chain.AnchorRoot, 30)[0];
        UnsignedChain.ChainBlock first = chain.Extend(parent.Root, 31, payloadHashByte: 0xE1);
        UnsignedChain.ChainBlock second = chain.Extend(parent.Root, 31, payloadHashByte: 0xE2);
        ImportWithBodyReplay(runner, first);
        ImportWithBodyReplay(runner, second);
        Hash256 head = runner.GetHead();
        (UnsignedChain.ChainBlock headTip, UnsignedChain.ChainBlock other) = head == first.Root ? (first, second) : (second, first);
        (BeaconStateFulu signingState, ulong voteSlot, int member) = FirstCommitteeMember(headTip, targetEpoch);
        ulong weightBefore = Weight(runner, other.Root);

        runner.OnAggregateAndProof(GossipAggregate(chain, voteSlot, other.Root, targetEpoch, member, signingState));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(FirstCommitteeMember(other, targetEpoch).Member, Is.EqualTo(member), "fixture bug: the siblings must share committees");
            Assert.That(Weight(runner, other.Root) - weightBefore, Is.EqualTo(EffectiveBalance), "the vote counts on the sibling");
            Assert.That(builds.Count, Is.EqualTo(2), "the head's target state and the sibling's own");
        }
    }

    /// <summary>
    /// Every target of another shuffling than the head's costs a whole state even once its aggregate authenticated, so only
    /// two are built per epoch, and one each per aggregator: four siblings at or before the decision slot, each its own decision
    /// block and all with the same committees, give three off-head targets. Three aggregators get two builds and the third is
    /// refused; one aggregator gets one build and its second aggregate is refused.
    /// </summary>
    [Test]
    public void Gossip_targets_of_other_shufflings_cost_at_most_two_states_an_epoch_and_one_an_aggregator([Values] bool oneAggregator)
    {
        const ulong targetEpoch = 2;
        UnsignedChain chain = UnsignedChain.Create();
        ForkChoiceRunner runner = new(chain.Spec, chain.Anchor.AnchorState, chain.Anchor.AnchorBlock.Message!, chain, chain.Anchor.Pubkeys);
        BuildCounter builds = new(runner);
        TickToSlot(runner, targetEpoch * Presets.SlotsPerEpoch + 30);
        List<UnsignedChain.ChainBlock> siblings = [];
        foreach (ulong slot in (ulong[])[28, 29, 30, 31])
        {
            siblings.Add(ImportLine(chain, runner, chain.AnchorRoot, slot)[0]);
        }

        Hash256 head = runner.GetHead();
        UnsignedChain.ChainBlock headTip = siblings.Single(sibling => sibling.Root == head);
        List<UnsignedChain.ChainBlock> others = [.. siblings.Where(sibling => sibling.Root != head)];
        List<(BeaconStateFulu SigningState, ulong Slot, int Member)> aggregators = [];
        for (int skip = 0; aggregators.Count < 3; skip++)
        {
            (BeaconStateFulu, ulong, int) candidate = FirstCommitteeMember(headTip, targetEpoch, skip);
            if (oneAggregator || aggregators.TrueForAll(known => known.Member != candidate.Item3))
                aggregators.Add(oneAggregator && aggregators.Count > 0 ? aggregators[0] : candidate);
        }

        int refusedAt = -1;
        for (int i = 0; i < others.Count && refusedAt < 0; i++)
        {
            (BeaconStateFulu signingState, ulong voteSlot, int member) = aggregators[i];
            try
            {
                runner.OnAggregateAndProof(GossipAggregate(chain, voteSlot, others[i].Root, targetEpoch, member, signingState));
            }
            catch (ForkChoiceException e) when (e.Message.Contains("spent"))
            {
                refusedAt = i;
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refusedAt, Is.EqualTo(oneAggregator ? 1 : 2), "the aggregate past the budget is refused");
            Assert.That(builds.Count, Is.EqualTo(1 + refusedAt), "the head's target state and one per aggregate before it");
        }
    }

    /// <summary>
    /// An ancestor of the head before its decision block lacks the RANDAO reveals up to that block, so a vote for it is another
    /// shuffling's: such an aggregate, even one the head's committee signed, is ignored before any state is built, and cannot
    /// spend the epoch's builds on old canonical blocks.
    /// </summary>
    [Test]
    public void Gossip_aggregate_for_an_ancestor_of_the_head_with_another_shuffling_is_ignored_without_a_build()
    {
        const ulong targetEpoch = 2;
        UnsignedChain chain = UnsignedChain.Create();
        ForkChoiceRunner runner = new(chain.Spec, chain.Anchor.AnchorState, chain.Anchor.AnchorBlock.Message!, chain, chain.Anchor.Pubkeys);
        BuildCounter builds = new(runner);
        TickToSlot(runner, targetEpoch * Presets.SlotsPerEpoch + 6);
        List<UnsignedChain.ChainBlock> line = ImportLine(chain, runner, chain.AnchorRoot, 30, 31, 40);
        (BeaconStateFulu signingState, ulong voteSlot, int member) = FirstCommitteeMember(line[^1], targetEpoch);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(runner.GetHead(), Is.EqualTo(line[^1].Root), "fixture bug: the line's tip must be the head");
            Assert.That(() => runner.OnAggregateAndProof(GossipAggregate(chain, voteSlot, line[0].Root, targetEpoch, member, signingState)),
                Throws.TypeOf<ForkChoiceException>().With.Message.Contains("ancestor of the head"));
            Assert.That(builds.Count, Is.Zero);
        }
    }

    /// <summary>
    /// get_head picks the state gossip is checked with, and a slashing or an invalid payload can move it without a tick or a
    /// block: once A's two voters are slashed, or A's payload is invalid, B is the head. An aggregate for B right after must be
    /// checked against B as the head, building B's target state, not against the head cached before.
    /// </summary>
    [Test]
    public void Gossip_aggregate_right_after_the_head_moves_without_a_tick_is_checked_against_the_new_head([Values] bool invalidPayload)
    {
        const ulong targetEpoch = 2;
        UnsignedChain chain = UnsignedChain.Create();
        ForkChoiceRunner runner = new(chain.Spec, chain.Anchor.AnchorState, chain.Anchor.AnchorBlock.Message!, chain, chain.Anchor.Pubkeys);
        BuildCounter builds = new(runner);
        TickToSlot(runner, targetEpoch * Presets.SlotsPerEpoch + 6);
        UnsignedChain.ChainBlock parent = ImportLine(chain, runner, chain.AnchorRoot, 30)[0];
        UnsignedChain.ChainBlock a = chain.Extend(parent.Root, 31, payloadHashByte: 31);
        UnsignedChain.ChainBlock b = chain.Extend(parent.Root, 32, payloadHashByte: 32);
        runner.OnBlock(a.Block, a.PostState, ExecutionStatus.Optimistic, (IReadOnlyList<DataColumnSidecar>?)null);
        runner.OnBlock(b.Block, b.PostState, ExecutionStatus.Optimistic, (IReadOnlyList<DataColumnSidecar>?)null);
        ulong[] aVoters = [.. new[] { 0, 1 }.Select(skip => (ulong)FirstCommitteeMember(a, targetEpoch - 1, skip).Member).Order()];
        int bSkip = 0;
        while (aVoters.Contains((ulong)FirstCommitteeMember(b, targetEpoch - 1, bSkip).Member))
            bSkip++;
        runner.OnAttestation(BodyVote(chain, a, targetEpoch - 1, skip: 0), isFromBlock: true, verifySignature: false);
        runner.OnAttestation(BodyVote(chain, a, targetEpoch - 1, skip: 1), isFromBlock: true, verifySignature: false);
        runner.OnAttestation(BodyVote(chain, b, targetEpoch - 1, bSkip), isFromBlock: true, verifySignature: false);
        Hash256 headBefore = runner.GetHead();
        if (invalidPayload)
            runner.OnInvalidExecutionPayload(a.Root);
        else
            runner.OnAttesterSlashing(chain.DoubleVote(aVoters, slot: 1, a.Root, b.Root), verifySignatures: false);
        (_, ulong voteSlot, int member) = FirstCommitteeMember(b, targetEpoch);
        int buildsBefore = builds.Count;

        Assert.That(() => runner.OnAggregateAndProof(GossipAggregate(chain, voteSlot, b.Root, targetEpoch, member, signingState: null)), Throws.InstanceOf<Exception>());
        runner.GetCheckpointState(new CheckpointRef(targetEpoch, b.Root));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(aVoters.Distinct().Count(), Is.EqualTo(2), "fixture bug: A needs two distinct voters");
            Assert.That(headBefore, Is.EqualTo(a.Root), "fixture bug: two votes put A ahead before");
            Assert.That(builds.Count - buildsBefore, Is.EqualTo(1), "the aggregate was checked against B's own target state, which the later lookup then finds");
            Assert.That(runner.GetHead(), Is.EqualTo(b.Root), "fixture bug: B is the head after");
        }
    }

    /// <summary>
    /// A held vote state stands in for every target of its shuffling, so only a vote that verified may leave one: two unsigned
    /// votes each build and leave no checkpoint state behind, a trusted body vote builds once and is held, and a later body
    /// vote of that shuffling builds nothing.
    /// </summary>
    [Test]
    public void Vote_state_is_held_only_once_a_vote_verifies_against_it()
    {
        const ulong targetEpoch = 2;
        UnsignedChain chain = UnsignedChain.Create();
        ForkChoiceRunner runner = new(chain.Spec, chain.Anchor.AnchorState, chain.Anchor.AnchorBlock.Message!, chain, chain.Anchor.Pubkeys);
        BuildCounter builds = new(runner);
        TickToSlot(runner, targetEpoch * Presets.SlotsPerEpoch + 6);
        List<UnsignedChain.ChainBlock> targets = ImportLine(chain, runner, chain.AnchorRoot, 33, 41, 50, 60);
        List<int> buildCounts = [];

        foreach (UnsignedChain.ChainBlock target in targets.Take(2))
        {
            Assert.That(() => runner.OnAttestation(BodyVote(chain, target, targetEpoch)), Throws.TypeOf<ForkChoiceException>(), "fixture: the vote is unsigned");
            buildCounts.Add(builds.Count);
        }

        // A refused vote's state is not its target's checkpoint state either, so asking for that builds again.
        runner.GetCheckpointState(new CheckpointRef(targetEpoch, targets[0].Root));
        buildCounts.Add(builds.Count);
        foreach (UnsignedChain.ChainBlock target in targets.Skip(2))
        {
            runner.OnAttestation(BodyVote(chain, target, targetEpoch), isFromBlock: true, verifySignature: false);
            buildCounts.Add(builds.Count);
        }

        Assert.That(buildCounts, Is.EqualTo((int[])[1, 2, 3, 4, 4]));
    }

    /// <summary>
    /// Each held vote state is a whole checkpoint state, so at most eight are kept: of nine shufflings (nine siblings at or before
    /// the decision slot, each its own decision block) the least recently used goes. The first is used again through its own
    /// cached checkpoint state before the ninth arrives, so the second is the one evicted and a later target of it builds again.
    /// </summary>
    [Test]
    public void Vote_states_are_bounded_and_the_least_recently_used_shuffling_goes_first()
    {
        const ulong targetEpoch = 2;
        const int MaxVoteStates = 8;
        UnsignedChain chain = UnsignedChain.Create();
        ForkChoiceRunner runner = new(chain.Spec, chain.Anchor.AnchorState, chain.Anchor.AnchorBlock.Message!, chain, chain.Anchor.Pubkeys);
        BuildCounter builds = new(runner);
        TickToSlot(runner, targetEpoch * Presets.SlotsPerEpoch + 6);
        List<UnsignedChain.ChainBlock> siblings = [];
        for (ulong slot = 1; slot <= MaxVoteStates + 1; slot++)
        {
            siblings.Add(ImportLine(chain, runner, chain.AnchorRoot, slot)[0]);
        }

        UnsignedChain.ChainBlock firstChild = ImportLine(chain, runner, siblings[0].Root, 40)[0];
        UnsignedChain.ChainBlock secondChild = ImportLine(chain, runner, siblings[1].Root, 41)[0];
        foreach (UnsignedChain.ChainBlock sibling in siblings.Take(MaxVoteStates))
        {
            runner.OnAttestation(BodyVote(chain, sibling, targetEpoch), isFromBlock: true, verifySignature: false);
        }

        runner.OnAttestation(BodyVote(chain, siblings[0], targetEpoch), isFromBlock: true, verifySignature: false);
        runner.OnAttestation(BodyVote(chain, siblings[^1], targetEpoch), isFromBlock: true, verifySignature: false);
        int afterSiblings = builds.Count;
        runner.OnAttestation(BodyVote(chain, firstChild, targetEpoch), isFromBlock: true, verifySignature: false);
        int afterFirstChild = builds.Count;
        runner.OnAttestation(BodyVote(chain, secondChild, targetEpoch), isFromBlock: true, verifySignature: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterSiblings, Is.EqualTo(MaxVoteStates + 1), "every sibling is its own shuffling, and the repeat is cached");
            Assert.That(afterFirstChild, Is.EqualTo(afterSiblings), "the first shuffling, used again, is still held");
            Assert.That(builds.Count, Is.EqualTo(afterSiblings + 1), "the second shuffling was the least recently used and was evicted");
        }
    }

    /// <summary>
    /// No block from an epoch before the previous one can be imported with a vote for it, so its held state is dropped on the
    /// tick into the epoch after next: a body vote for another target of that shuffling then builds again.
    /// </summary>
    [Test]
    public void Vote_states_of_epochs_before_the_previous_one_are_dropped_on_the_tick()
    {
        const ulong targetEpoch = 2;
        UnsignedChain chain = UnsignedChain.Create();
        ForkChoiceRunner runner = new(chain.Spec, chain.Anchor.AnchorState, chain.Anchor.AnchorBlock.Message!, chain, chain.Anchor.Pubkeys);
        BuildCounter builds = new(runner);
        TickToSlot(runner, targetEpoch * Presets.SlotsPerEpoch + 6);
        List<UnsignedChain.ChainBlock> targets = ImportLine(chain, runner, chain.AnchorRoot, 33, 41, 50);
        runner.OnAttestation(BodyVote(chain, targets[0], targetEpoch), isFromBlock: true, verifySignature: false);
        TickToSlot(runner, (targetEpoch + 1) * Presets.SlotsPerEpoch);
        runner.OnAttestation(BodyVote(chain, targets[1], targetEpoch), isFromBlock: true, verifySignature: false);
        int afterPreviousEpochVote = builds.Count;
        TickToSlot(runner, (targetEpoch + 2) * Presets.SlotsPerEpoch);
        runner.OnAttestation(BodyVote(chain, targets[2], targetEpoch), isFromBlock: true, verifySignature: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterPreviousEpochVote, Is.EqualTo(1), "a shuffling of the previous epoch is still held");
            Assert.That(builds.Count, Is.EqualTo(2), "the epoch-2 shuffling was dropped two epochs on");
        }
    }

    /// <summary>
    /// p2p-interface.md beacon_aggregate_and_proof IGNOREs an aggregate whose voted block does not descend from the finalized
    /// checkpoint. With the first Gloas block finalized, a vote for the anchor before it is refused for that reason, ahead of
    /// any target state or signature work.
    /// </summary>
    [Test]
    public void Gossip_aggregate_for_a_block_off_the_finalized_chain_is_refused()
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = FinalizedOnFirstGloasBlock(chain);
        const ulong voteEpoch = 2;
        AttestationData data = new()
        {
            Slot = voteEpoch * Presets.SlotsPerEpoch,
            Index = 0,
            BeaconBlockRoot = chain.AnchorRoot,
            Source = new Checkpoint { Epoch = 0, Root = chain.AnchorRoot },
            Target = new Checkpoint { Epoch = voteEpoch, Root = chain.AnchorRoot },
        };
        BlsSignature unsigned = new(SignatureSets.G2PointAtInfinity);
        SignedAggregateAndProofGloas aggregate = new()
        {
            Message = new AggregateAndProofGloas
            {
                AggregatorIndex = 0,
                Aggregate = new AttestationGloas
                {
                    AggregationBits = new BitArray((int)CommitteeSize, true),
                    Data = data,
                    Signature = unsigned,
                    CommitteeBits = new BitArray(Presets.MaxCommitteesPerSlot) { [0] = true },
                },
                SelectionProof = unsigned,
            },
            Signature = unsigned,
        };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(runner.FinalizedCheckpoint, Is.EqualTo(new CheckpointRef(ForkCrossingChain.ForkEpoch, chain.First.Root)), "fixture bug: the first Gloas block must be finalized");
            Assert.That(() => runner.OnAggregateAndProof(aggregate), Throws.TypeOf<ForkChoiceException>().With.Message.Contains("finalized checkpoint"));
        }
    }

    /// <summary>
    /// The epoch-2 shuffling is fixed by the last block at or before slot 31. A at slot 31 is its own decision block and its
    /// sibling B at slot 32 has their parent, so they never share a state; X at slot 32 and its child Y both have X's parent
    /// at slot 30, so they do. A decision slot off by one either way merges the first pair or splits the second.
    /// </summary>
    [Test]
    public void Targets_share_a_vote_state_exactly_when_the_decision_slot_gives_them_one_block()
    {
        const ulong targetEpoch = 2;
        UnsignedChain chain = UnsignedChain.Create();
        ForkChoiceRunner runner = new(chain.Spec, chain.Anchor.AnchorState, chain.Anchor.AnchorBlock.Message!, chain, chain.Anchor.Pubkeys);
        BuildCounter builds = new(runner);
        TickToSlot(runner, targetEpoch * Presets.SlotsPerEpoch + 6);
        UnsignedChain.ChainBlock parent = ImportLine(chain, runner, chain.AnchorRoot, 30)[0];
        UnsignedChain.ChainBlock a = ImportLine(chain, runner, parent.Root, 31)[0];
        List<UnsignedChain.ChainBlock> xy = ImportLine(chain, runner, parent.Root, 32, 40);

        runner.OnAttestation(BodyVote(chain, a, targetEpoch), isFromBlock: true, verifySignature: false);
        runner.OnAttestation(BodyVote(chain, xy[0], targetEpoch), isFromBlock: true, verifySignature: false);
        int afterSiblings = builds.Count;
        runner.OnAttestation(BodyVote(chain, xy[1], targetEpoch), isFromBlock: true, verifySignature: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterSiblings, Is.EqualTo(2), "the slot-31 block and its slot-32 sibling have two shufflings");
            Assert.That(builds.Count, Is.EqualTo(2), "the slot-40 child shares the slot-32 block's shuffling");
        }
    }

    /// <summary>
    /// A tree rooted after the decision slot holds none of the decision blocks, but every block in it descends from the root,
    /// so they all share the root's own ancestor there and one state serves every target of that epoch.
    /// </summary>
    [Test]
    public void Targets_whose_decision_slot_is_below_the_tree_root_share_one_vote_state()
    {
        const ulong targetEpoch = 2;
        UnsignedChain chain = UnsignedChain.Create();
        UnsignedChain.ChainBlock root = chain.Extend(chain.AnchorRoot, Presets.SlotsPerEpoch, payloadHashByte: 32);
        ForkChoiceRunner runner = new(chain.Spec, root.PostState, root.Block.Message!, chain, chain.Anchor.Pubkeys);
        BuildCounter builds = new(runner);
        TickToSlot(runner, targetEpoch * Presets.SlotsPerEpoch + 6);
        List<UnsignedChain.ChainBlock> targets = ImportLine(chain, runner, root.Root, 50, 60);

        foreach (UnsignedChain.ChainBlock target in targets)
        {
            runner.OnAttestation(BodyVote(chain, target, targetEpoch), isFromBlock: true, verifySignature: false);
        }

        Assert.That(builds.Count, Is.EqualTo(1));
    }

    /// <summary>A Gloas checkpoint state names its decision block from its own block roots too, so two Gloas targets of one shuffling share a state.</summary>
    [Test]
    public void Gloas_targets_of_one_shuffling_share_one_vote_state()
    {
        const ulong targetEpoch = 3;
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = JustifiedOnFirstGloasBlock(chain);
        BuildCounter builds = new(runner);
        ulong epochStart = targetEpoch * Presets.SlotsPerEpoch;
        BeaconStateGloas atEpochStart = chain.Voting[^1].PostState.Clone();
        GloasSlotProcessing.ProcessSlots(atEpochStart, epochStart, new EpochCache { Hasher = new CachedBeaconStateHasher() });
        int committeeSize = new EpochCache().GetCommitteeCache(atEpochStart, targetEpoch).GetBeaconCommittee(epochStart, 0).Length;

        foreach (ForkCrossingChain.ChainBlock target in (ForkCrossingChain.ChainBlock[])[chain.Voting[0], chain.Voting[^1]])
        {
            AttestationGloas vote = new()
            {
                AggregationBits = new BitArray(committeeSize, true),
                Data = new AttestationData
                {
                    Slot = epochStart,
                    Index = 0,
                    BeaconBlockRoot = target.Root,
                    Source = new Checkpoint { Epoch = ForkCrossingChain.ForkEpoch, Root = chain.First.Root },
                    Target = new Checkpoint { Epoch = targetEpoch, Root = target.Root },
                },
                Signature = new BlsSignature(SignatureSets.G2PointAtInfinity),
                CommitteeBits = new BitArray(Presets.MaxCommitteesPerSlot) { [0] = true },
            };
            runner.OnAttestation(vote, isFromBlock: true, verifySignature: false);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(committeeSize, Is.Positive, "fixture bug: slot 96 must have a committee");
            Assert.That(builds.Count, Is.EqualTo(1), "both blocks have the first Gloas block at slot 32 as their decision block for epoch 3");
        }
    }

    /// <summary>
    /// <c>on_attester_slashing</c> reads the justified block's state; with a Gloas justified root that
    /// state is only in the Gloas provider. Whichever container carried the slashing, only the validators
    /// named by both attestations equivocated: the first and the last 40 of slot 32's committee share 16,
    /// and exactly those 16 votes leave the first Gloas block's weight. A gossiped slashing is verified
    /// against that Gloas state, so one whose second signature is forged discounts nobody.
    /// </summary>
    [Test]
    public void Attester_slashing_is_checked_against_a_gloas_justified_state_and_discounts_the_equivocators([Values] bool fuluContainer, [Values] bool forged)
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = JustifiedOnFirstGloasBlock(chain);
        ulong weightBefore = Weight(runner, chain.First.Root);
        Hash256 domain = chain.First.PostState.GetDomain(DomainType.BeaconAttester, ForkCrossingChain.ForkEpoch);
        AttestationData vote1 = GloasTestFixtures.Vote(GloasTestFixtures.BoundarySlot, 0, ForkCrossingChain.ForkEpoch, 0x31);
        AttestationData vote2 = GloasTestFixtures.Vote(GloasTestFixtures.BoundarySlot, 0, ForkCrossingChain.ForkEpoch, 0x41);
        const int SignerCount = 40;
        const int SharedSigners = 2 * SignerCount - (int)CommitteeSize;
        IndexedAttestationGloas attestation1 = SignedUnder(domain, vote1, chain.Committee32[..SignerCount]);
        IndexedAttestationGloas attestation2 = SignedUnder(domain, vote2, chain.Committee32[^SignerCount..]);
        if (forged)
            attestation2.Signature = SignedUnder(domain, vote1, attestation2.AttestingIndices!).Signature;

        Action slash = fuluContainer
            ? () => runner.OnAttesterSlashing(new AttesterSlashing { Attestation1 = ToFuluIndexed(attestation1), Attestation2 = ToFuluIndexed(attestation2) })
            : () => runner.OnAttesterSlashing(new AttesterSlashingGloas { Attestation1 = attestation1, Attestation2 = attestation2 });

        Assert.That(chain.Committee32, Has.Length.EqualTo((int)CommitteeSize), "fixture bug");
        Assert.That(runner.JustifiedCheckpoint.Root, Is.EqualTo(chain.First.Root), "fixture bug");
        if (forged)
            Assert.That(slash, Throws.TypeOf<ForkChoiceException>().With.Message.Contains("attestation 2 is invalid"));
        else
            Assert.That(slash, Throws.Nothing);
        Assert.That(Weight(runner, chain.First.Root), Is.EqualTo(forged ? weightBefore : weightBefore - SharedSigners * EffectiveBalance));
    }

    /// <summary>
    /// <c>on_attester_slashing</c> verifies against <c>store.block_states[justified.root]</c>, not the justified
    /// checkpoint state. With the justified checkpoint at the fork epoch on the Fulu anchor, the block state still
    /// signs epoch-1 votes under the Fulu fork version, while the checkpoint state has crossed into Gloas.
    /// </summary>
    [Test]
    public void Attester_slashing_is_verified_under_the_justified_blocks_own_state([Values] bool signedUnderBlockStateFork)
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = chain.CreateRunner();
        TickToSlot(runner, GloasTestFixtures.BoundarySlot + 1);
        BeaconStateGloas justifiesAnchorAtForkEpoch = chain.First.PostState.Clone();
        justifiesAnchorAtForkEpoch.CurrentJustifiedCheckpoint = new Checkpoint { Epoch = ForkCrossingChain.ForkEpoch, Root = chain.AnchorRoot };
        runner.OnBlock(chain.First.Block, justifiesAnchorAtForkEpoch);

        Hash256 blockStateDomain = chain.AnchorState.GetDomain(DomainType.BeaconAttester, ForkCrossingChain.ForkEpoch);
        Hash256 checkpointStateDomain = UpgradedAnchor(chain).GetDomain(DomainType.BeaconAttester, ForkCrossingChain.ForkEpoch);
        Hash256 domain = signedUnderBlockStateFork ? blockStateDomain : checkpointStateDomain;
        ulong[] signers = chain.Committee32[..8];
        AttesterSlashingGloas slashing = new()
        {
            Attestation1 = SignedUnder(domain, GloasTestFixtures.Vote(GloasTestFixtures.BoundarySlot, 0, ForkCrossingChain.ForkEpoch, 0x31), signers),
            Attestation2 = SignedUnder(domain, GloasTestFixtures.Vote(GloasTestFixtures.BoundarySlot, 0, ForkCrossingChain.ForkEpoch, 0x41), signers),
        };

        Assert.That(runner.JustifiedCheckpoint, Is.EqualTo(new CheckpointRef(ForkCrossingChain.ForkEpoch, chain.AnchorRoot)), "fixture bug");
        Assert.That(blockStateDomain, Is.Not.EqualTo(checkpointStateDomain), "fixture bug: the two states must sign under different fork versions");
        if (signedUnderBlockStateFork)
            Assert.That(() => runner.OnAttesterSlashing(slashing), Throws.Nothing);
        else
            Assert.That(() => runner.OnAttesterSlashing(slashing), Throws.TypeOf<ForkChoiceException>().With.Message.Contains("attestation 1 is invalid"));
    }

    /// <summary>
    /// A vote is verified against its target checkpoint state, here always a Gloas state, whichever container
    /// carried it: a vote whose aggregate signature is over other data must move no weight, or anyone could
    /// steer the head with votes attributed to a committee.
    /// </summary>
    [Test]
    public void Vote_checked_against_a_gloas_target_state_counts_only_with_a_valid_signature(
        [Values] bool targetFirstGloasBlock, [Values] bool fuluContainer, [Values] bool forged)
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        (ForkChoiceRunner runner, Hash256 target, BeaconStateGloas signingState) = RunnerForBoundaryVote(chain, targetFirstGloasBlock, pubkeys: null);
        AttestationGloas attestation = SignedBoundaryVote(signingState, EpochOneVote(chain, GloasTestFixtures.BoundarySlot, target));
        if (forged)
        {
            AttestationData other = EpochOneVote(chain, GloasTestFixtures.BoundarySlot, target);
            other.Source = new Checkpoint { Epoch = 0, Root = GloasTestFixtures.Hash(0x99) };
            attestation.Signature = SignedBoundaryVote(signingState, other).Signature;
        }

        Action vote = fuluContainer
            ? () => runner.OnAttestation(GloasTestFixtures.ToFuluAttestation(attestation))
            : () => runner.OnAttestation(attestation);

        if (forged)
            Assert.That(vote, Throws.TypeOf<ForkChoiceException>().With.Message.Contains("signature"));
        else
            Assert.That(vote, Throws.Nothing);
        Assert.That(Weight(runner, target), Is.EqualTo(forged ? 0 : CommitteeSize * EffectiveBalance));
    }

    /// <summary>
    /// <c>on_attestation</c> counts a gossiped vote only from the slot after its own, and a gossiped vote from a
    /// future slot is refused outright; only votes from block bodies skip these wall-clock checks.
    /// </summary>
    [Test]
    public void Gossip_vote_counts_only_from_the_slot_after_its_own([Values] bool fuluContainer)
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ulong currentSlot = GloasTestFixtures.BoundarySlot + 1;
        ForkChoiceRunner runner = chain.CreateRunner();
        TickToSlot(runner, currentSlot);
        runner.OnBlock(chain.First.Block, chain.First.PostState);
        BeaconStateGloas signingState = chain.First.PostState;
        AttestationGloas currentSlotVote = SignedBoundaryVote(signingState, EpochOneVote(chain, currentSlot, chain.First.Root));
        AttestationGloas futureSlotVote = SignedBoundaryVote(signingState, EpochOneVote(chain, currentSlot + 1, chain.First.Root));
        void Gossip(AttestationGloas attestation)
        {
            if (fuluContainer)
                runner.OnAttestation(GloasTestFixtures.ToFuluAttestation(attestation));
            else
                runner.OnAttestation(attestation);
        }

        Assert.That(() => Gossip(futureSlotVote), Throws.TypeOf<ForkChoiceException>().With.Message.Contains("from the future"));
        Gossip(currentSlotVote);
        ulong weightInItsOwnSlot = Weight(runner, chain.First.Root);
        TickToSlot(runner, currentSlot + 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(runner.ProposerBoostRoot, Is.EqualTo(Hash256.Zero), "fixture bug: no boost may confound the weight");
            Assert.That(weightInItsOwnSlot, Is.Zero);
            Assert.That(Weight(runner, chain.First.Root), Is.EqualTo(CommitteeSize * EffectiveBalance));
        }
    }

    /// <summary>
    /// <c>is_valid_indexed_attestation</c> still guards a slashing checked against a Gloas justified state:
    /// indices that are unsorted, or name a validator the justified registry lacks, refuse the slashing
    /// whole, whichever container carried it, and nobody is discounted.
    /// </summary>
    [Test]
    public void Attester_slashing_with_indices_a_gloas_justified_state_rejects_is_refused([Values] bool fuluContainer, [Values] bool outOfRange)
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = JustifiedOnFirstGloasBlock(chain);
        ulong weightBefore = Weight(runner, chain.First.Root);
        AttestationData vote1 = GloasTestFixtures.Vote(GloasTestFixtures.BoundarySlot, 0, ForkCrossingChain.ForkEpoch, 0x31);
        AttestationData vote2 = GloasTestFixtures.Vote(GloasTestFixtures.BoundarySlot, 0, ForkCrossingChain.ForkEpoch, 0x41);
        ulong[] committee = chain.Committee32;
        ulong[] invalid = outOfRange ? [.. committee, (ulong)GloasTestFixtures.ValidatorCount] : [.. Enumerable.Reverse(committee)];

        Action slash = fuluContainer
            ? () => runner.OnAttesterSlashing(new AttesterSlashing
            {
                Attestation1 = new IndexedAttestation { AttestingIndices = invalid, Data = vote1 },
                Attestation2 = new IndexedAttestation { AttestingIndices = invalid, Data = vote2 },
            }, verifySignatures: false)
            : () => runner.OnAttesterSlashing(new AttesterSlashingGloas
            {
                Attestation1 = new IndexedAttestationGloas { AttestingIndices = invalid, Data = vote1 },
                Attestation2 = new IndexedAttestationGloas { AttestingIndices = invalid, Data = vote2 },
            }, verifySignatures: false);

        Assert.That(runner.JustifiedCheckpoint.Root, Is.EqualTo(chain.First.Root), "fixture bug");
        Assert.That(slash, Throws.TypeOf<ForkChoiceException>().With.Message.Contains("invalid"));
        Assert.That(Weight(runner, chain.First.Root), Is.EqualTo(weightBefore), "a refused slashing discounts nobody");
    }

    /// <summary>
    /// The Gloas <c>is_valid_indexed_attestation</c> caps attesting indices at <c>MAX_VALIDATORS_PER_COMMITTEE *
    /// MAX_COMMITTEES_PER_SLOT</c> (specs/gloas/beacon-chain.md, EIP-7688), since the progressive list has no SSZ
    /// limit. A Gloas slashing is held to it against a Fulu justified state too, and before any signature is
    /// aggregated, whichever attestation is oversized: the other carries a forged signature, and reaching
    /// verification refuses attestation 1 as invalid, which an at-bound slashing does and a one-over one must not.
    /// </summary>
    [Test]
    public void Gloas_attester_slashing_is_bounded_before_any_signature_is_verified(
        [Values] bool gloasJustified, [Values] bool overBound, [Values] bool oversizedFirst)
    {
        const int Bound = Presets.MaxValidatorsPerCommittee * Presets.MaxCommitteesPerSlot;
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = gloasJustified ? JustifiedOnFirstGloasBlock(chain) : chain.CreateRunner();
        Hash256 domain = chain.First.PostState.GetDomain(DomainType.BeaconAttester, ForkCrossingChain.ForkEpoch);
        AttestationData vote1 = GloasTestFixtures.Vote(GloasTestFixtures.BoundarySlot, 0, ForkCrossingChain.ForkEpoch, 0x31);
        AttestationData vote2 = GloasTestFixtures.Vote(GloasTestFixtures.BoundarySlot, 0, ForkCrossingChain.ForkEpoch, 0x41);
        ulong[] signers = chain.Committee32[..8];
        IndexedAttestationGloas forged = SignedUnder(domain, vote1, signers);
        forged.Signature = SignedUnder(domain, vote2, signers).Signature;
        IndexedAttestationGloas oversized = new()
        {
            AttestingIndices = [.. Enumerable.Range(0, overBound ? Bound + 1 : Bound).Select(static i => (ulong)i)],
            Data = vote2,
        };

        Assert.That(runner.JustifiedCheckpoint.Root, Is.EqualTo(gloasJustified ? chain.First.Root : chain.AnchorRoot), "fixture bug");
        AttesterSlashingGloas slashing = oversizedFirst
            ? new AttesterSlashingGloas { Attestation1 = oversized, Attestation2 = forged }
            : new AttesterSlashingGloas { Attestation1 = forged, Attestation2 = oversized };
        string oversizedName = oversizedFirst ? "attestation 1" : "attestation 2";
        Assert.That(() => runner.OnAttesterSlashing(slashing),
            Throws.TypeOf<ForkChoiceException>().With.Message.Contains(overBound ? $"{oversizedName} has {Bound + 1} attesting indices" : "attestation 1 is invalid"));
    }

    /// <summary>The Gloas <c>is_valid_indexed_attestation</c> bound admits exactly <c>MAX_VALIDATORS_PER_COMMITTEE * MAX_COMMITTEES_PER_SLOT</c> indices.</summary>
    [TestCase(Presets.MaxValidatorsPerCommittee * Presets.MaxCommitteesPerSlot, false)]
    [TestCase(Presets.MaxValidatorsPerCommittee * Presets.MaxCommitteesPerSlot + 1, true)]
    public void Gloas_indexed_attestation_bound_refuses_one_index_over_it(int count, bool refused)
    {
        ulong[] indices = new ulong[count];
        Assert.That(() => ForkChoiceRunner.ThrowIfOverGloasIndexedAttestationBound(indices, "Vote"),
            refused ? Throws.TypeOf<ForkChoiceException>().With.Message.EqualTo($"Vote has {count} attesting indices, over the bound of 131072") : Throws.Nothing);
    }

    /// <summary>
    /// A Gloas attestation against a Fulu target state, whose <c>is_valid_indexed_attestation</c> has no length bound, is still
    /// held to the Gloas one. A slot's committees exceed 131072 members only past 4194304 active validators, so the target
    /// state carries 4194336: one slot's 64 committees then hold 131073.
    /// </summary>
    [Test]
    public void Gloas_attestation_over_the_indexed_attestation_bound_is_refused_against_a_fulu_target_state()
    {
        const int Bound = Presets.MaxValidatorsPerCommittee * Presets.MaxCommitteesPerSlot;
        const int ActiveValidators = (Bound + 1) * (int)Presets.SlotsPerEpoch;
        UnsignedChain chain = UnsignedChain.Create();
        BeaconStateFulu anchorState = chain.Anchor.AnchorState;
        Assert.That(anchorState.Slot, Is.Zero, "fixture bug: the vote is for the anchor's own slot, so the target state needs no advance");
        BeaconStateFulu targetState = anchorState.Clone();
        Validator template = anchorState.Validators![0];
        targetState.Validators = new Validator[ActiveValidators];
        for (int i = 0; i < ActiveValidators; i++)
        {
            targetState.Validators[i] = new Validator
            {
                Pubkey = template.Pubkey,
                WithdrawalCredentials = template.WithdrawalCredentials,
                EffectiveBalance = template.EffectiveBalance,
                ActivationEpoch = 0,
                ExitEpoch = Presets.FarFutureEpoch,
                WithdrawableEpoch = Presets.FarFutureEpoch,
            };
        }

        InMemoryStates states = new();
        states.States[chain.AnchorRoot] = targetState;
        ForkChoiceRunner runner = new(chain.Spec, anchorState, chain.Anchor.AnchorBlock.Message!, states, chain.Anchor.Pubkeys);
        AttestationGloas vote = new()
        {
            AggregationBits = new System.Collections.BitArray(Bound + 1, true),
            CommitteeBits = new System.Collections.BitArray(Presets.MaxCommitteesPerSlot, true),
            Data = new AttestationData
            {
                Slot = 0,
                Index = 0,
                BeaconBlockRoot = chain.AnchorRoot,
                Source = anchorState.CurrentJustifiedCheckpoint,
                Target = new Checkpoint { Epoch = 0, Root = chain.AnchorRoot },
            },
        };

        Assert.That(() => runner.OnAttestation(vote, isFromBlock: true, verifySignature: false),
            Throws.TypeOf<ForkChoiceException>().With.Message.EqualTo($"Attestation has {Bound + 1} attesting indices, over the bound of {Bound}"));
    }

    /// <summary>
    /// A checkpoint state is advanced on a copy of its block state. A provider that loses the state between the
    /// lookup and the copy must make fork choice refuse the vote, not crash on a missing state.
    /// </summary>
    [Test]
    public void Checkpoint_state_evicted_before_its_copy_refuses_the_vote()
    {
        UnsignedChain chain = UnsignedChain.Create();
        ForkChoiceRunner runner = new(chain.Spec, chain.Anchor.AnchorState, chain.Anchor.AnchorBlock.Message!, new EvictedBeforeCopy(chain), chain.Anchor.Pubkeys);
        ulong slot = Presets.SlotsPerEpoch + 1;
        Attestation vote = new()
        {
            AggregationBits = new System.Collections.BitArray(1, true),
            CommitteeBits = new System.Collections.BitArray(Presets.MaxCommitteesPerSlot) { [0] = true },
            Data = new AttestationData
            {
                Slot = slot,
                Index = 0,
                BeaconBlockRoot = chain.AnchorRoot,
                Source = chain.Anchor.AnchorState.CurrentJustifiedCheckpoint,
                Target = new Checkpoint { Epoch = 1, Root = chain.AnchorRoot },
            },
        };

        Assert.That(() => runner.OnAttestation(vote, isFromBlock: true, verifySignature: false),
            Throws.TypeOf<ForkChoiceException>().With.Message.Contains($"No state for the block {chain.AnchorRoot}"));
    }

    /// <summary>
    /// <c>get_weight</c> counts the validators active at the justified state's own epoch, so one exiting
    /// the epoch after still carries weight; measured here through the proposer boost, which is a
    /// committee fraction of the justified balance: 16 validators x 32 ETH / 32 slots x 40% = 6.4 ETH.
    /// </summary>
    [Test]
    public void Justified_balances_count_validators_exiting_the_epoch_after_the_checkpoint()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        BeaconStateFulu justifiedState = chain.AnchorState.Clone();
        Validator[] validators = justifiedState.Validators!;
        for (int i = 0; i < validators.Length; i += 2)
        {
            validators[i] = validators[i].Clone();
            validators[i].ExitEpoch = 1;
        }

        (ForkChoiceRunner runner, BeaconStateFulu postState) = RunnerAt(chain, justifiedState);
        runner.OnBlock(chain.Block, postState, ExecutionStatus.Valid, chain.Columns);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(validators, Has.Length.EqualTo(16), "fixture bug");
            Assert.That(validators.Select(v => v.EffectiveBalance), Is.All.EqualTo(EffectiveBalance), "fixture bug");
            Assert.That(runner.ProposerBoostRoot, Is.EqualTo(chain.BlockRoot), "fixture bug: the block must be timely");
            Assert.That(Weight(runner, chain.BlockRoot), Is.EqualTo(6_400_000_000ul));
        }
    }

    /// <summary>
    /// Resolving a block state picks the fork by comparing the slot's epoch with the Gloas fork epoch
    /// alone: the mainnet fork-choice vectors run mainnet's schedule from epoch 0, where no fork this
    /// driver models is live yet, and must still get a head.
    /// </summary>
    [Test]
    public void Head_resolves_under_a_schedule_whose_modelled_forks_all_postdate_the_anchor()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        InMemoryStates states = new();
        states.States[chain.AnchorRoot] = chain.AnchorState;
        ForkChoiceRunner runner = new(BeaconChainSpec.Mainnet, chain.AnchorState, chain.AnchorBlock.Message!, states, chain.Pubkeys);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(BeaconChainSpec.Mainnet.ElectraForkEpoch, Is.GreaterThan(0ul), "fixture bug");
            Assert.That(runner.GetHead(), Is.EqualTo(chain.AnchorRoot));
        }
    }

    /// <summary>
    /// A Gloas block carries only the bid; its payload arrives later in an envelope, so it enters fork
    /// choice optimistic, keyed by the committed bid's block hash - the hash the invalidation walk maps a
    /// latest valid hash through - and never by the parent's applied hash its post-state records.
    /// </summary>
    [Test]
    public void Gloas_block_is_registered_optimistic_under_its_bids_block_hash()
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = chain.CreateRunner();
        TickToSlot(runner, GloasTestFixtures.BoundarySlot);

        runner.OnBlock(chain.First.Block, chain.First.PostState);

        Hash256 bidBlockHash = chain.First.Block.Message!.Body!.SignedExecutionPayloadBid!.Message!.BlockHash!;
        ForkChoiceSnapshotNode node = runner.Snapshot().Nodes.Single(n => n.Root == chain.First.Root);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.First.PostState.LatestBlockHash, Is.Not.EqualTo(bidBlockHash), "fixture bug: the parent's applied hash must differ from the bid's");
            Assert.That(node.ExecutionStatus, Is.EqualTo(ExecutionStatus.Optimistic));
            Assert.That(node.ExecutionBlockHash, Is.EqualTo(bidBlockHash));
            Assert.That(runner.GetExecutionBlockHash(chain.First.Root), Is.EqualTo(bidBlockHash));
        }
    }

    /// <summary>
    /// Each <c>OnBlock</c> overload admits only its own fork's slots: a Fulu-shaped block in a Gloas epoch
    /// would be registered with a Fulu post-state no checkpoint lookup there could use, and a Gloas-shaped
    /// block before the fork has no committed bid for the proto-array to key it by.
    /// </summary>
    [Test]
    public void Each_OnBlock_overload_refuses_a_slot_of_the_other_fork()
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = chain.CreateRunner();
        TickToSlot(runner, GloasTestFixtures.BoundarySlot);
        SignedBeaconBlock fuluAtFork = TestChain.CreateBlock(GloasTestFixtures.BoundarySlot, chain.AnchorRoot);
        SignedBeaconBlockGloas gloasBeforeFork = new() { Message = new BeaconBlockGloas { Slot = GloasTestFixtures.BoundarySlot - 1, ParentRoot = chain.AnchorRoot } };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => runner.OnBlock(fuluAtFork, chain.AnchorState, ExecutionStatus.Valid, new RecordingRule(verdict: true)),
                Throws.TypeOf<ForkChoiceException>().With.Message.Contains(nameof(SignedBeaconBlockGloas)));
            Assert.That(runner.ContainsBlock(SszRoots.HashTreeRoot(fuluAtFork.Message!)), Is.False);
            Assert.That(() => runner.OnBlock(gloasBeforeFork, chain.First.PostState),
                Throws.TypeOf<ForkChoiceException>().With.Message.Contains("before the Gloas fork"));
        }
    }

    /// <summary>
    /// Without a Gloas state provider no Gloas checkpoint state could ever be resolved, so admitting the
    /// block would only defer the failure to the first justification on it: it is refused up front.
    /// </summary>
    [Test]
    public void Gloas_block_is_refused_by_a_runner_without_a_gloas_state_provider()
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = chain.CreateRunner(withGloasStates: false);
        TickToSlot(runner, GloasTestFixtures.BoundarySlot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => runner.OnBlock(chain.First.Block, chain.First.PostState),
                Throws.TypeOf<ForkChoiceException>().With.Message.Contains(nameof(IGloasBlockStateProvider)));
            Assert.That(runner.ContainsBlock(chain.First.Root), Is.False);
        }
    }

    /// <summary>
    /// <c>update_checkpoints</c> takes the store's justified checkpoint from the Gloas post-state's
    /// <c>current_justified_checkpoint</c> and the finalized one from its <c>finalized_checkpoint</c>, and the
    /// block's node carries the same pair; the post-state is doctored so that its previous justified,
    /// current justified and finalized checkpoints are three different ones.
    /// </summary>
    [Test]
    public void Gloas_block_realizes_its_post_states_current_justified_and_finalized_checkpoints()
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = FinalizedOnFirstGloasBlock(chain);
        ForkChoiceSnapshotNode node = runner.Snapshot().Nodes.Single(n => n.Root == chain.Voting[1].Root);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(runner.JustifiedCheckpoint, Is.EqualTo(new CheckpointRef(DoctoredJustifiedEpoch, chain.Voting[0].Root)));
            Assert.That(runner.FinalizedCheckpoint, Is.EqualTo(new CheckpointRef(ForkCrossingChain.ForkEpoch, chain.First.Root)));
            Assert.That(node.JustifiedEpoch, Is.EqualTo(DoctoredJustifiedEpoch));
            Assert.That(node.FinalizedEpoch, Is.EqualTo(ForkCrossingChain.ForkEpoch));
        }
    }

    /// <summary>
    /// The Gloas <c>on_block</c> keeps the store assertions: no block from a future slot, none at or before
    /// the finalized slot, and none off the finalized checkpoint's chain, since fork choice must never weigh
    /// a branch that finality excluded. The store is at slot 66 with the first Gloas block (slot 32) finalized;
    /// each block is otherwise importable, so without the assertions it would enter the tree.
    /// </summary>
    [TestCase(67ul, false, "from the future", TestName = "Gloas_block_from_a_future_slot_is_refused")]
    [TestCase(32ul, true, "not after the finalized slot", TestName = "Gloas_block_at_the_finalized_slot_is_refused")]
    [TestCase(66ul, true, "does not descend from the finalized checkpoint", TestName = "Gloas_block_off_the_finalized_chain_is_refused")]
    public void Gloas_block_violating_an_on_block_store_assertion_is_refused(ulong slot, bool onAnchor, string refusal)
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = FinalizedOnFirstGloasBlock(chain);
        BeaconBlockGloas template = chain.Voting[1].Block.Message!;
        SignedBeaconBlockGloas block = new()
        {
            Message = new BeaconBlockGloas
            {
                Slot = slot,
                ProposerIndex = template.ProposerIndex,
                ParentRoot = onAnchor ? chain.AnchorRoot : chain.Voting[1].Root,
                StateRoot = template.StateRoot,
                Body = template.Body,
            },
        };
        int nodesBefore = runner.Snapshot().Nodes.Count;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(runner.CurrentSlot, Is.EqualTo(2 * Presets.SlotsPerEpoch + 2), "fixture bug");
            Assert.That(runner.FinalizedCheckpoint, Is.EqualTo(new CheckpointRef(ForkCrossingChain.ForkEpoch, chain.First.Root)), "fixture bug");
            Assert.That(() => runner.OnBlock(block, chain.Voting[1].PostState), Throws.TypeOf<ForkChoiceException>().With.Message.Contains(refusal));
            Assert.That(runner.Snapshot().Nodes, Has.Count.EqualTo(nodesBefore));
        }
    }

    /// <summary>
    /// specs/bellatrix/optimistic-sync.md: the parent of an imported block MUST NOT have an INVALIDATED payload.
    /// The child is timely and its post-state is doctored to justify and finalize a later epoch, so a refusal
    /// that came after the store updates would leave the proposer boost and the realized and unrealized
    /// checkpoints behind; the unrealized ones only show once the next epoch pulls them up.
    /// </summary>
    [Test]
    public void Timely_child_of_an_execution_invalid_parent_is_refused_before_any_store_update([Values] bool gloas)
    {
        (ForkChoiceRunner runner, Action importChild, ulong doctoredEpoch) = gloas ? TimelyChildOfInvalidGloasParent() : TimelyChildOfInvalidFuluParent();
        CheckpointRef justifiedBefore = runner.JustifiedCheckpoint;
        CheckpointRef finalizedBefore = runner.FinalizedCheckpoint;
        int nodesBefore = runner.Snapshot().Nodes.Count;
        Assert.That(doctoredEpoch, Is.GreaterThan(Math.Max(justifiedBefore.Epoch, finalizedBefore.Epoch)), "fixture bug: the doctored checkpoints must be adoptable");

        Exception refusal = Assert.Catch(() => importChild());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(refusal, Is.TypeOf<ForkChoiceException>().With.Message.Contains("invalid execution payload"));
            Assert.That(runner.ProposerBoostRoot, Is.EqualTo(Hash256.Zero));
            Assert.That(runner.JustifiedCheckpoint, Is.EqualTo(justifiedBefore));
            Assert.That(runner.FinalizedCheckpoint, Is.EqualTo(finalizedBefore));
            Assert.That(runner.Snapshot().Nodes, Has.Count.EqualTo(nodesBefore));
        }

        TickToSlot(runner, (runner.CurrentSlot / Presets.SlotsPerEpoch + 1) * Presets.SlotsPerEpoch);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(runner.JustifiedCheckpoint, Is.EqualTo(justifiedBefore), "no unrealized justification was recorded");
            Assert.That(runner.FinalizedCheckpoint, Is.EqualTo(finalizedBefore), "no unrealized finalization was recorded");
        }
    }

    /// <summary>
    /// The proto-array accepts an execution block hash exactly when execution is enabled. A block that breaks
    /// this is refused before the store moves, or a timely one would keep the proposer boost it was refused with.
    /// </summary>
    [Test]
    public void Timely_block_with_an_inconsistent_execution_status_is_refused_before_any_store_update()
    {
        UnsignedChain chain = UnsignedChain.Create();
        ForkChoiceRunner runner = new(chain.Spec, chain.Anchor.AnchorState, chain.Anchor.AnchorBlock.Message!, chain, chain.Anchor.Pubkeys);
        TickToSlot(runner, 1);
        UnsignedChain.ChainBlock block = chain.Extend(chain.AnchorRoot, slot: 1, payloadHashByte: 0xc1);
        int nodesBefore = runner.Snapshot().Nodes.Count;
        Assert.That(block.Block.Message!.Body!.ExecutionPayload!.BlockHash, Is.Not.Null, "fixture bug: the block must carry a payload hash");

        Assert.That(() => runner.OnBlock(block.Block, block.PostState, ExecutionStatus.Irrelevant, (IReadOnlyList<DataColumnSidecar>?)null),
            Throws.TypeOf<ForkChoiceException>().With.Message.Contains("execution block hash"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(runner.ProposerBoostRoot, Is.EqualTo(Hash256.Zero));
            Assert.That(runner.Snapshot().Nodes, Has.Count.EqualTo(nodesBefore));
        }
    }

    /// <summary><c>ulong.MaxValue / 32</c>, the last epoch whose start slot fits in 64 bits.</summary>
    private const ulong LastEpochWithAStartSlot = 576460752303423487;

    /// <summary>Epochs that a block of epoch 2 can never name: the next epoch, the last one with a start slot, and the largest.</summary>
    private static ulong[] EpochsAfterTheBlockEpoch() => [3, LastEpochWithAStartSlot, ulong.MaxValue];

    /// <summary>
    /// A post-state never names a checkpoint epoch after its block's own epoch. A block that does is refused before the store
    /// adopts it: a justified epoch near 2^64 would make <c>get_head</c> process slots without end, and a finalized one would
    /// refuse every later block.
    /// </summary>
    [Test]
    public void Timely_block_naming_a_checkpoint_epoch_after_its_own_is_refused_before_any_store_update(
        [Values] bool justified,
        [Values(1ul, LastEpochWithAStartSlot, ulong.MaxValue)] ulong epoch)
    {
        UnsignedChain chain = UnsignedChain.Create();
        ForkChoiceRunner runner = new(chain.Spec, chain.Anchor.AnchorState, chain.Anchor.AnchorBlock.Message!, chain, chain.Anchor.Pubkeys);
        TickToSlot(runner, 1);
        UnsignedChain.ChainBlock block = chain.Extend(chain.AnchorRoot, slot: 1, payloadHashByte: 0xc1);
        BeaconStateFulu doctored = block.PostState.Clone();
        Checkpoint beyond = new() { Epoch = epoch, Root = chain.AnchorRoot };
        if (justified)
            doctored.CurrentJustifiedCheckpoint = beyond;
        else
            doctored.FinalizedCheckpoint = beyond;

        AssertRefusedWithTheStoreUnchanged(runner, block, doctored);
    }

    public enum StateCheckpoint
    {
        Justified,
        Finalized,
    }

    /// <summary>
    /// The same refusal for a prior-epoch block whose own justification weighing (every validator voting for its epoch, possible
    /// from epoch 2) names a sane pulled-up tip: the store must not adopt the state's epoch, nor realize the tip, before the refusal.
    /// </summary>
    [Test]
    public void Prior_epoch_block_with_a_state_checkpoint_epoch_after_its_own_is_refused_before_any_store_update(
        [Values] StateCheckpoint field,
        [ValueSource(nameof(EpochsAfterTheBlockEpoch))] ulong epoch)
    {
        UnsignedChain chain = UnsignedChain.Create();
        ForkChoiceRunner runner = new(chain.Spec, chain.Anchor.AnchorState, chain.Anchor.AnchorBlock.Message!, chain, chain.Anchor.Pubkeys);
        TickToSlot(runner, 3 * Presets.SlotsPerEpoch + 1);
        UnsignedChain.ChainBlock block = chain.Extend(chain.AnchorRoot, slot: 2 * Presets.SlotsPerEpoch + 1, payloadHashByte: 0xc1);
        BeaconStateFulu doctored = block.PostState.Clone();
        Array.Fill(doctored.CurrentEpochParticipation!, (byte)(1 << Presets.TimelyTargetFlagIndex));
        Checkpoint beyond = new() { Epoch = epoch, Root = chain.AnchorRoot };
        if (field == StateCheckpoint.Justified)
        {
            doctored.CurrentJustifiedCheckpoint = beyond;
        }
        else
        {
            // Justifying epoch 2 over a justified epoch 1 finalizes epoch 1, so the pulled-up finalized checkpoint no longer carries the state's.
            doctored.CurrentJustifiedCheckpoint = new Checkpoint { Epoch = 1, Root = chain.AnchorRoot };
            doctored.JustificationBits = new BitArray([true, false, false, false]);
            doctored.FinalizedCheckpoint = beyond;
        }

        JustificationAndFinalizationState pulledUp = EpochProcessing.ComputeJustificationAndFinalization(doctored, new EpochCache());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pulledUp.CurrentJustifiedCheckpoint.Epoch, Is.EqualTo(2ul), "fixture bug: the pulled-up tip must be sane");
            Assert.That(pulledUp.FinalizedCheckpoint.Epoch, Is.LessThanOrEqualTo(2ul), "fixture bug: the pulled-up tip must be sane");
        }

        AssertRefusedWithTheStoreUnchanged(runner, block, doctored);
    }

    /// <summary>
    /// A previous-justified epoch of <c>ulong.MaxValue</c> wraps into the first finalization rule of the block's own weighing, so
    /// only the pulled-up tip names a huge finalized epoch while the post-state's checkpoints are sane.
    /// </summary>
    [Test]
    public void Prior_epoch_block_whose_pulled_up_finalized_epoch_is_after_its_own_is_refused_before_any_store_update()
    {
        UnsignedChain chain = UnsignedChain.Create();
        ForkChoiceRunner runner = new(chain.Spec, chain.Anchor.AnchorState, chain.Anchor.AnchorBlock.Message!, chain, chain.Anchor.Pubkeys);
        TickToSlot(runner, 3 * Presets.SlotsPerEpoch + 1);
        UnsignedChain.ChainBlock block = chain.Extend(chain.AnchorRoot, slot: 2 * Presets.SlotsPerEpoch + 1, payloadHashByte: 0xc1);
        BeaconStateFulu doctored = block.PostState.Clone();
        doctored.PreviousJustifiedCheckpoint = new Checkpoint { Epoch = ulong.MaxValue, Root = chain.AnchorRoot };
        doctored.JustificationBits = new BitArray([true, true, true, false]);

        Assert.That(EpochProcessing.ComputeJustificationAndFinalization(doctored, new EpochCache()).FinalizedCheckpoint.Epoch, Is.EqualTo(ulong.MaxValue), "fixture bug: only the pulled-up tip may be huge");
        AssertRefusedWithTheStoreUnchanged(runner, block, doctored);
    }

    private static void AssertRefusedWithTheStoreUnchanged(ForkChoiceRunner runner, UnsignedChain.ChainBlock block, BeaconStateFulu doctored)
    {
        CheckpointRef justifiedBefore = runner.JustifiedCheckpoint;
        CheckpointRef finalizedBefore = runner.FinalizedCheckpoint;
        int nodesBefore = runner.Snapshot().Nodes.Count;

        Assert.That(() => runner.OnBlock(block.Block, doctored, ExecutionStatus.Valid, (IReadOnlyList<DataColumnSidecar>?)null),
            Throws.TypeOf<ForkChoiceException>().With.Message.Contains("after its own epoch"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(runner.ProposerBoostRoot, Is.EqualTo(Hash256.Zero));
            Assert.That(runner.JustifiedCheckpoint, Is.EqualTo(justifiedBefore));
            Assert.That(runner.FinalizedCheckpoint, Is.EqualTo(finalizedBefore));
            Assert.That(runner.Snapshot().Nodes, Has.Count.EqualTo(nodesBefore));
        }
    }

    /// <summary>
    /// The spec's <c>store.blocks</c> is never pruned, so a proposal on a fork that finality left behind still makes a
    /// later block of the same slot and proposer an equivocation until finality passes that slot. The fork block
    /// (slot 289) and a rival of the finalized block (slot 288) are imported before it, so the proto-array's prune, which drops
    /// everything before the finalized block, removes both: the record at slot 289 stays, the one at the finalized slot goes.
    /// </summary>
    [Test]
    public void Proposer_of_a_pruned_fork_block_is_kept_until_finality_passes_its_slot()
    {
        const ulong FinalizedEpoch = 9;
        const ulong FinalizedSlot = FinalizedEpoch * Presets.SlotsPerEpoch;
        UnsignedChain chain = UnsignedChain.Create();
        ForkChoiceRunner runner = new(chain.Spec, chain.Anchor.AnchorState, chain.Anchor.AnchorBlock.Message!, chain, chain.Anchor.Pubkeys);
        TickToSlot(runner, FinalizedSlot + 2);

        Hash256 parentRoot = chain.AnchorRoot;
        for (ulong slot = 1; slot < FinalizedSlot; slot++)
        {
            UnsignedChain.ChainBlock main = chain.Extend(parentRoot, slot, payloadHashByte: (byte)(slot % 0xe0 + 1));
            runner.OnBlock(main.Block, main.PostState, ExecutionStatus.Valid, (IReadOnlyList<DataColumnSidecar>?)null);
            parentRoot = main.Root;
        }

        UnsignedChain.ChainBlock fork = chain.Extend(parentRoot, FinalizedSlot + 1, payloadHashByte: 0xf1);
        runner.OnBlock(fork.Block, fork.PostState, ExecutionStatus.Valid, (IReadOnlyList<DataColumnSidecar>?)null);
        UnsignedChain.ChainBlock atFinalizedSlot = chain.Extend(parentRoot, FinalizedSlot, payloadHashByte: 0xf5);
        runner.OnBlock(atFinalizedSlot.Block, atFinalizedSlot.PostState, ExecutionStatus.Valid, (IReadOnlyList<DataColumnSidecar>?)null);
        UnsignedChain.ChainBlock finalized = chain.Extend(parentRoot, FinalizedSlot, payloadHashByte: 0xf2);
        runner.OnBlock(finalized.Block, finalized.PostState, ExecutionStatus.Valid, (IReadOnlyList<DataColumnSidecar>?)null);

        UnsignedChain.ChainBlock finalizing = chain.Extend(finalized.Root, FinalizedSlot + 2, payloadHashByte: 0xf3);
        BeaconStateFulu doctored = finalizing.PostState.Clone();
        doctored.CurrentJustifiedCheckpoint = new Checkpoint { Epoch = FinalizedEpoch, Root = finalized.Root };
        doctored.FinalizedCheckpoint = new Checkpoint { Epoch = FinalizedEpoch, Root = finalized.Root };
        runner.OnBlock(finalizing.Block, doctored, ExecutionStatus.Valid, (IReadOnlyList<DataColumnSidecar>?)null);
        Assert.That(runner.FinalizedCheckpoint, Is.EqualTo(new CheckpointRef(FinalizedEpoch, finalized.Root)), "fixture bug");

        UnsignedChain.ChainBlock sameProposal = chain.Extend(finalized.Root, FinalizedSlot + 1, payloadHashByte: 0xf4);
        runner.OnBlock(sameProposal.Block, sameProposal.PostState, ExecutionStatus.Valid, (IReadOnlyList<DataColumnSidecar>?)null);
        runner.Prune();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(runner.ContainsBlock(fork.Root), Is.False, "fixture bug: the proto-array must have pruned the fork block");
            Assert.That(fork.Block.Message!.ProposerIndex, Is.EqualTo(sameProposal.Block.Message!.ProposerIndex), "fixture bug");
            Assert.That(atFinalizedSlot.Block.Message!.ProposerIndex, Is.EqualTo(finalized.Block.Message!.ProposerIndex), "fixture bug");
            Assert.That(runner.IsProposerEquivocation(sameProposal.Root), Is.True, "slot 289 is after the finalized slot");
            Assert.That(runner.IsProposerEquivocation(finalized.Root), Is.False, "the rival at the finalized slot 288 is dropped");
            Assert.That(runner.IsProposerEquivocation(finalizing.Root), Is.False, "a unique proposal is no equivocation");
        }
    }

    /// <summary>
    /// Votes are verified against the pubkey cache, so the runner must extend it from every registry it
    /// validates against: a Gloas block's post-state, and a checkpoint state that epoch transitions produced.
    /// The cache starts empty, as it lags a registry that deposits grew, and slot 32's committee signs for
    /// real; the target is the first Gloas block itself (no advance), or the anchor advanced across the fork.
    /// </summary>
    [Test]
    public void Signed_vote_verifies_with_keys_first_seen_in_the_registry_it_is_checked_against([Values] bool targetFirstGloasBlock)
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        PubkeyCache pubkeys = new();
        (ForkChoiceRunner runner, Hash256 target, BeaconStateGloas signingState) = RunnerForBoundaryVote(chain, targetFirstGloasBlock, pubkeys);

        runner.OnAttestation(SignedBoundaryVote(signingState, EpochOneVote(chain, GloasTestFixtures.BoundarySlot, target)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pubkeys.Count, Is.EqualTo(GloasTestFixtures.ValidatorCount));
            Assert.That(Weight(runner, target), Is.EqualTo(CommitteeSize * EffectiveBalance));
        }
    }

    /// <summary>
    /// <see cref="ForkCrossingChain"/>'s first two Gloas blocks imported at slot 66, then the slot-65 block with
    /// its post-state doctored to name three different checkpoints: the anchor as previous justified, the
    /// slot-64 block as current justified at <see cref="DoctoredJustifiedEpoch"/>, and the first Gloas block as finalized.
    /// </summary>
    private static ForkChoiceRunner FinalizedOnFirstGloasBlock(ForkCrossingChain chain)
    {
        ForkChoiceRunner runner = chain.CreateRunner();
        TickToSlot(runner, 2 * Presets.SlotsPerEpoch + 2);
        runner.OnBlock(chain.First.Block, chain.First.PostState);
        runner.OnBlock(chain.Voting[0].Block, chain.Voting[0].PostState);

        BeaconStateGloas doctored = chain.Voting[1].PostState.Clone();
        doctored.PreviousJustifiedCheckpoint = new Checkpoint { Epoch = 0, Root = chain.AnchorRoot };
        doctored.CurrentJustifiedCheckpoint = new Checkpoint { Epoch = DoctoredJustifiedEpoch, Root = chain.Voting[0].Root };
        doctored.FinalizedCheckpoint = new Checkpoint { Epoch = ForkCrossingChain.ForkEpoch, Root = chain.First.Root };
        runner.OnBlock(chain.Voting[1].Block, doctored);
        return runner;
    }

    /// <summary><see cref="ForkCrossingChain"/> fully imported with its body votes and ticked into epoch 3, where the justified checkpoint is the first Gloas block.</summary>
    private static ForkChoiceRunner JustifiedOnFirstGloasBlock(ForkCrossingChain chain)
    {
        ForkChoiceRunner runner = chain.CreateRunner();
        TickToSlot(runner, 2 * Presets.SlotsPerEpoch + 2);
        ImportGloas(runner, chain.First, replayInFuluContainer: false);
        foreach (ForkCrossingChain.ChainBlock block in chain.Voting)
        {
            ImportGloas(runner, block, replayInFuluContainer: false);
        }

        TickToSlot(runner, 3 * Presets.SlotsPerEpoch);
        return runner;
    }

    /// <summary>A runner at slot 2 holding an execution-invalidated slot-1 block, and the import of its slot-2 child with a post-state doctored to justify and finalize epoch 1.</summary>
    private static (ForkChoiceRunner Runner, Action ImportChild, ulong DoctoredEpoch) TimelyChildOfInvalidFuluParent()
    {
        const ulong DoctoredEpoch = 1;
        UnsignedChain chain = UnsignedChain.Create();
        ForkChoiceRunner runner = new(chain.Spec, chain.Anchor.AnchorState, chain.Anchor.AnchorBlock.Message!, chain, chain.Anchor.Pubkeys);
        TickToSlot(runner, 2);
        UnsignedChain.ChainBlock parent = chain.Extend(chain.AnchorRoot, slot: 1, payloadHashByte: 0xc1);
        runner.OnBlock(parent.Block, parent.PostState, ExecutionStatus.Optimistic, (IReadOnlyList<DataColumnSidecar>?)null);
        runner.OnInvalidExecutionPayload(parent.Root);

        UnsignedChain.ChainBlock child = chain.Extend(parent.Root, slot: 2, payloadHashByte: 0xc2);
        BeaconStateFulu doctored = child.PostState.Clone();
        doctored.CurrentJustifiedCheckpoint = new Checkpoint { Epoch = DoctoredEpoch, Root = parent.Root };
        doctored.FinalizedCheckpoint = new Checkpoint { Epoch = DoctoredEpoch, Root = parent.Root };
        return (runner, () => runner.OnBlock(child.Block, doctored, ExecutionStatus.Optimistic, (IReadOnlyList<DataColumnSidecar>?)null), DoctoredEpoch);
    }

    /// <summary>A runner at slot 64 holding the execution-invalidated first Gloas block, and the import of its slot-64 child with a post-state doctored to justify and finalize the fork epoch.</summary>
    private static (ForkChoiceRunner Runner, Action ImportChild, ulong DoctoredEpoch) TimelyChildOfInvalidGloasParent()
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = chain.CreateRunner();
        TickToSlot(runner, 2 * Presets.SlotsPerEpoch);
        runner.OnBlock(chain.First.Block, chain.First.PostState);
        runner.OnInvalidExecutionPayload(chain.First.Root);

        ForkCrossingChain.ChainBlock child = chain.Voting[0];
        BeaconStateGloas doctored = child.PostState.Clone();
        doctored.CurrentJustifiedCheckpoint = new Checkpoint { Epoch = ForkCrossingChain.ForkEpoch, Root = chain.First.Root };
        doctored.FinalizedCheckpoint = new Checkpoint { Epoch = ForkCrossingChain.ForkEpoch, Root = chain.First.Root };
        Assert.That(child.Block.Message!.Slot, Is.EqualTo(runner.CurrentSlot), "fixture bug: the child must be timely");
        return (runner, () => runner.OnBlock(child.Block, doctored), ForkCrossingChain.ForkEpoch);
    }

    /// <summary>The Gloas caller-side contract: the block, then its body votes with signatures already trusted.</summary>
    private static void ImportGloas(ForkChoiceRunner runner, ForkCrossingChain.ChainBlock block, bool replayInFuluContainer)
    {
        runner.OnBlock(block.Block, block.PostState);
        foreach (AttestationGloas attestation in block.Block.Message!.Body!.Attestations!)
        {
            if (replayInFuluContainer)
                runner.OnAttestation(GloasTestFixtures.ToFuluAttestation(attestation), isFromBlock: true, verifySignature: false);
            else
                runner.OnAttestation(attestation, isFromBlock: true, verifySignature: false);
        }
    }

    /// <summary>
    /// A runner at slot 33 whose epoch-1 target is the first Gloas block, imported, or else the anchor, with
    /// the Gloas state its committee signs under: that block's post-state, or the anchor upgraded by hand.
    /// </summary>
    private static (ForkChoiceRunner Runner, Hash256 Target, BeaconStateGloas SigningState) RunnerForBoundaryVote(
        ForkCrossingChain chain, bool targetFirstGloasBlock, PubkeyCache? pubkeys)
    {
        ForkChoiceRunner runner = chain.CreateRunner(pubkeys: pubkeys);
        TickToSlot(runner, GloasTestFixtures.BoundarySlot + 1);
        if (!targetFirstGloasBlock)
            return (runner, chain.AnchorRoot, UpgradedAnchor(chain));

        runner.OnBlock(chain.First.Block, chain.First.PostState);
        return (runner, chain.First.Root, chain.First.PostState);
    }

    /// <summary>A vote at <paramref name="slot"/> of epoch 1 for <paramref name="target"/> as both head and target, sourced from the anchor.</summary>
    private static AttestationData EpochOneVote(ForkCrossingChain chain, ulong slot, Hash256 target) => new()
    {
        Slot = slot,
        Index = 0,
        BeaconBlockRoot = target,
        Source = new Checkpoint { Epoch = 0, Root = chain.AnchorRoot },
        Target = new Checkpoint { Epoch = ForkCrossingChain.ForkEpoch, Root = target },
    };

    /// <summary>The whole committee of <c>data.Slot</c> in <paramref name="signingState"/>, aggregate-signed with real keys.</summary>
    private static AttestationGloas SignedBoundaryVote(BeaconStateGloas signingState, AttestationData data) =>
        GloasTestFixtures.CommitteeAttestation(
            signingState, data, new EpochCache().GetCommitteeCache(signingState, ForkCrossingChain.ForkEpoch), 0, sign: true);

    /// <summary>An indexed attestation by <paramref name="signers"/> over <paramref name="data"/>, aggregate-signed under <paramref name="domain"/>.</summary>
    private static IndexedAttestationGloas SignedUnder(Hash256 domain, AttestationData data, ulong[] signers) => new()
    {
        AttestingIndices = signers,
        Data = data,
        Signature = GloasTestFixtures.AggregateSignature(Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(data), domain), [.. signers.Select(static i => (int)i)]),
    };

    private static IndexedAttestation ToFuluIndexed(IndexedAttestationGloas attestation) =>
        new() { AttestingIndices = attestation.AttestingIndices, Data = attestation.Data, Signature = attestation.Signature };

    /// <summary>The anchor state taken across the fork by hand: Fulu slot processing to the boundary, then the upgrade.</summary>
    private static BeaconStateGloas UpgradedAnchor(ForkCrossingChain chain)
    {
        BeaconStateFulu fulu = chain.AnchorState.Clone();
        SlotProcessing.ProcessSlots(fulu, GloasTestFixtures.BoundarySlot, new EpochCache());
        return GloasForkTransition.UpgradeToGloas(fulu, chain.Spec);
    }

    private static Hash256 BlockStateRoot(ForkCrossingChain chain, bool gloasRoot) =>
        gloasRoot ? SszRoots.HashTreeRoot(chain.First.PostState) : SszRoots.HashTreeRoot(chain.AnchorState);

    private static void TickToSlot(ForkChoiceRunner runner, ulong slot) =>
        runner.OnTick(runner.GenesisTime + slot * Presets.SecondsPerSlot);

    /// <summary>The weight of <paramref name="root"/> as a fresh <see cref="ForkChoiceRunner.GetHead"/> computes it.</summary>
    private static ulong Weight(ForkChoiceRunner runner, Hash256 root)
    {
        runner.GetHead();
        return runner.Snapshot().Nodes.Single(n => n.Root == root).Weight;
    }

    /// <summary>
    /// <see cref="UnsignedChain.BuildEquivocation"/> with everything up to the slashing block imported
    /// and replayed. The runner is ticked past every block so no proposer boost confounds the weights.
    /// </summary>
    private static (ForkChoiceRunner Runner, UnsignedChain.ChainBlock Voted, UnsignedChain.ChainBlock B, UnsignedChain.ChainBlock Slashing) EquivocationScenario()
    {
        UnsignedChain chain = UnsignedChain.Create();
        ForkChoiceRunner runner = new(chain.Spec, chain.Anchor.AnchorState, chain.Anchor.AnchorBlock.Message!, chain, chain.Anchor.Pubkeys);
        runner.OnTick(runner.GenesisTime + 8 * chain.Spec.SecondsPerSlot);
        UnsignedChain.Equivocation scenario = chain.BuildEquivocation();

        ImportWithBodyReplay(runner, scenario.A);
        ImportWithBodyReplay(runner, scenario.B);
        ImportWithBodyReplay(runner, scenario.Voted);
        return (runner, scenario.Voted, scenario.B, scenario.Slashing);
    }

    /// <summary>The caller-side contract of <see cref="ForkChoiceRunner.OnBlock(SignedBeaconBlock, BeaconStateFulu, ExecutionStatus, IDataAvailabilityRule)"/>: the block, then its body operations with signatures already trusted.</summary>
    private static void ImportWithBodyReplay(ForkChoiceRunner runner, UnsignedChain.ChainBlock block)
    {
        runner.OnBlock(block.Block, block.PostState, ExecutionStatus.Valid, (IReadOnlyList<DataColumnSidecar>?)null);
        BeaconBlockBody body = block.Block.Message!.Body!;
        foreach (Attestation attestation in body.Attestations!)
        {
            runner.OnAttestation(attestation, isFromBlock: true, verifySignature: false);
        }

        foreach (AttesterSlashing slashing in body.AttesterSlashings!)
        {
            runner.OnAttesterSlashing(slashing, verifySignatures: false);
        }
    }

    /// <summary>A runner rooted at the fixture's anchor, ticked to the block's slot, with the block's real post-state computed.</summary>
    /// <param name="anchorBlockState">What the state provider serves for the anchor root, when not the anchor state itself.</param>
    private static (ForkChoiceRunner Runner, BeaconStateFulu PostState) RunnerAt(ImportableBlobBlock chain, BeaconStateFulu? anchorBlockState = null)
    {
        InMemoryStates states = new();
        states.States[chain.AnchorRoot] = anchorBlockState ?? chain.AnchorState;
        ForkChoiceRunner runner = new(chain.Spec, chain.AnchorState, chain.AnchorBlock.Message!, states, chain.Pubkeys);
        runner.OnTick(runner.GenesisTime + chain.Block.Message!.Slot * chain.Spec.SecondsPerSlot);

        BeaconStateFulu postState = chain.AnchorState.Clone();
        FuluStateTransition.Apply(postState, chain.Block, new EpochCache(), chain.Pubkeys, new AcceptingNotifier(), chain.Spec);
        return (runner, postState);
    }

    private sealed class RecordingRule(bool verdict) : IDataAvailabilityRule
    {
        public List<(BeaconBlock Block, Hash256 Root)> Asked { get; } = [];

        public bool IsDataAvailable(BeaconBlock block, Hash256 blockRoot, BeaconChainSpec spec)
        {
            Asked.Add((block, blockRoot));
            return verdict;
        }
    }

    private sealed class InMemoryStates : IForkChoiceStateProvider
    {
        public Dictionary<Hash256, BeaconStateFulu> States { get; } = [];

        public BeaconStateFulu? GetBlockState(Hash256 blockRoot) => States.GetValueOrDefault(blockRoot);

        public BeaconStateFulu? CopyBlockState(Hash256 blockRoot) => GetBlockState(blockRoot)?.Clone();
    }

    /// <summary>Serves the chain's block states, but every copy finds the state already evicted.</summary>
    private sealed class EvictedBeforeCopy(UnsignedChain chain) : IForkChoiceStateProvider
    {
        public BeaconStateFulu? GetBlockState(Hash256 blockRoot) => chain.GetBlockState(blockRoot);

        public BeaconStateFulu? CopyBlockState(Hash256 blockRoot) => null;
    }

    private sealed class AcceptingNotifier : INewPayloadNotifier
    {
        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body) => ExecutionStatus.Valid;
    }

    /// <summary>Imports a line of blocks at <paramref name="slots"/>, each on the previous one and the first on <paramref name="parent"/>.</summary>
    private static List<UnsignedChain.ChainBlock> ImportLine(UnsignedChain chain, ForkChoiceRunner runner, Hash256 parent, params ulong[] slots)
    {
        List<UnsignedChain.ChainBlock> blocks = [];
        foreach (ulong slot in slots)
        {
            UnsignedChain.ChainBlock block = chain.Extend(parent, slot, payloadHashByte: (byte)slot);
            ImportWithBodyReplay(runner, block);
            blocks.Add(block);
            parent = block.Root;
        }

        return blocks;
    }

    /// <summary>The first slot of <paramref name="epoch"/> past <paramref name="skip"/> others with a committee on <paramref name="tip"/>'s chain, its sole member, and the state that signs for it.</summary>
    private static (BeaconStateFulu SigningState, ulong Slot, int Member) FirstCommitteeMember(UnsignedChain.ChainBlock tip, ulong epoch, int skip = 0)
    {
        BeaconStateFulu atEpochStart = tip.PostState.Clone();
        ulong epochStart = epoch * Presets.SlotsPerEpoch;
        if (atEpochStart.Slot < epochStart)
            SlotProcessing.ProcessSlots(atEpochStart, epochStart, new EpochCache { Hasher = new CachedBeaconStateHasher() });
        CommitteeCache committees = new EpochCache().GetCommitteeCache(atEpochStart, epoch);
        ulong slot = epochStart;
        while (committees.GetBeaconCommittee(slot, 0).Length == 0 || skip-- > 0)
            slot++;
        ReadOnlySpan<int> committee = committees.GetBeaconCommittee(slot, 0);
        Assert.That(committee.Length, Is.EqualTo(1), "fixture bug: the aggregation bits assume one-member committees");
        return (atEpochStart, slot, committee[0]);
    }

    /// <summary>The unsigned vote of <paramref name="target"/>'s first committee of <paramref name="epoch"/> for that block as head and target.</summary>
    private static Attestation BodyVote(UnsignedChain chain, UnsignedChain.ChainBlock target, ulong epoch, int skip = 0)
    {
        (_, ulong slot, _) = FirstCommitteeMember(target, epoch, skip);
        return new Attestation
        {
            AggregationBits = new BitArray(1, true),
            Data = VoteData(chain, slot, target.Root, epoch),
            Signature = new BlsSignature(SignatureSets.G2PointAtInfinity),
            CommitteeBits = new BitArray(Presets.MaxCommitteesPerSlot) { [0] = true },
        };
    }

    /// <summary>An aggregate by <paramref name="member"/> for <paramref name="head"/> as head and target, signed under <paramref name="signingState"/>, or unsigned without one.</summary>
    private static SignedAggregateAndProof GossipAggregate(UnsignedChain chain, ulong slot, Hash256 head, ulong epoch, int member, BeaconStateFulu? signingState)
    {
        BlsSignature unsigned = new(SignatureSets.G2PointAtInfinity);
        AttestationData data = VoteData(chain, slot, head, epoch);
        byte[] slotRoot = new byte[32];
        BitConverter.TryWriteBytes(slotRoot, slot);
        AggregateAndProof message = new()
        {
            AggregatorIndex = (ulong)member,
            Aggregate = new Attestation
            {
                AggregationBits = new BitArray(1, true),
                Data = data,
                Signature = signingState is null ? unsigned : SignAs(SszRoots.HashTreeRoot(data), DomainType.BeaconAttester),
                CommitteeBits = new BitArray(Presets.MaxCommitteesPerSlot) { [0] = true },
            },
            SelectionProof = signingState is null ? unsigned : SignAs(new Hash256(slotRoot), DomainType.SelectionProof),
        };
        return new SignedAggregateAndProof { Message = message, Signature = signingState is null ? unsigned : SignAs(SszRoots.HashTreeRoot(message), DomainType.AggregateAndProof) };

        BlsSignature SignAs(Hash256 root, ReadOnlySpan<byte> domainType) =>
            ImportableBlobBlock.SignAs((ulong)member, root, signingState!.GetDomain(domainType, epoch));
    }

    private static AttestationData VoteData(UnsignedChain chain, ulong slot, Hash256 head, ulong epoch) => new()
    {
        Slot = slot,
        Index = 0,
        BeaconBlockRoot = head,
        Source = chain.Anchor.AnchorState.CurrentJustifiedCheckpoint,
        Target = new Checkpoint { Epoch = epoch, Root = head },
    };

    /// <summary>Counts the checkpoint states the runner advances, one hasher each.</summary>
    private sealed class BuildCounter
    {
        public BuildCounter(ForkChoiceRunner runner) =>
            runner.CheckpointStateHasher = () =>
            {
                Count++;
                return new CachedBeaconStateHasher();
            };

        public int Count { get; private set; }
    }

    private sealed class CountingHasher(IBeaconStateHasher inner) : IBeaconStateHasher
    {
        public int Calls { get; private set; }

        public Hash256 HashTreeRoot(BeaconStateFulu state)
        {
            Calls++;
            return inner.HashTreeRoot(state);
        }

        public Hash256 HashTreeRoot(BeaconStateGloas state)
        {
            Calls++;
            return inner.HashTreeRoot(state);
        }
    }
}
