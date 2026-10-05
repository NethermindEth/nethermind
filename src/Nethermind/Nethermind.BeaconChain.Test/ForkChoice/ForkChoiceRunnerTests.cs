// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Hashing;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Test.Engine;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Test.StateTransition;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using FuluStateTransition = Nethermind.BeaconChain.StateTransition.StateTransition;

namespace Nethermind.BeaconChain.Test.ForkChoice;

[HardTimeout(60_000)]
public class ForkChoiceRunnerTests
{
    private const ulong EffectiveBalance = 32 * GloasTestFixtures.Gwei;

    private const ulong CommitteeSize = 64;

    private const int MultiMemberValidatorCount = 512;

    private const int MaxOtherShufflingBodyBuilds = 2;

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

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(() => runner.OnBlock(chain.Block, postState, ExecutionStatus.Valid, eightColumns),
            Throws.TypeOf<ForkChoiceException>().With.Message.Contains("blob data"));
        Assert.That(runner.ContainsBlock(chain.BlockRoot), Is.False, "a rejected block must not have been added to the tree");
        Assert.That(() => runner.OnBlock(chain.Block, postState, ExecutionStatus.Valid, chain.Columns), Throws.Nothing,
            "the spec vectors hand over the whole matrix and expect acceptance");
        Assert.That(runner.ContainsBlock(chain.BlockRoot), Is.True);
    }

    [Test]
    public void OnBlock_with_a_rule_asks_that_rule_about_this_block_and_honors_its_verdict()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        (ForkChoiceRunner runner, BeaconStateFulu postState) = RunnerAt(chain);
        RecordingRule refusing = new(verdict: false);
        RecordingRule accepting = new(verdict: true);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(() => runner.OnBlock(chain.Block, postState, ExecutionStatus.Valid, refusing), Throws.TypeOf<ForkChoiceException>());
        Assert.That(refusing.Asked, Is.EqualTo(new List<(BeaconBlock, Hash256)> { (chain.Block.Message!, chain.BlockRoot) }),
            "the rule is consulted for this block, keyed by its real root");
        Assert.That(runner.ContainsBlock(chain.BlockRoot), Is.False);

        Assert.That(() => runner.OnBlock(chain.Block, postState, ExecutionStatus.Valid, accepting), Throws.Nothing);
        Assert.That(accepting.Asked, Has.Count.EqualTo(1));
        Assert.That(runner.ContainsBlock(chain.BlockRoot), Is.True);
    }

    [Test]
    public void Snapshot_copies_the_store_and_later_blocks_do_not_change_an_earlier_copy()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        (ForkChoiceRunner runner, BeaconStateFulu postState) = RunnerAt(chain);
        ForkChoiceSnapshot anchorOnly = runner.Snapshot();

        runner.OnBlock(chain.Block, postState, ExecutionStatus.Optimistic, chain.Columns);
        ForkChoiceSnapshot withBlock = runner.Snapshot();

        using IDisposable assertionScope = Assert.EnterMultipleScope();
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
    }

    [Test]
    public void Body_attester_slashing_replayed_after_OnBlock_discounts_the_equivocating_votes()
    {
        (ForkChoiceRunner runner, UnsignedChain.ChainBlock voted, UnsignedChain.ChainBlock b, UnsignedChain.ChainBlock slashing) = EquivocationScenario();
        Hash256 headBeforeSlashing = runner.GetHead();

        ImportWithBodyReplay(runner, slashing);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(headBeforeSlashing, Is.EqualTo(voted.Root), "two votes on A's branch outweigh one on B");
        Assert.That(runner.GetHead(), Is.EqualTo(b.Root), "the slashed validators' votes no longer count, so B's lone vote wins");
    }

    [Test]
    public void Body_attester_slashing_replay_with_verification_on_is_refused_whole()
    {
        (ForkChoiceRunner runner, _, _, UnsignedChain.ChainBlock slashing) = EquivocationScenario();
        runner.OnBlock(slashing.Block, slashing.PostState, ExecutionStatus.Valid, (IReadOnlyList<DataColumnSidecar>?)null);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(() => runner.OnAttesterSlashing(slashing.Block.Message!.Body!.AttesterSlashings![0], verifySignatures: true),
            Throws.TypeOf<ForkChoiceException>().With.Message.Contains("invalid"));
        Assert.That(runner.GetHead(), Is.EqualTo(slashing.Root), "a refused slashing discounts nobody: the head stays on A's branch, at its new leaf");
    }

    [Test]
    public void Justified_checkpoint_on_a_gloas_block_is_weighed_from_its_gloas_state([Values] bool replayInFuluContainer)
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = TickToSlot(chain.CreateRunner(), 2 * Presets.SlotsPerEpoch + 2);

        ImportGloas(runner, chain.First, replayInFuluContainer);
        Hash256 headAtFork = runner.GetHead();
        foreach (ForkCrossingChain.ChainBlock block in chain.Voting)
        {
            ImportGloas(runner, block, replayInFuluContainer);
        }

        CheckpointRef justifiedBeforePullUp = runner.JustifiedCheckpoint;
        TickToSlot(runner, 3 * Presets.SlotsPerEpoch);
        Hash256 head = runner.GetHead();

        using IDisposable assertionScope = Assert.EnterMultipleScope();
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

    /// <summary>Build the checkpoint oracle by composing Fulu slots, upgrade and Gloas slots independently of the runner.</summary>
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
        BeaconStateGloas expected = gloasRoot ? chain.First.PostState.Clone() : chain.UpgradedAnchor();
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
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(actual.Slot, Is.EqualTo(startSlot));
        Assert.That(SszRoots.HashTreeRoot(actual), Is.EqualTo(SszRoots.HashTreeRoot(expected)));
        Assert.That(SszRoots.HashTreeRoot(actual), Is.EqualTo(SszRoots.HashTreeRoot(transitioned)), "the state transition's own crossing lands on the same state");
        Assert.That(BlockStateRoot(chain, gloasRoot), Is.EqualTo(blockStateRoot), "the block state the providers froze must not be advanced in place");
    }

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
        List<CountingHasher> made = CountCheckpointHashes(runner);
        Hash256 blockStateRoot = SszRoots.HashTreeRoot(blockState);
        CheckpointRef checkpoint = new(1, blockRoot);
        BeaconStateFulu expected = blockState.Clone();
        SlotProcessing.ProcessSlots(expected, Presets.SlotsPerEpoch, new EpochCache());

        ForkedBeaconState first = runner.GetCheckpointState(checkpoint);
        ForkedBeaconState repeated = runner.GetCheckpointState(checkpoint);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(defaultHasher, Is.TypeOf<CachedBeaconStateHasher>(), "production advances must hash incrementally");
        Assert.That(made, Has.Count.EqualTo(1), "one hasher per advance, and none for the cached repeat");
        Assert.That(made.Single().Calls, Is.EqualTo(distance - 1), "every skipped slot's state root but the block's own comes from that hasher");
        Assert.That(repeated, Is.SameAs(first));
        Assert.That(SszRoots.HashTreeRoot(((ForkedBeaconState.OfFulu)first).State), Is.EqualTo(SszRoots.HashTreeRoot(expected)));
        Assert.That(SszRoots.HashTreeRoot(blockState), Is.EqualTo(blockStateRoot), "the block state must not be advanced in place");
    }

    [Test]
    public void Checkpoint_advance_takes_its_slot_roots_from_a_later_block_of_the_same_chain([Values(2, 32)] int distance, [Values] bool blockInsideTheAdvance)
    {
        (UnsignedChain chain, ForkChoiceRunner runner) = CreateRunner();
        TickToSlot(runner, Presets.SlotsPerEpoch + 2);
        ulong checkpointSlot = Presets.SlotsPerEpoch - (ulong)distance;
        UnsignedChain.ChainBlock? checkpointBlock = checkpointSlot == 0 ? null : ImportLine(chain, runner, chain.AnchorRoot, checkpointSlot)[0];
        Hash256 checkpointRoot = checkpointBlock?.Root ?? chain.AnchorRoot;
        ImportLine(chain, runner, checkpointRoot, blockInsideTheAdvance ? [Presets.SlotsPerEpoch - 1, Presets.SlotsPerEpoch + 1] : [Presets.SlotsPerEpoch + 1]);
        List<CountingHasher> made = CountCheckpointHashes(runner);
        BeaconStateFulu expected = (checkpointBlock?.PostState ?? chain.Anchor.AnchorState).Clone();
        SlotProcessing.ProcessSlots(expected, Presets.SlotsPerEpoch, new EpochCache());

        ForkedBeaconState state = runner.GetCheckpointState(new CheckpointRef(1, checkpointRoot));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(made.Single().Calls, Is.EqualTo(blockInsideTheAdvance ? distance - 1 : 0), "only an advance the later block went through is answered from its roots");
        Assert.That(SszRoots.HashTreeRoot(((ForkedBeaconState.OfFulu)state).State), Is.EqualTo(SszRoots.HashTreeRoot(expected)));
    }

    [Test]
    public void Body_votes_for_an_older_target_of_the_blocks_shuffling_count_as_a_full_build_counts_them_without_one()
    {
        const ulong targetEpoch = 3;
        const int votedSlots = 8;
        UnsignedChain chain = UnsignedChain.Create(ImportableBlobBlock.Create(blobCount: 0, validatorCount: MultiMemberValidatorCount));
        ulong epochStart = targetEpoch * Presets.SlotsPerEpoch;
        UnsignedChain.ChainBlock first = chain.Extend(chain.AnchorRoot, 40, payloadHashByte: 40);
        UnsignedChain.ChainBlock target = chain.Extend(first.Root, epochStart - 6, payloadHashByte: 90);
        BeaconStateFulu checkpointState = target.PostState.Clone();
        SlotProcessing.ProcessSlots(checkpointState, epochStart, new EpochCache());
        CommitteeCache fullBuild = new EpochCache().GetCommitteeCache(checkpointState, targetEpoch);
        Attestation[] votes = [.. Enumerable.Range(0, votedSlots).Select(i => WholeCommitteeVote(chain, epochStart + (ulong)i, target.Root, targetEpoch, fullBuild))];
        UnsignedChain.ChainBlock including = chain.Extend(target.Root, epochStart + Presets.SlotsPerEpoch + 2, payloadHashByte: 130, attestations: votes);
        CommitteeCache fromIncludingBlock = new EpochCache().GetCommitteeCache(including.PostState, targetEpoch);

        (ForkChoiceRunner viaBlock, BuildCounter viaBlockBuilds) = Import(new EvictedBeforeCopy(chain), asBodyOfTheBlock: true);
        (ForkChoiceRunner viaSpec, BuildCounter viaSpecBuilds) = Import(chain, asBodyOfTheBlock: false);

        using (Assert.EnterMultipleScope())
        {
            for (ulong slot = epochStart; slot < epochStart + Presets.SlotsPerEpoch; slot++)
                Assert.That(fromIncludingBlock.GetBeaconCommittee(slot, 0).ToArray(), Is.EqualTo(fullBuild.GetBeaconCommittee(slot, 0).ToArray()), $"committee of slot {slot}");
            Assert.That(viaSpecBuilds.Count, Is.EqualTo(1), "fixture bug: the spec path must build the target's checkpoint state");
            Assert.That(viaBlockBuilds.Count, Is.Zero, "a vote of the block's own shuffling builds no checkpoint state");
            Assert.That(Weights(viaBlock), Is.EqualTo(Weights(viaSpec)), "both paths count the same validators");
            Assert.That(Weight(viaBlock, target.Root), Is.EqualTo(votedSlots * (MultiMemberValidatorCount / Presets.SlotsPerEpoch) * EffectiveBalance));
            Assert.That(() => viaBlock.OnAttestation(votes[0], isFromBlock: true, verifySignature: false), Throws.TypeOf<ForkChoiceException>(),
                "the block state is held as neither the target's checkpoint state nor a vote state, so the spec's path still has to build one");
        }

        (ForkChoiceRunner Runner, BuildCounter Builds) Import(IForkChoiceStateProvider states, bool asBodyOfTheBlock)
        {
            ForkChoiceRunner runner = new(chain.Spec, chain.Anchor.AnchorState, chain.Anchor.AnchorBlock.Message!, states, chain.Anchor.Pubkeys);
            BuildCounter builds = new(runner);
            TickToSlot(runner, including.Block.Message!.Slot + 1);
            foreach (UnsignedChain.ChainBlock block in new[] { first, target, including })
                ImportWithBodyReplay(runner, block, asBodyOfTheBlock);
            return (runner, builds);
        }
    }

    [Test]
    public void Body_vote_reads_the_last_block_state_only_while_it_is_that_blocks_own([Values] bool advancedInPlace)
    {
        const ulong targetEpoch = 2;
        (UnsignedChain chain, ForkChoiceRunner runner, BuildCounter builds) = CountedRunnerAt((targetEpoch + 1) * Presets.SlotsPerEpoch);
        UnsignedChain.ChainBlock target = ImportLine(chain, runner, chain.AnchorRoot, 40)[0];
        UnsignedChain.ChainBlock including = ImportLine(chain, runner, target.Root, 70)[0];
        if (advancedInPlace)
            SlotProcessing.ProcessSlots(including.PostState, (targetEpoch + 1) * Presets.SlotsPerEpoch + 4, new EpochCache());
        else
            ImportWithBodyReplay(runner, chain.Extend(target.Root, 70, payloadHashByte: 0x71));

        runner.OnBodyAttestation(BodyVote(chain, target, targetEpoch), including.Root);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(builds.Count, Is.EqualTo(1), "the target's checkpoint state is built");
        Assert.That(Weight(runner, target.Root), Is.EqualTo(EffectiveBalance), "the vote counts");
    }

    [Test]
    public void Body_votes_for_targets_of_another_shuffling_verify_their_signature_and_build_two_states_an_epoch()
    {
        const ulong targetEpoch = 2;
        ulong now = (targetEpoch + 1) * Presets.SlotsPerEpoch + 4;
        (UnsignedChain chain, ForkChoiceRunner runner, BuildCounter builds) = CountedRunnerAt(now);
        UnsignedChain.ChainBlock parent = ImportLine(chain, runner, chain.AnchorRoot, 30)[0];
        // A at the decision slot 31 decides the epoch-2 shuffling of its chain; the others' chain decides on their parent.
        UnsignedChain.ChainBlock a = ImportLine(chain, runner, parent.Root, 31)[0];
        List<UnsignedChain.ChainBlock> others = ImportLine(chain, runner, parent.Root, 32, 40, 50);
        UnsignedChain.ChainBlock including = ImportLine(chain, runner, a.Root, 65)[0];

        string?[] firstEpoch = [.. others.Select(target => Refusal(target, including))];
        int buildsInOneEpoch = builds.Count;
        UnsignedChain.ChainBlock nextEpoch = ImportLine(chain, runner, including.Root, 97)[0];
        string?[] secondEpoch = [Refusal(others[0], nextEpoch), Refusal(others[1], nextEpoch)];
        string? lateFromFirstEpoch = Refusal(others[2], ImportLine(chain, runner, a.Root, 66)[0]);
        int buildsBeforeTicks = builds.Count;
        List<int> buildsAfterTicks = [];
        for (int tick = 1; tick <= 3; tick++)
        {
            TickToSlot(runner, now + (ulong)tick);
            buildsAfterTicks.Add(builds.Count);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstEpoch[..2], Has.All.Not.Null, "the first two are checked against their target states and fail their signatures");
            Assert.That(firstEpoch[2], Is.Null, "the third waits for a build");
            Assert.That(buildsInOneEpoch, Is.EqualTo(2));
            Assert.That(secondEpoch, Has.All.Not.Null, "a block of the next epoch has builds again");
            Assert.That(lateFromFirstEpoch, Is.Null, "a late block of the earlier epoch does not renew the builds");
            Assert.That(buildsBeforeTicks, Is.EqualTo(4));
            Assert.That(buildsAfterTicks, Is.EqualTo(new[] { 5, 6, 6 }), "the tick builds one waiting target state a slot until none waits");
            Assert.That(others.Select(o => Weight(runner, o.Root)), Has.All.Zero, "no unsigned vote counts");
        }

        string? Refusal(UnsignedChain.ChainBlock target, UnsignedChain.ChainBlock block)
        {
            try
            {
                runner.OnBodyAttestation(BodyVote(chain, target, targetEpoch), block.Root);
                return null;
            }
            catch (ForkChoiceException e)
            {
                return e.Message;
            }
        }
    }

    [Test]
    public void Body_votes_waiting_for_a_build_are_bounded()
    {
        const ulong targetEpoch = 2;
        const int waiting = 64;
        ulong now = (targetEpoch + 1) * Presets.SlotsPerEpoch;
        (UnsignedChain chain, ForkChoiceRunner runner, BuildCounter builds) = CountedRunnerAt(now);
        UnsignedChain.ChainBlock parent = ImportLine(chain, runner, chain.AnchorRoot, 30)[0];
        UnsignedChain.ChainBlock a = ImportLine(chain, runner, parent.Root, 31)[0];
        UnsignedChain.ChainBlock other = ImportLine(chain, runner, parent.Root, 32)[0];
        UnsignedChain.ChainBlock including = ImportLine(chain, runner, a.Root, 65)[0];
        // Unsigned, so no build is cached and every vote costs one.
        Attestation vote = BodyVote(chain, other, targetEpoch);
        StringLabel droppedLabel = new("body_attestation_deferred_dropped");
        long droppedBefore = Metrics.BeaconChainForkChoiceRejections.GetValueOrDefault(droppedLabel);
        for (int i = 0; i < MaxOtherShufflingBodyBuilds + waiting + 8; i++)
        {
            Assert.That(() => runner.OnBodyAttestation(vote, including.Root),
                i < MaxOtherShufflingBodyBuilds ? Throws.TypeOf<ForkChoiceException>() : Throws.Nothing, "a budgeted build refuses the vote at once; later ones wait");
        }

        int waitingBeforeTicks = runner.DeferredBodyVoteCount;
        long dropped = Metrics.BeaconChainForkChoiceRejections.GetValueOrDefault(droppedLabel) - droppedBefore;
        for (ulong tick = 1; tick <= waiting + 8; tick++)
            TickToSlot(runner, now + tick);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(waitingBeforeTicks, Is.EqualTo(waiting));
        Assert.That(dropped, Is.EqualTo(8), "each vote pushed out of the full queue is counted under its own label, apart from refused votes");
        Assert.That(builds.Count, Is.EqualTo(MaxOtherShufflingBodyBuilds + waiting));
        Assert.That(runner.DeferredBodyVoteCount, Is.Zero);
    }

    [Test]
    public void Signed_body_vote_past_the_build_budget_counts_after_the_tick()
    {
        const ulong targetEpoch = 2;
        ulong now = (targetEpoch + 1) * Presets.SlotsPerEpoch;
        (UnsignedChain chain, ForkChoiceRunner runner, BuildCounter builds) = CountedRunnerAt(now);
        UnsignedChain.ChainBlock parent = ImportLine(chain, runner, chain.AnchorRoot, 30)[0];
        UnsignedChain.ChainBlock[] siblings = [.. Enumerable.Range(0, 4).Select(i => ImportWith(parent.Root, 31, (byte)(0xc0 + i)))];
        UnsignedChain.ChainBlock including = ImportLine(chain, runner, siblings[0].Root, 65)[0];

        for (int i = 1; i < siblings.Length; i++)
            runner.OnBodyAttestation(BodyVote(chain, siblings[i], targetEpoch, skip: i, sign: true), including.Root);
        ulong[] weightsBeforeTick = [.. siblings.Skip(1).Select(s => Weight(runner, s.Root))];
        int buildsBeforeTick = builds.Count;
        TickToSlot(runner, now + 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(buildsBeforeTick, Is.EqualTo(2));
            Assert.That(weightsBeforeTick, Is.EqualTo(new[] { EffectiveBalance, EffectiveBalance, 0UL }), "the third vote waits for a build");
            Assert.That(builds.Count, Is.EqualTo(3));
            Assert.That(siblings.Skip(1).Select(s => Weight(runner, s.Root)), Has.All.EqualTo(EffectiveBalance), "every signed vote counts");
        }

        UnsignedChain.ChainBlock ImportWith(Hash256 parentRoot, ulong slot, byte payloadHashByte)
        {
            UnsignedChain.ChainBlock block = chain.Extend(parentRoot, slot, payloadHashByte);
            ImportWithBodyReplay(runner, block);
            return block;
        }
    }

    [Test]
    public void Nonzero_committee_gossip_without_a_held_head_is_ignored_without_loading_states([Values] bool headComputed)
    {
        UnsignedChain chain = UnsignedChain.Create();
        GossipStates states = new(chain.Anchor.AnchorState);
        ForkChoiceRunner runner = new(chain.Spec, chain.Anchor.AnchorState, chain.Anchor.AnchorBlock.Message!, states, chain.Anchor.Pubkeys);
        if (headComputed) runner.GetHead();
        int loadsBefore = states.Loads;
        SignedAggregateAndProof aggregate = GossipAggregate(chain, 0, chain.AnchorRoot, 0, 0, signingState: null);
        aggregate.Message!.Aggregate!.CommitteeBits = new BitArray(Presets.MaxCommitteesPerSlot) { [1] = true };

        ForkChoiceException refusal = Assert.Throws<ForkChoiceException>(() => runner.OnAggregateAndProof(aggregate))!;

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(refusal.RejectGossip, Is.False, "missing local head data cannot convict the relay");
        Assert.That(refusal.Message, Does.Contain(headComputed ? "No held state" : "No cached head"));
        Assert.That(states.Loads, Is.EqualTo(loadsBefore), "gossip must not call a state accessor that can replay blocks");
    }

    [Test]
    public void Payload_gossip_for_an_unheld_fulu_block_does_not_load_states()
    {
        UnsignedChain chain = UnsignedChain.Create();
        GossipStates states = new(chain.Anchor.AnchorState);
        ForkChoiceRunner runner = new(chain.Spec, chain.Anchor.AnchorState, chain.Anchor.AnchorBlock.Message!, states, chain.Anchor.Pubkeys);
        int loadsBefore = states.Loads;
        PayloadAttestationMessage message = new() { Data = new PayloadAttestationData { BeaconBlockRoot = chain.AnchorRoot, Slot = 0 } };

        ForkChoiceException refusal = Assert.Throws<ForkChoiceException>(() => runner.OnPayloadAttestationMessage(message))!;

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(refusal.RejectGossip, Is.False);
        Assert.That(states.Loads, Is.EqualTo(loadsBefore), "a payload vote must not replay a Fulu block before checking its fork");
    }

    [Test]
    public void Nonzero_committee_gossip_reuses_cached_count_without_reading_validators()
    {
        UnsignedChain chain = UnsignedChain.Create();
        BeaconStateFulu headState = chain.Anchor.AnchorState.Clone();
        GossipStates states = new(headState) { Held = true };
        ForkChoiceRunner runner = new(chain.Spec, headState, chain.Anchor.AnchorBlock.Message!, states, chain.Anchor.Pubkeys);
        runner.GetHead();
        SignedAggregateAndProof aggregate = GossipAggregate(chain, 0, chain.AnchorRoot, 0, 0, signingState: null);
        aggregate.Message!.Aggregate!.CommitteeBits = new BitArray(Presets.MaxCommitteesPerSlot) { [1] = true };
        Assert.That(() => runner.OnAggregateAndProof(aggregate), Throws.TypeOf<ForkChoiceException>().With.Message.Contains("out of range"));
        headState.Validators = null;
        int loadsBefore = states.Loads;

        ForkChoiceException refusal = Assert.Throws<ForkChoiceException>(() => runner.OnAggregateAndProof(aggregate))!;

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(refusal.RejectGossip, Is.True, "the cached committee count still proves the index is invalid");
        Assert.That(refusal.Message, Does.Contain("out of range"));
        Assert.That(states.Loads, Is.EqualTo(loadsBefore), "a cached range check must not reload the head");
    }

    [Test]
    public void Gossip_committee_range_does_not_require_a_shuffling_decision_root([Values(5UL, ulong.MaxValue)] ulong targetEpoch, [Values] bool largeRegistry)
    {
        const ulong slot = 96;
        UnsignedChain chain = UnsignedChain.Create();
        InMemoryStates states = new();
        states.States[chain.AnchorRoot] = chain.Anchor.AnchorState;
        ForkChoiceRunner runner = new(chain.Spec, chain.Anchor.AnchorState, chain.Anchor.AnchorBlock.Message!, states, chain.Anchor.Pubkeys);
        TickToSlot(runner, slot);
        UnsignedChain.ChainBlock head = chain.Extend(chain.AnchorRoot, slot, payloadHashByte: 0xA1);
        states.States[head.Root] = head.PostState;
        ImportWithBodyReplay(runner, head);
        Assert.That(runner.GetHead(), Is.EqualTo(head.Root));
        if (largeRegistry)
            head.PostState.Validators = Enumerable.Repeat(head.PostState.Validators![0], 2 * (int)Presets.SlotsPerEpoch * Presets.TargetCommitteeSize).ToArray();
        SignedAggregateAndProof aggregate = GossipAggregate(chain, slot, head.Root, targetEpoch, 0, signingState: null);
        aggregate.Message!.Aggregate!.CommitteeBits = new BitArray(Presets.MaxCommitteesPerSlot) { [1] = true };

        ForkChoiceException refusal = Assert.Throws<ForkChoiceException>(() => runner.OnAggregateAndProof(aggregate))!;

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(refusal.RejectGossip, Is.True, "an unavailable decision root cannot hide a provable gossip violation");
        Assert.That(refusal.Message, Does.Contain(largeRegistry && targetEpoch != ulong.MaxValue ? "does not match its slot" : "out of range"), "committee range precedes the target-epoch check");
    }

    [Test]
    public void Unsigned_aggregates_on_distinct_targets_of_one_shuffling_build_one_state_and_cache_none()
    {
        const ulong targetEpoch = 2;
        ulong epochStart = targetEpoch * Presets.SlotsPerEpoch;
        (UnsignedChain chain, ForkChoiceRunner runner, BuildCounter builds) = CountedRunnerAt(epochStart + 6);
        // All past the decision slot 31, so the anchor fixes the epoch-2 shuffling of every one of them.
        List<UnsignedChain.ChainBlock> targets = ImportLine(chain, runner, chain.AnchorRoot, 33, 41, 50, 60, 63);
        runner.GetHead();
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

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(voteSlot, Is.LessThan(epochStart + 6), "fixture bug: the vote must be from a past slot so it applies at once");
        Assert.That(refused, Is.EqualTo(targets.Count - 1), "every unsigned aggregate is refused");
        Assert.That(buildsAfterFlood, Is.EqualTo(1), "only the first target of the shuffling costs a checkpoint state");
        Assert.That(buildsAfterSignedVote, Is.EqualTo(1), "a signed vote on another target of that shuffling needs no state of its own");
        Assert.That(weightAfter - weightBefore, Is.EqualTo(EffectiveBalance), "the signed aggregate's vote counts");
        Assert.That(builds.Count, Is.EqualTo(2), "the refused aggregate's state was not cached as its target's checkpoint state");
    }

    [Test]
    public void Unsigned_gossip_aggregate_for_another_shuffling_builds_only_the_head_target_state()
    {
        const ulong targetEpoch = 2;
        (UnsignedChain chain, ForkChoiceRunner runner, BuildCounter builds) = CountedRunnerAt(targetEpoch * Presets.SlotsPerEpoch + 6);
        UnsignedChain.ChainBlock parent = ImportLine(chain, runner, chain.AnchorRoot, 30)[0];
        UnsignedChain.ChainBlock a = ImportLine(chain, runner, parent.Root, 31)[0];
        UnsignedChain.ChainBlock b = ImportLine(chain, runner, parent.Root, 32)[0];
        Hash256 head = runner.GetHead();
        (UnsignedChain.ChainBlock headTip, UnsignedChain.ChainBlock other) = head == a.Root ? (a, b) : (b, a);
        (_, ulong voteSlot, int member) = FirstCommitteeMember(headTip, targetEpoch);
        ulong weightBefore = Weight(runner, other.Root);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(head, Is.AnyOf(a.Root, b.Root), "fixture bug: the head must be one of the two branches");
        Assert.That(() => runner.OnAggregateAndProof(GossipAggregate(chain, voteSlot, other.Root, targetEpoch, member, signingState: null)),
            Throws.InstanceOf<Exception>());
        Assert.That(builds.Count, Is.EqualTo(1), "only the head's target state is built for a peer's aggregate");
        Assert.That(Weight(runner, other.Root), Is.EqualTo(weightBefore), "the vote does not count");
    }

    [Test]
    public void Gossip_aggregate_for_a_held_target_of_another_shuffling_is_checked_against_the_head_state()
    {
        const ulong targetEpoch = 2;
        (UnsignedChain chain, ForkChoiceRunner runner, BuildCounter builds) = CountedRunnerAt((targetEpoch + 1) * Presets.SlotsPerEpoch - 1);
        UnsignedChain.ChainBlock parent = ImportLine(chain, runner, chain.AnchorRoot, 30)[0];
        UnsignedChain.ChainBlock a = ImportLine(chain, runner, parent.Root, 31)[0];
        UnsignedChain.ChainBlock b = ImportLine(chain, runner, parent.Root, 32)[0];
        CommitteeCache aCommittees = new EpochCache().GetCommitteeCache(FirstCommitteeMember(a, targetEpoch).SigningState, targetEpoch);
        int bSkip = 0;
        (BeaconStateFulu bState, ulong voteSlot, int member) = FirstCommitteeMember(b, targetEpoch);
        while (!HasOtherSoleMember(aCommittees, voteSlot, member))
            (bState, voteSlot, member) = FirstCommitteeMember(b, targetEpoch, ++bSkip);
        List<int> aSkips = [];
        for (int skip = 0; aSkips.Count < 2; skip++)
        {
            if (FirstCommitteeMember(a, targetEpoch, skip).Member != member)
                aSkips.Add(skip);
        }

        runner.OnAttestation(BodyVote(chain, b, targetEpoch, bSkip), isFromBlock: true, verifySignature: false);
        foreach (int skip in aSkips)
            runner.OnAttestation(BodyVote(chain, a, targetEpoch, skip), isFromBlock: true, verifySignature: false);
        Hash256 head = runner.GetHead();
        int buildsBefore = builds.Count;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(head, Is.EqualTo(a.Root), "fixture bug: A's two votes must make it the head");
            Assert.That(() => runner.OnAggregateAndProof(GossipAggregate(chain, voteSlot, b.Root, targetEpoch, member, bState)),
                Throws.TypeOf<ForkChoiceException>().With.Message.Contains($"Aggregator {member} is not a member of committee 0"));
            Assert.That(builds.Count, Is.EqualTo(buildsBefore), "both the head's and B's target states are held");
        }

        static bool HasOtherSoleMember(CommitteeCache committees, ulong slot, int member)
        {
            ReadOnlySpan<int> committee = committees.GetBeaconCommittee(slot, 0);
            return committee.Length == 1 && committee[0] != member;
        }
    }

    [TestCase(true, false)]
    [TestCase(false, false)]
    [TestCase(false, true)]
    public void Signed_gossip_aggregate_for_an_equivocated_sibling_with_the_same_committees_counts(bool sourceHeld, bool headSource)
    {
        const ulong targetEpoch = 2;
        UnsignedChain chain = UnsignedChain.Create();
        EvictableGossipStates states = new(chain);
        ForkChoiceRunner runner = new(chain.Spec, chain.Anchor.AnchorState, chain.Anchor.AnchorBlock.Message!, states, chain.Anchor.Pubkeys);
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

        if (!sourceHeld) states.Evicted = headSource ? head : other.Root;
        SignedAggregateAndProof aggregate = GossipAggregate(chain, voteSlot, other.Root, targetEpoch, member, signingState);
        if (sourceHeld)
        {
            runner.OnAggregateAndProof(aggregate);
        }
        else
        {
            ForkChoiceException refusal = Assert.Throws<ForkChoiceException>(() => runner.OnAggregateAndProof(aggregate))!;
            Assert.That(refusal.RejectGossip, Is.False, "a valid vote with an evicted source cannot convict its relay");
        }

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(FirstCommitteeMember(other, targetEpoch).Member, Is.EqualTo(member), "fixture bug: the siblings must share committees");
        Assert.That(Weight(runner, other.Root) - weightBefore, Is.EqualTo(sourceHeld ? EffectiveBalance : 0UL), "only a vote whose target state can be checked counts");
        Assert.That(states.Replays, Is.Zero, "gossip must not regenerate an evicted head or target source");
        if (sourceHeld) Assert.That(builds.Count, Is.EqualTo(2), "the head's target state and the sibling's own");
    }

    [Test]
    public void Gossip_targets_of_other_shufflings_cost_at_most_two_states_an_epoch_and_one_an_aggregator([Values] bool oneAggregator)
    {
        const ulong targetEpoch = 2;
        (UnsignedChain chain, ForkChoiceRunner runner, BuildCounter builds) = CountedRunnerAt(targetEpoch * Presets.SlotsPerEpoch + 30);
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

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(refusedAt, Is.EqualTo(oneAggregator ? 1 : 2), "the aggregate past the budget is refused");
        Assert.That(builds.Count, Is.EqualTo(1 + refusedAt), "the head's target state and one per aggregate before it");
    }

    [Test]
    public void Gossip_aggregate_for_an_ancestor_of_the_head_with_another_shuffling_is_ignored_without_a_build()
    {
        const ulong targetEpoch = 2;
        (UnsignedChain chain, ForkChoiceRunner runner, BuildCounter builds) = CountedRunnerAt(targetEpoch * Presets.SlotsPerEpoch + 6);
        List<UnsignedChain.ChainBlock> line = ImportLine(chain, runner, chain.AnchorRoot, 30, 31, 40);
        (BeaconStateFulu signingState, ulong voteSlot, int member) = FirstCommitteeMember(line[^1], targetEpoch);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(runner.GetHead(), Is.EqualTo(line[^1].Root), "fixture bug: the line's tip must be the head");
        Assert.That(() => runner.OnAggregateAndProof(GossipAggregate(chain, voteSlot, line[0].Root, targetEpoch, member, signingState)),
            Throws.TypeOf<ForkChoiceException>().With.Message.Contains("ancestor of the head"));
        Assert.That(builds.Count, Is.Zero);
    }

    [Test]
    public void Gossip_aggregate_for_an_ancestor_of_the_head_in_the_first_two_epochs_counts()
    {
        const ulong targetEpoch = 1;
        (UnsignedChain chain, ForkChoiceRunner runner) = CreateRunner();
        TickToSlot(runner, 2 * Presets.SlotsPerEpoch - 1);
        List<UnsignedChain.ChainBlock> line = ImportLine(chain, runner, chain.AnchorRoot, 30, 32, 40);
        (BeaconStateFulu signingState, ulong voteSlot, int member) = FirstCommitteeMember(line[^1], targetEpoch);
        ulong weightBefore = Weight(runner, line[0].Root) - Weight(runner, line[1].Root);

        runner.OnAggregateAndProof(GossipAggregate(chain, voteSlot, line[0].Root, targetEpoch, member, signingState));

        Assert.That(Weight(runner, line[0].Root) - Weight(runner, line[1].Root) - weightBefore, Is.EqualTo(EffectiveBalance));
    }

    [Test]
    public void Off_head_build_allowance_is_one_per_aggregator_and_target_epoch()
    {
        const ulong currentEpoch = 3;
        (UnsignedChain chain, ForkChoiceRunner runner) = CreateRunner();
        TickToSlot(runner, (currentEpoch + 1) * Presets.SlotsPerEpoch - 1);
        List<UnsignedChain.ChainBlock> siblings = [];
        foreach (ulong slot in (ulong[])[29, 30, 31])
        {
            siblings.Add(ImportLine(chain, runner, chain.AnchorRoot, slot)[0]);
        }

        Hash256 head = runner.GetHead();
        UnsignedChain.ChainBlock headTip = siblings.Single(sibling => sibling.Root == head);
        List<UnsignedChain.ChainBlock> others = [.. siblings.Where(sibling => sibling.Root != head)];
        (BeaconStateFulu previousState, ulong previousSlot, int aggregator) = FirstCommitteeMember(headTip, currentEpoch - 1);
        (BeaconStateFulu currentState, ulong currentSlot, int _) = Enumerable.Range(0, (int)Presets.SlotsPerEpoch)
            .Select(skip => FirstCommitteeMember(headTip, currentEpoch, skip))
            .First(candidate => candidate.Member == aggregator);
        ulong[] weightsBefore = [.. others.Select(other => Weight(runner, other.Root))];

        runner.OnAggregateAndProof(GossipAggregate(chain, previousSlot, others[0].Root, currentEpoch - 1, aggregator, previousState));
        runner.OnAggregateAndProof(GossipAggregate(chain, currentSlot, others[1].Root, currentEpoch, aggregator, currentState));

        Assert.That(others.Select((other, i) => Weight(runner, other.Root) - weightsBefore[i]), Is.EqualTo((ulong[])[0, EffectiveBalance]),
            "the current-epoch vote replaces the previous-epoch one as the aggregator's latest message");
    }

    [Test]
    public void Gossip_aggregate_right_after_the_head_moves_without_a_tick_is_checked_against_the_new_head([Values] bool invalidPayload)
    {
        const ulong targetEpoch = 2;
        (UnsignedChain chain, ForkChoiceRunner runner, BuildCounter builds) = CountedRunnerAt(targetEpoch * Presets.SlotsPerEpoch + 6);
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
        runner.GetHead();
        (_, ulong voteSlot, int member) = FirstCommitteeMember(b, targetEpoch);
        int buildsBefore = builds.Count;

        Assert.That(() => runner.OnAggregateAndProof(GossipAggregate(chain, voteSlot, b.Root, targetEpoch, member, signingState: null)), Throws.InstanceOf<Exception>());
        runner.GetCheckpointState(new CheckpointRef(targetEpoch, b.Root));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(aVoters.Distinct().Count(), Is.EqualTo(2), "fixture bug: A needs two distinct voters");
        Assert.That(headBefore, Is.EqualTo(a.Root), "fixture bug: two votes put A ahead before");
        Assert.That(builds.Count - buildsBefore, Is.EqualTo(1), "the aggregate was checked against B's own target state, which the later lookup then finds");
        Assert.That(runner.GetHead(), Is.EqualTo(b.Root), "fixture bug: B is the head after");
    }

    private enum VoteStateAnchor
    {
        Genesis,
        PastDecisionSlot,
        GloasJustified,
    }
    private abstract record VoteStateStep;
    private sealed record ImportTarget(int Id, int Parent, ulong Slot) : VoteStateStep;
    private sealed record TargetVote(int Id, bool Unsigned = false) : VoteStateStep;
    private sealed record LookupTarget(int Id) : VoteStateStep;
    private sealed record AdvanceVoteClock(ulong Slot) : VoteStateStep;
    private sealed record ObserveBuilds(int Expected) : VoteStateStep;
    private sealed record VoteStateCase(string Name, VoteStateStep[] Steps, VoteStateAnchor Anchor = VoteStateAnchor.Genesis);
    private static readonly VoteStateCase[] VoteStateScenarios =
    [
        new("Vote_state_is_held_only_once_a_vote_verifies_against_it",
            [new ImportTarget(1, 0, 33), new ImportTarget(2, 1, 41), new ImportTarget(3, 2, 50), new ImportTarget(4, 3, 60),
             new TargetVote(1, Unsigned: true), new ObserveBuilds(1), new TargetVote(2, Unsigned: true), new ObserveBuilds(2), new LookupTarget(1), new ObserveBuilds(3),
             new TargetVote(3), new ObserveBuilds(4), new TargetVote(4), new ObserveBuilds(4)]),
        new("Vote_states_are_bounded_and_the_least_recently_used_shuffling_goes_first",
            [.. Enumerable.Range(1, 9).Select(i => new ImportTarget(i, 0, (ulong)i)), new ImportTarget(10, 1, 40), new ImportTarget(11, 2, 41),
             .. Enumerable.Range(1, 8).Select(i => new TargetVote(i)), new TargetVote(1), new TargetVote(9), new ObserveBuilds(9), new TargetVote(10), new ObserveBuilds(9), new TargetVote(11), new ObserveBuilds(10)]),
        new("Vote_states_of_epochs_before_the_previous_one_are_dropped_on_the_tick",
            [new ImportTarget(1, 0, 33), new ImportTarget(2, 1, 41), new ImportTarget(3, 2, 50), new TargetVote(1), new AdvanceVoteClock(96), new TargetVote(2), new ObserveBuilds(1),
             new AdvanceVoteClock(128), new TargetVote(3), new ObserveBuilds(2)]),
        new("Targets_share_a_vote_state_exactly_when_the_decision_slot_gives_them_one_block",
            [new ImportTarget(1, 0, 30), new ImportTarget(2, 1, 31), new ImportTarget(3, 1, 32), new ImportTarget(4, 3, 40), new TargetVote(2), new TargetVote(3), new ObserveBuilds(2), new TargetVote(4), new ObserveBuilds(2)]),
        new("Targets_whose_decision_slot_is_below_the_tree_root_share_one_vote_state",
            [new ImportTarget(1, 0, 50), new ImportTarget(2, 1, 60), new TargetVote(1), new TargetVote(2), new ObserveBuilds(1)], VoteStateAnchor.PastDecisionSlot),
        new("Gloas_targets_of_one_shuffling_share_one_vote_state",
            [new TargetVote(1), new TargetVote(2), new ObserveBuilds(1)], VoteStateAnchor.GloasJustified),
    ];
    private static IEnumerable<TestCaseData> VoteStateCases()
    {
        for (int i = 0; i < VoteStateScenarios.Length; i++) yield return new TestCaseData(i).SetName(VoteStateScenarios[i].Name);
    }

    [TestCaseSource(nameof(VoteStateCases))]
    public void Vote_states_follow_verified_votes_clock_and_decision_roots(int index)
    {
        VoteStateCase test = VoteStateScenarios[index];
        ForkChoiceRunner runner;
        BuildCounter builds;
        UnsignedChain? fulu = null;
        Dictionary<int, UnsignedChain.ChainBlock> blocks = [];
        Dictionary<int, Hash256> roots = [];
        ulong targetEpoch = test.Anchor == VoteStateAnchor.GloasJustified ? 3UL : 2UL;
        int committeeSize = 0;
        ForkCrossingChain? gloas = null;
        if (test.Anchor == VoteStateAnchor.GloasJustified)
        {
            gloas = ForkCrossingChain.Instance;
            runner = JustifiedOnFirstGloasBlock(gloas);
            builds = new(runner);
            BeaconStateGloas atEpochStart = gloas.Voting[^1].PostState.Clone();
            GloasSlotProcessing.ProcessSlots(atEpochStart, targetEpoch * Presets.SlotsPerEpoch, new EpochCache { Hasher = new CachedBeaconStateHasher() });
            committeeSize = new EpochCache().GetCommitteeCache(atEpochStart, targetEpoch).GetBeaconCommittee(targetEpoch * Presets.SlotsPerEpoch, 0).Length;
            roots[1] = gloas.Voting[0].Root;
            roots[2] = gloas.Voting[^1].Root;
        }
        else if (test.Anchor == VoteStateAnchor.PastDecisionSlot)
        {
            fulu = UnsignedChain.Create();
            UnsignedChain.ChainBlock root = fulu.Extend(fulu.AnchorRoot, Presets.SlotsPerEpoch, payloadHashByte: 32);
            runner = new(fulu.Spec, root.PostState, root.Block.Message!, fulu, fulu.Anchor.Pubkeys);
            builds = new(runner);
            TickToSlot(runner, targetEpoch * Presets.SlotsPerEpoch + 6);
            roots[0] = root.Root;
        }
        else
        {
            (fulu, runner, builds) = CountedRunnerAt(targetEpoch * Presets.SlotsPerEpoch + 6);
            roots[0] = fulu.AnchorRoot;
        }

        List<int> observed = [];
        List<int> expected = [];
        foreach (VoteStateStep step in test.Steps)
        {
            switch (step)
            {
                case ImportTarget import:
                    blocks[import.Id] = ImportLine(fulu!, runner, roots[import.Parent], import.Slot)[0];
                    roots[import.Id] = blocks[import.Id].Root;
                    break;
                case TargetVote vote when gloas is null:
                    Attestation attestation = BodyVote(fulu!, blocks[vote.Id], targetEpoch);
                    if (vote.Unsigned) Assert.That(() => runner.OnAttestation(attestation), Throws.TypeOf<ForkChoiceException>(), "fixture: the vote is unsigned");
                    else runner.OnAttestation(attestation, isFromBlock: true, verifySignature: false);
                    break;
                case TargetVote vote:
                    if (vote.Unsigned) throw new InvalidOperationException("The Gloas lifetime scenario requires a trusted body vote");
                    AttestationGloas attestationGloas = new()
                    {
                        AggregationBits = new BitArray(committeeSize, true),
                        Data = new AttestationData
                        {
                            Slot = targetEpoch * Presets.SlotsPerEpoch,
                            Index = 0,
                            BeaconBlockRoot = roots[vote.Id],
                            Source = new Checkpoint { Epoch = ForkCrossingChain.ForkEpoch, Root = gloas!.First.Root },
                            Target = new Checkpoint { Epoch = targetEpoch, Root = roots[vote.Id] },
                        },
                        Signature = new BlsSignature(SignatureSets.G2PointAtInfinity),
                        CommitteeBits = new BitArray(Presets.MaxCommitteesPerSlot) { [0] = true },
                    };
                    runner.OnAttestation(attestationGloas, isFromBlock: true, verifySignature: false);
                    break;
                case LookupTarget lookup:
                    runner.GetCheckpointState(new CheckpointRef(targetEpoch, roots[lookup.Id]));
                    break;
                case AdvanceVoteClock tick:
                    TickToSlot(runner, tick.Slot);
                    break;
                case ObserveBuilds observation:
                    observed.Add(builds.Count);
                    expected.Add(observation.Expected);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(step));
            }
        }
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        if (gloas is not null) Assert.That(committeeSize, Is.Positive, "fixture bug: slot 96 must have a committee");
        Assert.That(observed, Is.EqualTo(expected));
    }
    [Test]
    public void Gossip_aggregate_membership_reject_precedes_finalized_ancestry_ignore()
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = FinalizedOnFirstGloasBlock(chain);
        runner.GetHead();
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

        ForkChoiceException refusal = Assert.Throws<ForkChoiceException>(() => runner.OnAggregateAndProof(aggregate))!;
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(runner.FinalizedCheckpoint, Is.EqualTo(new CheckpointRef(ForkCrossingChain.ForkEpoch, chain.First.Root)), "fixture bug: the first Gloas block must be finalized");
        Assert.That(refusal.Message, Does.Contain("not a member"));
        Assert.That(refusal.RejectGossip, Is.True, "a provable membership failure precedes the later ancestry ignore");
    }

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

    [Test]
    public void Attester_slashing_is_verified_under_the_justified_blocks_own_state([Values] bool signedUnderBlockStateFork)
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = TickToSlot(chain.CreateRunner(), GloasTestFixtures.BoundarySlot + 1);
        BeaconStateGloas justifiesAnchorAtForkEpoch = chain.First.PostState.Clone();
        justifiesAnchorAtForkEpoch.CurrentJustifiedCheckpoint = new Checkpoint { Epoch = ForkCrossingChain.ForkEpoch, Root = chain.AnchorRoot };
        runner.OnBlock(chain.First.Block, justifiesAnchorAtForkEpoch);

        Hash256 blockStateDomain = chain.AnchorState.GetDomain(DomainType.BeaconAttester, ForkCrossingChain.ForkEpoch);
        Hash256 checkpointStateDomain = chain.UpgradedAnchor().GetDomain(DomainType.BeaconAttester, ForkCrossingChain.ForkEpoch);
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

    [Test]
    public void Gossip_vote_counts_only_from_the_slot_after_its_own([Values] bool fuluContainer)
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ulong currentSlot = GloasTestFixtures.BoundarySlot + 1;
        ForkChoiceRunner runner = TickToSlot(chain.CreateRunner(), currentSlot);
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

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(runner.ProposerBoostRoot, Is.EqualTo(Hash256.Zero), "fixture bug: no boost may confound the weight");
        Assert.That(weightInItsOwnSlot, Is.Zero);
        Assert.That(Weight(runner, chain.First.Root), Is.EqualTo(CommitteeSize * EffectiveBalance));
    }

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

    [TestCase(Presets.MaxValidatorsPerCommittee * Presets.MaxCommitteesPerSlot, false)]
    [TestCase(Presets.MaxValidatorsPerCommittee * Presets.MaxCommitteesPerSlot + 1, true)]
    public void Gloas_indexed_attestation_bound_refuses_one_index_over_it(int count, bool refused)
    {
        ulong[] indices = new ulong[count];
        Assert.That(() => ForkChoiceRunner.ThrowIfOverGloasIndexedAttestationBound(indices, "Vote"),
            refused ? Throws.TypeOf<ForkChoiceException>().With.Message.EqualTo($"Vote has {count} attesting indices, over the bound of 131072") : Throws.Nothing);
    }

    /// <summary>A Gloas vote keeps its index bound against a Fulu target; this registry gives one slot 131073 members.</summary>
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

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(validators, Has.Length.EqualTo(16), "fixture bug");
        Assert.That(validators.Select(v => v.EffectiveBalance), Is.All.EqualTo(EffectiveBalance), "fixture bug");
        Assert.That(runner.ProposerBoostRoot, Is.EqualTo(chain.BlockRoot), "fixture bug: the block must be timely");
        Assert.That(Weight(runner, chain.BlockRoot), Is.EqualTo(6_400_000_000ul));
    }

    [Test]
    public void Head_resolves_under_a_schedule_whose_modelled_forks_all_postdate_the_anchor()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        InMemoryStates states = new();
        states.States[chain.AnchorRoot] = chain.AnchorState;
        ForkChoiceRunner runner = new(BeaconChainSpec.Mainnet, chain.AnchorState, chain.AnchorBlock.Message!, states, chain.Pubkeys);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(BeaconChainSpec.Mainnet.ElectraForkEpoch, Is.GreaterThan(0ul), "fixture bug");
        Assert.That(runner.GetHead(), Is.EqualTo(chain.AnchorRoot));
    }

    [Test]
    public void Gloas_block_is_registered_optimistic_under_its_bids_block_hash()
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = TickToSlot(chain.CreateRunner(), GloasTestFixtures.BoundarySlot);

        runner.OnBlock(chain.First.Block, chain.First.PostState);

        Hash256 bidBlockHash = chain.First.Block.Message!.Body!.SignedExecutionPayloadBid!.Message!.BlockHash!;
        ForkChoiceSnapshotNode node = runner.Snapshot().Nodes.Single(n => n.Root == chain.First.Root);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(chain.First.PostState.LatestBlockHash, Is.Not.EqualTo(bidBlockHash), "fixture bug: the parent's applied hash must differ from the bid's");
        Assert.That(node.ExecutionStatus, Is.EqualTo(ExecutionStatus.Optimistic));
        Assert.That(node.ExecutionBlockHash, Is.EqualTo(bidBlockHash));
        Assert.That(runner.GetExecutionBlockHash(chain.First.Root), Is.EqualTo(bidBlockHash));
    }

    [Test]
    public void Each_OnBlock_overload_refuses_a_slot_of_the_other_fork()
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = TickToSlot(chain.CreateRunner(), GloasTestFixtures.BoundarySlot);
        SignedBeaconBlock fuluAtFork = TestChain.CreateBlock(GloasTestFixtures.BoundarySlot, chain.AnchorRoot);
        SignedBeaconBlockGloas gloasBeforeFork = new() { Message = new BeaconBlockGloas { Slot = GloasTestFixtures.BoundarySlot - 1, ParentRoot = chain.AnchorRoot } };

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(() => runner.OnBlock(fuluAtFork, chain.AnchorState, ExecutionStatus.Valid, new RecordingRule(verdict: true)),
            Throws.TypeOf<ForkChoiceException>().With.Message.Contains(nameof(SignedBeaconBlockGloas)));
        Assert.That(runner.ContainsBlock(SszRoots.HashTreeRoot(fuluAtFork.Message!)), Is.False);
        Assert.That(() => runner.OnBlock(gloasBeforeFork, chain.First.PostState),
            Throws.TypeOf<ForkChoiceException>().With.Message.Contains("before the Gloas fork"));
    }

    [Test]
    public void Gloas_block_is_refused_by_a_runner_without_a_gloas_state_provider()
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = chain.CreateRunner(withGloasStates: false);
        TickToSlot(runner, GloasTestFixtures.BoundarySlot);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(() => runner.OnBlock(chain.First.Block, chain.First.PostState),
            Throws.TypeOf<ForkChoiceException>().With.Message.Contains(nameof(IGloasBlockStateProvider)));
        Assert.That(runner.ContainsBlock(chain.First.Root), Is.False);
    }

    /// <summary>Keep previous/current justified and finalized roots distinct so checkpoint-source mixups are visible.</summary>
    [Test]
    public void Gloas_block_realizes_its_post_states_current_justified_and_finalized_checkpoints()
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = FinalizedOnFirstGloasBlock(chain);
        ForkChoiceSnapshotNode node = runner.Snapshot().Nodes.Single(n => n.Root == chain.Voting[1].Root);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(runner.JustifiedCheckpoint, Is.EqualTo(new CheckpointRef(DoctoredJustifiedEpoch, chain.Voting[0].Root)));
        Assert.That(runner.FinalizedCheckpoint, Is.EqualTo(new CheckpointRef(ForkCrossingChain.ForkEpoch, chain.First.Root)));
        Assert.That(node.JustifiedEpoch, Is.EqualTo(DoctoredJustifiedEpoch));
        Assert.That(node.FinalizedEpoch, Is.EqualTo(ForkCrossingChain.ForkEpoch));
    }

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

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(runner.CurrentSlot, Is.EqualTo(2 * Presets.SlotsPerEpoch + 2), "fixture bug");
        Assert.That(runner.FinalizedCheckpoint, Is.EqualTo(new CheckpointRef(ForkCrossingChain.ForkEpoch, chain.First.Root)), "fixture bug");
        Assert.That(() => runner.OnBlock(block, chain.Voting[1].PostState), Throws.TypeOf<ForkChoiceException>().With.Message.Contains(refusal));
        Assert.That(runner.Snapshot().Nodes, Has.Count.EqualTo(nodesBefore));
    }

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

    [Test]
    public void Timely_block_with_an_inconsistent_execution_status_is_refused_before_any_store_update()
    {
        (UnsignedChain chain, ForkChoiceRunner runner) = CreateRunner();
        TickToSlot(runner, 1);
        UnsignedChain.ChainBlock block = chain.Extend(chain.AnchorRoot, slot: 1, payloadHashByte: 0xc1);
        int nodesBefore = runner.Snapshot().Nodes.Count;
        Assert.That(block.Block.Message!.Body!.ExecutionPayload!.BlockHash, Is.Not.Null, "fixture bug: the block must carry a payload hash");

        Assert.That(() => runner.OnBlock(block.Block, block.PostState, ExecutionStatus.Irrelevant, (IReadOnlyList<DataColumnSidecar>?)null),
            Throws.TypeOf<ForkChoiceException>().With.Message.Contains("execution block hash"));
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(runner.ProposerBoostRoot, Is.EqualTo(Hash256.Zero));
        Assert.That(runner.Snapshot().Nodes, Has.Count.EqualTo(nodesBefore));
    }

    private const ulong LastEpochWithAStartSlot = 576460752303423487;

    private static ulong[] EpochsAfterTheBlockEpoch() => [3, LastEpochWithAStartSlot, ulong.MaxValue];

    [Test]
    public void Timely_block_naming_a_checkpoint_epoch_after_its_own_is_refused_before_any_store_update(
        [Values] bool justified,
        [Values(1ul, LastEpochWithAStartSlot, ulong.MaxValue)] ulong epoch)
    {
        (UnsignedChain chain, ForkChoiceRunner runner) = CreateRunner();
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

    [Test]
    public void Prior_epoch_block_with_a_state_checkpoint_epoch_after_its_own_is_refused_before_any_store_update(
        [Values] StateCheckpoint field,
        [ValueSource(nameof(EpochsAfterTheBlockEpoch))] ulong epoch)
    {
        (UnsignedChain chain, ForkChoiceRunner runner) = CreateRunner();
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

    /// <summary>ulong.MaxValue previous justification wraps a finalization rule; validate the pulled-up tip too.</summary>
    [Test]
    public void Prior_epoch_block_whose_pulled_up_finalized_epoch_is_after_its_own_is_refused_before_any_store_update()
    {
        (UnsignedChain chain, ForkChoiceRunner runner) = CreateRunner();
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
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(runner.ProposerBoostRoot, Is.EqualTo(Hash256.Zero));
        Assert.That(runner.JustifiedCheckpoint, Is.EqualTo(justifiedBefore));
        Assert.That(runner.FinalizedCheckpoint, Is.EqualTo(finalizedBefore));
        Assert.That(runner.Snapshot().Nodes, Has.Count.EqualTo(nodesBefore));
    }

    [Test]
    public void Proposer_of_a_pruned_fork_block_is_kept_until_finality_passes_its_slot()
    {
        const ulong FinalizedEpoch = 9;
        const ulong FinalizedSlot = FinalizedEpoch * Presets.SlotsPerEpoch;
        (UnsignedChain chain, ForkChoiceRunner runner) = CreateRunner();
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

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(runner.ContainsBlock(fork.Root), Is.False, "fixture bug: the proto-array must have pruned the fork block");
        Assert.That(fork.Block.Message!.ProposerIndex, Is.EqualTo(sameProposal.Block.Message!.ProposerIndex), "fixture bug");
        Assert.That(atFinalizedSlot.Block.Message!.ProposerIndex, Is.EqualTo(finalized.Block.Message!.ProposerIndex), "fixture bug");
        Assert.That(runner.IsProposerEquivocation(sameProposal.Root), Is.True, "slot 289 is after the finalized slot");
        Assert.That(runner.IsProposerEquivocation(finalized.Root), Is.False, "the rival at the finalized slot 288 is dropped");
        Assert.That(runner.IsProposerEquivocation(finalizing.Root), Is.False, "a unique proposal is no equivocation");
    }

    [Test]
    public void Signed_vote_verifies_with_keys_first_seen_in_the_registry_it_is_checked_against([Values] bool targetFirstGloasBlock)
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        PubkeyCache pubkeys = new();
        (ForkChoiceRunner runner, Hash256 target, BeaconStateGloas signingState) = RunnerForBoundaryVote(chain, targetFirstGloasBlock, pubkeys);

        runner.OnAttestation(SignedBoundaryVote(signingState, EpochOneVote(chain, GloasTestFixtures.BoundarySlot, target)));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(pubkeys.Count, Is.EqualTo(GloasTestFixtures.ValidatorCount));
        Assert.That(Weight(runner, target), Is.EqualTo(CommitteeSize * EffectiveBalance));
    }

    private static ForkChoiceRunner FinalizedOnFirstGloasBlock(ForkCrossingChain chain)
    {
        ForkChoiceRunner runner = TickToSlot(chain.CreateRunner(), 2 * Presets.SlotsPerEpoch + 2);
        runner.OnBlock(chain.First.Block, chain.First.PostState);
        runner.OnBlock(chain.Voting[0].Block, chain.Voting[0].PostState);

        BeaconStateGloas doctored = chain.Voting[1].PostState.Clone();
        doctored.PreviousJustifiedCheckpoint = new Checkpoint { Epoch = 0, Root = chain.AnchorRoot };
        doctored.CurrentJustifiedCheckpoint = new Checkpoint { Epoch = DoctoredJustifiedEpoch, Root = chain.Voting[0].Root };
        doctored.FinalizedCheckpoint = new Checkpoint { Epoch = ForkCrossingChain.ForkEpoch, Root = chain.First.Root };
        runner.OnBlock(chain.Voting[1].Block, doctored);
        return runner;
    }

    private static ForkChoiceRunner JustifiedOnFirstGloasBlock(ForkCrossingChain chain)
    {
        ForkChoiceRunner runner = TickToSlot(chain.CreateRunner(), 2 * Presets.SlotsPerEpoch + 2);
        ImportGloas(runner, chain.First, replayInFuluContainer: false);
        foreach (ForkCrossingChain.ChainBlock block in chain.Voting)
        {
            ImportGloas(runner, block, replayInFuluContainer: false);
        }

        TickToSlot(runner, 3 * Presets.SlotsPerEpoch);
        return runner;
    }

    private static (ForkChoiceRunner Runner, Action ImportChild, ulong DoctoredEpoch) TimelyChildOfInvalidFuluParent()
    {
        const ulong DoctoredEpoch = 1;
        (UnsignedChain chain, ForkChoiceRunner runner) = CreateRunner();
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

    private static (ForkChoiceRunner Runner, Action ImportChild, ulong DoctoredEpoch) TimelyChildOfInvalidGloasParent()
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = TickToSlot(chain.CreateRunner(), 2 * Presets.SlotsPerEpoch);
        runner.OnBlock(chain.First.Block, chain.First.PostState);
        runner.OnInvalidExecutionPayload(chain.First.Root);

        ForkCrossingChain.ChainBlock child = chain.Voting[0];
        BeaconStateGloas doctored = child.PostState.Clone();
        doctored.CurrentJustifiedCheckpoint = new Checkpoint { Epoch = ForkCrossingChain.ForkEpoch, Root = chain.First.Root };
        doctored.FinalizedCheckpoint = new Checkpoint { Epoch = ForkCrossingChain.ForkEpoch, Root = chain.First.Root };
        Assert.That(child.Block.Message!.Slot, Is.EqualTo(runner.CurrentSlot), "fixture bug: the child must be timely");
        return (runner, () => runner.OnBlock(child.Block, doctored), ForkCrossingChain.ForkEpoch);
    }

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

    private static (ForkChoiceRunner Runner, Hash256 Target, BeaconStateGloas SigningState) RunnerForBoundaryVote(
        ForkCrossingChain chain, bool targetFirstGloasBlock, PubkeyCache? pubkeys)
    {
        ForkChoiceRunner runner = chain.CreateRunner(pubkeys: pubkeys);
        TickToSlot(runner, GloasTestFixtures.BoundarySlot + 1);
        if (!targetFirstGloasBlock)
            return (runner, chain.AnchorRoot, chain.UpgradedAnchor());

        runner.OnBlock(chain.First.Block, chain.First.PostState);
        return (runner, chain.First.Root, chain.First.PostState);
    }

    private static AttestationData EpochOneVote(ForkCrossingChain chain, ulong slot, Hash256 target) => new()
    {
        Slot = slot,
        Index = 0,
        BeaconBlockRoot = target,
        Source = new Checkpoint { Epoch = 0, Root = chain.AnchorRoot },
        Target = new Checkpoint { Epoch = ForkCrossingChain.ForkEpoch, Root = target },
    };

    private static AttestationGloas SignedBoundaryVote(BeaconStateGloas signingState, AttestationData data) =>
        GloasTestFixtures.CommitteeAttestation(
            signingState, data, new EpochCache().GetCommitteeCache(signingState, ForkCrossingChain.ForkEpoch), 0, sign: true);

    private static IndexedAttestationGloas SignedUnder(Hash256 domain, AttestationData data, ulong[] signers) => new()
    {
        AttestingIndices = signers,
        Data = data,
        Signature = GloasTestFixtures.AggregateSignature(Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(data), domain), [.. signers.Select(static i => (int)i)]),
    };

    private static IndexedAttestation ToFuluIndexed(IndexedAttestationGloas attestation) =>
        new() { AttestingIndices = attestation.AttestingIndices, Data = attestation.Data, Signature = attestation.Signature };

    private static Hash256 BlockStateRoot(ForkCrossingChain chain, bool gloasRoot) =>
        gloasRoot ? SszRoots.HashTreeRoot(chain.First.PostState) : SszRoots.HashTreeRoot(chain.AnchorState);

    private static ForkChoiceRunner TickToSlot(ForkChoiceRunner runner, ulong slot)
    {
        runner.OnTick(runner.GenesisTime + slot * Presets.SecondsPerSlot);
        return runner;
    }

    private static Dictionary<Hash256, ulong> Weights(ForkChoiceRunner runner)
    {
        runner.GetHead();
        return runner.Snapshot().Nodes.ToDictionary(n => n.Root, n => n.Weight);
    }

    private static ulong Weight(ForkChoiceRunner runner, Hash256 root)
    {
        runner.GetHead();
        return runner.Snapshot().Nodes.Single(n => n.Root == root).Weight;
    }

    /// <summary>Tick past all blocks so proposer boost cannot confound slashing weights.</summary>
    private static (ForkChoiceRunner Runner, UnsignedChain.ChainBlock Voted, UnsignedChain.ChainBlock B, UnsignedChain.ChainBlock Slashing) EquivocationScenario()
    {
        (UnsignedChain chain, ForkChoiceRunner runner) = CreateRunner();
        runner.OnTick(runner.GenesisTime + 8 * chain.Spec.SecondsPerSlot);
        UnsignedChain.Equivocation scenario = chain.BuildEquivocation();

        ImportWithBodyReplay(runner, scenario.A);
        ImportWithBodyReplay(runner, scenario.B);
        ImportWithBodyReplay(runner, scenario.Voted);
        return (runner, scenario.Voted, scenario.B, scenario.Slashing);
    }

    private static void ImportWithBodyReplay(ForkChoiceRunner runner, UnsignedChain.ChainBlock block, bool asBodyOfTheBlock = false)
    {
        runner.OnBlock(block.Block, block.PostState, ExecutionStatus.Valid, (IReadOnlyList<DataColumnSidecar>?)null);
        BeaconBlockBody body = block.Block.Message!.Body!;
        foreach (Attestation attestation in body.Attestations!)
        {
            if (asBodyOfTheBlock)
                runner.OnBodyAttestation(attestation, block.Root);
            else
                runner.OnAttestation(attestation, isFromBlock: true, verifySignature: false);
        }

        foreach (AttesterSlashing slashing in body.AttesterSlashings!)
        {
            runner.OnAttesterSlashing(slashing, verifySignatures: false);
        }
    }

    private static (UnsignedChain Chain, ForkChoiceRunner Runner) CreateRunner()
    {
        UnsignedChain chain = UnsignedChain.Create();
        ForkChoiceRunner runner = new(chain.Spec, chain.Anchor.AnchorState, chain.Anchor.AnchorBlock.Message!, chain, chain.Anchor.Pubkeys);
        return (chain, runner);
    }

    private static (UnsignedChain Chain, ForkChoiceRunner Runner, BuildCounter Builds) CountedRunnerAt(ulong slot)
    {
        (UnsignedChain chain, ForkChoiceRunner runner) = CreateRunner();
        BuildCounter builds = new(runner);
        TickToSlot(runner, slot);
        return (chain, runner, builds);
    }

    private static (ForkChoiceRunner Runner, BeaconStateFulu PostState) RunnerAt(ImportableBlobBlock chain, BeaconStateFulu? anchorBlockState = null)
    {
        InMemoryStates states = new();
        states.States[chain.AnchorRoot] = anchorBlockState ?? chain.AnchorState;
        ForkChoiceRunner runner = new(chain.Spec, chain.AnchorState, chain.AnchorBlock.Message!, states, chain.Pubkeys);
        runner.OnTick(runner.GenesisTime + chain.Block.Message!.Slot * chain.Spec.SecondsPerSlot);

        BeaconStateFulu postState = chain.AnchorState.Clone();
        FuluStateTransition.Apply(postState, chain.Block, new EpochCache(), chain.Pubkeys, new TestEngineDriver.BodyOnlyNotifier(), chain.Spec);
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

    private sealed class EvictableGossipStates(UnsignedChain chain) : IForkChoiceStateProvider
    {
        public Hash256? Evicted { get; set; }
        public int Replays { get; private set; }

        public BeaconStateFulu? GetBlockState(Hash256 blockRoot)
        {
            if (blockRoot == Evicted) Replays++;
            return chain.GetBlockState(blockRoot);
        }

        public BeaconStateFulu? CopyBlockState(Hash256 blockRoot) => GetBlockState(blockRoot)?.Clone();

        BeaconStateFulu? IForkChoiceStateProvider.GetHeldBlockState(Hash256 blockRoot) => blockRoot == Evicted ? null : chain.GetBlockState(blockRoot);
    }

    private sealed class GossipStates(BeaconStateFulu state) : IForkChoiceStateProvider
    {
        public bool Held { get; init; }
        public int Loads { get; private set; }

        public BeaconStateFulu? GetBlockState(Hash256 blockRoot)
        {
            Loads++;
            return state;
        }

        public BeaconStateFulu? CopyBlockState(Hash256 blockRoot) => GetBlockState(blockRoot)?.Clone();

        BeaconStateFulu? IForkChoiceStateProvider.GetHeldBlockState(Hash256 blockRoot) => Held ? state : null;
    }

    private sealed class InMemoryStates : IForkChoiceStateProvider
    {
        public Dictionary<Hash256, BeaconStateFulu> States { get; } = [];
        public BeaconStateFulu? GetBlockState(Hash256 blockRoot) => States.GetValueOrDefault(blockRoot);
        public BeaconStateFulu? CopyBlockState(Hash256 blockRoot) => GetBlockState(blockRoot)?.Clone();
    }

    /// <summary>Lookup succeeds but copy observes eviction, witnessing a state-provider race.</summary>
    private sealed class EvictedBeforeCopy(UnsignedChain chain) : IForkChoiceStateProvider
    {
        public BeaconStateFulu? GetBlockState(Hash256 blockRoot) => chain.GetBlockState(blockRoot);
        public BeaconStateFulu? CopyBlockState(Hash256 blockRoot) => null;
    }

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

    private static Attestation BodyVote(UnsignedChain chain, UnsignedChain.ChainBlock target, ulong epoch, int skip = 0, bool sign = false)
    {
        (BeaconStateFulu signingState, ulong slot, int member) = FirstCommitteeMember(target, epoch, skip);
        AttestationData data = VoteData(chain, slot, target.Root, epoch);
        return new Attestation
        {
            AggregationBits = new BitArray(1, true),
            Data = data,
            Signature = sign
                ? ImportableBlobBlock.SignAs((ulong)member, SszRoots.HashTreeRoot(data), signingState.GetDomain(DomainType.BeaconAttester, epoch))
                : new BlsSignature(SignatureSets.G2PointAtInfinity),
            CommitteeBits = new BitArray(Presets.MaxCommitteesPerSlot) { [0] = true },
        };
    }

    private static Attestation WholeCommitteeVote(UnsignedChain chain, ulong slot, Hash256 target, ulong epoch, CommitteeCache committees) => new()
    {
        AggregationBits = new BitArray(committees.GetBeaconCommittee(slot, 0).Length, true),
        Data = VoteData(chain, slot, target, epoch),
        Signature = new BlsSignature(SignatureSets.G2PointAtInfinity),
        CommitteeBits = new BitArray(Presets.MaxCommitteesPerSlot) { [0] = true },
    };

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

    private static List<CountingHasher> CountCheckpointHashes(ForkChoiceRunner runner)
    {
        List<CountingHasher> made = [];
        runner.CheckpointStateHasher = () =>
        {
            CountingHasher hasher = new(new CachedBeaconStateHasher());
            made.Add(hasher);
            return hasher;
        };
        return made;
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
