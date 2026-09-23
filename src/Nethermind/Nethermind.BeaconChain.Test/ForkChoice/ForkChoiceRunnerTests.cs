// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Test.StateTransition;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using NUnit.Framework;
using FuluStateTransition = Nethermind.BeaconChain.StateTransition.StateTransition;

namespace Nethermind.BeaconChain.Test.ForkChoice;

/// <summary>
/// The pure predicates behind <see cref="ForkChoiceRunner.ShouldOverrideForkchoiceUpdate"/> (silent-wrong
/// risks: get_proposer_head returning the ordinary head instead of ever re-org'ing), and the way
/// <see cref="ForkChoiceRunner.OnBlock"/> hands <c>is_data_available</c> to whichever
/// <see cref="IDataAvailabilityRule"/> its caller chose: a column list means the supernode rule the
/// spec vectors need, a rule means exactly that rule, and neither is ever inferred from the other.
/// Also the body replay <see cref="ForkChoiceRunner.OnBlock(SignedBeaconBlock, BeaconStateFulu, ExecutionStatus, IDataAvailabilityRule)"/>
/// leaves to its caller, on a hand-built chain (<see cref="UnsignedChain"/>): no mainnet vector
/// carries a body attester slashing, so the vectors never show whether a replayed one is honored.
/// </summary>
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

    private sealed class AcceptingNotifier : INewPayloadNotifier
    {
        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body) => ExecutionStatus.Valid;
    }
}
