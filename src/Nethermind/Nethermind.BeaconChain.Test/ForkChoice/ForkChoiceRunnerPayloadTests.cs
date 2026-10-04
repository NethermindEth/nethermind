// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;

namespace Nethermind.BeaconChain.Test.ForkChoice;

/// <summary>
/// The Gloas <c>store.payloads</c> in <see cref="ForkChoiceRunner"/>: the <c>on_block</c> gate that holds back a
/// block building on an unverified full parent, the pre-Gloas parent that needs no envelope, the
/// <c>validate_on_attestation</c> payload-status rules, and the bid parent hashes kept per Gloas block.
/// </summary>
/// <remarks>
/// The children built here are slot-advanced copies of their parent's post-state, never run through
/// <c>process_block</c>: fork choice reads only the post-state's checkpoints and registry.
/// </remarks>
[HardTimeout(60_000)]
public class ForkChoiceRunnerPayloadTests
{
    private const ulong EffectiveBalance = 32 * Gwei;

    /// <summary>With 2048 validators and 32 slots, each slot has one committee of 64.</summary>
    private const ulong CommitteeSize = 64;

    /// <summary>
    /// specs/gloas/fork-choice.md <c>on_block</c>: a block building on its parent's full payload is accepted only once
    /// that payload is verified, or fork choice would weigh a chain whose execution the node never checked. A block
    /// building on the payload before its parent's (an empty parent) needs nothing. A refused block moves no store state:
    /// it takes no proposer boost and leaves no bid record behind.
    /// </summary>
    [Test]
    public void Block_building_on_a_full_gloas_parent_waits_for_that_parent_payload([Values] bool buildsOnFull, [Values] bool parentPayloadVerified)
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ulong childSlot = BoundarySlot + 1;
        ForkChoiceRunner runner = chain.CreateRunner();
        TickToSlot(runner, childSlot);
        runner.OnBlock(chain.First.Block, chain.First.PostState);
        if (parentPayloadVerified)
            runner.OnExecutionPayloadVerified(chain.First.Root);

        Hash256 bidParentBlockHash = buildsOnFull ? BidOf(chain.First.Block).BlockHash! : chain.First.PostState.LatestBlockHash!;
        SignedBeaconBlockGloas child = ChildOf(chain.First.PostState, childSlot, bidParentBlockHash, out BeaconStateGloas childState);
        Hash256 childRoot = SszRoots.HashTreeRoot(child.Message!);
        Assert.That(runner.IsParentNodeFull(child.Message!), Is.EqualTo(buildsOnFull), "fixture bug");

        bool accepted = !buildsOnFull || parentPayloadVerified;
        Assert.That(() => runner.OnBlock(child, childState),
            accepted ? Throws.Nothing : Throws.TypeOf<ForkChoiceException>().With.Message.Contains("which is not verified"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(runner.ContainsBlock(childRoot), Is.EqualTo(accepted));
            Assert.That(runner.ProposerBoostRoot, Is.EqualTo(accepted ? childRoot : Hash256.Zero), "the timely child takes the boost only if accepted");
            Assert.That(runner.GetParentBlockHash(childRoot), Is.EqualTo(accepted ? bidParentBlockHash : null));
        }
    }

    /// <summary>
    /// The first Gloas block builds on its Fulu parent's payload, which came inside that block and has no envelope.
    /// A literal <c>root in store.payloads</c> would refuse it and stall every node at the fork.
    /// </summary>
    [Test]
    public void First_gloas_block_building_on_the_fulu_payload_needs_no_envelope()
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = chain.CreateRunner();
        TickToSlot(runner, BoundarySlot);
        BeaconStateGloas state = chain.UpgradedAnchor();
        SignedBeaconBlockGloas block = MinimalBlock(state, SelfBuildBid(state, runner.GetExecutionBlockHash(chain.AnchorRoot)!, Hash(0xE2)));
        Assert.That(runner.IsParentNodeFull(block.Message!), Is.True, "fixture bug: the block must build on the anchor's payload");

        runner.OnBlock(block, state);

        Assert.That(runner.ContainsBlock(SszRoots.HashTreeRoot(block.Message!)), Is.True);
    }

    /// <summary>
    /// <c>store.payloads</c> holds only Gloas blocks fork choice knows (the spec asserts the envelope's block is in
    /// <c>store.block_states</c>); a pre-Gloas block counts as verified without an entry, an unknown one never does,
    /// and recording the same payload twice is harmless, since an envelope can arrive by gossip and by request.
    /// </summary>
    [Test]
    public void Payload_verification_is_recorded_only_for_known_gloas_blocks()
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = chain.CreateRunner();
        TickToSlot(runner, BoundarySlot + 1);
        runner.OnBlock(chain.First.Block, chain.First.PostState);
        Hash256 unknown = Hash(0xEE);
        bool firstVerifiedBefore = runner.IsPayloadVerified(chain.First.Root);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => runner.OnExecutionPayloadVerified(chain.AnchorRoot), Throws.TypeOf<ForkChoiceException>().With.Message.Contains("before the Gloas fork"));
            Assert.That(() => runner.OnExecutionPayloadVerified(unknown), Throws.TypeOf<ForkChoiceException>().With.Message.Contains("unknown"));
        }

        runner.OnExecutionPayloadVerified(chain.First.Root);
        Assert.That(() => runner.OnExecutionPayloadVerified(chain.First.Root), Throws.Nothing);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstVerifiedBefore, Is.False);
            Assert.That(runner.IsPayloadVerified(chain.First.Root), Is.True);
            Assert.That(runner.IsPayloadVerified(chain.AnchorRoot), Is.True);
            Assert.That(runner.IsPayloadVerified(unknown), Is.False);
            Assert.That(runner.GetParentBlockHash(chain.First.Root), Is.EqualTo(BidOf(chain.First.Block).ParentBlockHash));
            Assert.That(runner.GetParentBlockHash(chain.AnchorRoot), Is.Null);
        }
    }

    /// <summary>
    /// The bid parent hashes and PTC votes are pruned with the blocks they describe: a finalized chain long enough for the
    /// proto-array to prune (<see cref="ProtoArrayForkChoice.DefaultPruneThreshold"/> nodes) must not keep
    /// answering for blocks fork choice dropped, or the maps grow for the life of the node.
    /// </summary>
    [Test]
    public void Prune_drops_the_bid_parent_hash_of_every_pruned_block()
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        const ulong FinalizedEpoch = 9;
        ulong finalizedSlot = FinalizedEpoch * Presets.SlotsPerEpoch;
        ulong lastSlot = finalizedSlot + 1;
        ForkChoiceRunner runner = chain.CreateRunner();
        TickToSlot(runner, lastSlot);
        runner.OnBlock(chain.First.Block, chain.First.PostState);

        SignedBeaconBlockGloas template = ChildOf(chain.First.PostState, BoundarySlot + 1, chain.First.PostState.LatestBlockHash!, out _);
        Hash256 parentRoot = chain.First.Root;
        Hash256 finalizedRoot = Hash256.Zero;
        for (ulong slot = BoundarySlot + 1; slot <= lastSlot; slot++)
        {
            SignedBeaconBlockGloas block = WithSlotAndParent(template, slot, parentRoot);
            BeaconStateGloas postState = chain.First.PostState;
            if (slot == lastSlot)
            {
                postState = chain.First.PostState.Clone();
                postState.CurrentJustifiedCheckpoint = new Checkpoint { Epoch = FinalizedEpoch, Root = finalizedRoot };
                postState.FinalizedCheckpoint = new Checkpoint { Epoch = FinalizedEpoch, Root = finalizedRoot };
            }

            runner.OnBlock(block, postState);
            parentRoot = SszRoots.HashTreeRoot(block.Message!);
            if (slot == finalizedSlot)
                finalizedRoot = parentRoot;
        }

        Assert.That(runner.FinalizedCheckpoint, Is.EqualTo(new CheckpointRef(FinalizedEpoch, finalizedRoot)), "fixture bug");
        runner.Prune();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(runner.ContainsBlock(chain.First.Root), Is.False, "fixture bug: the proto-array must have pruned");
            Assert.That(runner.GetParentBlockHash(chain.First.Root), Is.Null);
            Assert.That(runner.GetParentBlockHash(parentRoot), Is.Not.Null, "the unpruned tip keeps its record");
            Assert.That(runner.GetPtcVotes(chain.First.Root), Is.Null);
            Assert.That(runner.GetPtcVotes(parentRoot), Is.Not.Null, "the unpruned tip keeps its PTC votes");
        }
    }

    /// <summary>A vote's payload-status index and whether it is refused (the message fragment) or counted (<see langword="null"/>).</summary>
    public readonly record struct PayloadVote(ulong Index, bool SameSlot, bool PayloadVerified, string? Refusal)
    {
        public override string ToString() => $"index {Index}, {(SameSlot ? "same" : "later")} slot, payload {(PayloadVerified ? "verified" : "unverified")}";
    }

    private static IEnumerable<PayloadVote> PayloadVotes() =>
    [
        new(Index: 0, SameSlot: true, PayloadVerified: false, Refusal: null),
        new(Index: 0, SameSlot: false, PayloadVerified: false, Refusal: null),
        new(Index: 1, SameSlot: false, PayloadVerified: true, Refusal: null),
        new(Index: 1, SameSlot: false, PayloadVerified: false, Refusal: "which is not verified"),
        new(Index: 1, SameSlot: true, PayloadVerified: true, Refusal: "from its own slot"),
        new(Index: 2, SameSlot: false, PayloadVerified: true, Refusal: "is not a payload status"),
    ];

    /// <summary>
    /// specs/gloas/fork-choice.md <c>validate_on_attestation</c>: <c>data.index</c> votes for the head block's payload
    /// status, so it is 0 or 1, 0 in the block's own slot, and 1 only for a verified payload. Otherwise fork choice
    /// counts votes for payloads it never verified. The rules hold for gossip and block-body votes alike, and for a
    /// vote at a Gloas slot whichever container carried it.
    /// </summary>
    [Test]
    public void Vote_payload_status_index_is_validated(
        [ValueSource(nameof(PayloadVotes))] PayloadVote vote, [Values] bool isFromBlock, [Values] bool fuluContainer)
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = chain.CreateRunner();
        TickToSlot(runner, BoundarySlot + 2);
        runner.OnBlock(chain.First.Block, chain.First.PostState);
        if (vote.PayloadVerified)
            runner.OnExecutionPayloadVerified(chain.First.Root);

        AttestationData data = new()
        {
            Slot = vote.SameSlot ? BoundarySlot : BoundarySlot + 1,
            Index = vote.Index,
            BeaconBlockRoot = chain.First.Root,
            Source = new Checkpoint { Epoch = 0, Root = chain.AnchorRoot },
            Target = new Checkpoint { Epoch = ForkCrossingChain.ForkEpoch, Root = chain.First.Root },
        };
        AttestationGloas attestation = CommitteeAttestation(
            chain.First.PostState, data, new EpochCache().GetCommitteeCache(chain.First.PostState, ForkCrossingChain.ForkEpoch), 0, sign: false);
        Action attest = fuluContainer
            ? () => runner.OnAttestation(ToFuluAttestation(attestation), isFromBlock, verifySignature: false)
            : () => runner.OnAttestation(attestation, isFromBlock, verifySignature: false);

        Assert.That(attest, vote.Refusal is null ? Throws.Nothing : Throws.TypeOf<ForkChoiceException>().With.Message.Contains(vote.Refusal));
        Assert.That(Weight(runner, chain.First.Root), Is.EqualTo(vote.Refusal is null ? CommitteeSize * EffectiveBalance : 0));
    }

    /// <summary>
    /// specs/gloas/fork-choice.md <c>validate_on_attestation</c>: an index-1 vote needs <c>is_payload_verified</c>, which is
    /// <c>root in store.payloads</c>. A pre-Gloas block has no envelope and never enters it, so a Gloas-slot index-1 vote for the
    /// last Fulu block is refused, from gossip and from a block, while its index-0 vote counts.
    /// </summary>
    [Test]
    public void Index_one_vote_for_a_pre_gloas_block_is_refused([Values(0ul, 1ul)] ulong index, [Values] bool isFromBlock)
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = chain.CreateRunner();
        TickToSlot(runner, BoundarySlot + 2);
        AttestationData data = new()
        {
            Slot = BoundarySlot + 1,
            Index = index,
            BeaconBlockRoot = chain.AnchorRoot,
            Source = new Checkpoint { Epoch = 0, Root = chain.AnchorRoot },
            Target = new Checkpoint { Epoch = ForkCrossingChain.ForkEpoch, Root = chain.AnchorRoot },
        };
        AttestationGloas attestation = CommitteeAttestation(
            chain.First.PostState, data, new EpochCache().GetCommitteeCache(chain.First.PostState, ForkCrossingChain.ForkEpoch), 0, sign: false);

        Assert.That(() => runner.OnAttestation(attestation, isFromBlock, verifySignature: false),
            index == 1 ? Throws.TypeOf<ForkChoiceException>().With.Message.Contains("which is not verified") : Throws.Nothing);
    }

    /// <summary>
    /// specs/gloas/validator.md sets <c>data.index</c> to 1 only for a FULL head. The head of a store whose tip is the last Fulu
    /// block is its EMPTY node, so the vote an attester derives from it is one <c>validate_on_attestation</c> counts.
    /// </summary>
    [Test]
    public void A_vote_derived_from_a_pre_gloas_head_is_accepted()
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = chain.CreateRunner();
        TickToSlot(runner, BoundarySlot + 1);
        ForkChoiceNode head = runner.GetHeadNode();
        Assert.That(head.Root, Is.EqualTo(chain.AnchorRoot), "fixture bug");

        AttestationData data = new()
        {
            Slot = BoundarySlot + 1,
            Index = head.PayloadStatus == ForkChoicePayloadStatus.Full ? 1ul : 0ul,
            BeaconBlockRoot = head.Root,
            Source = new Checkpoint { Epoch = 0, Root = chain.AnchorRoot },
            Target = new Checkpoint { Epoch = ForkCrossingChain.ForkEpoch, Root = chain.AnchorRoot },
        };
        AttestationGloas attestation = CommitteeAttestation(
            chain.First.PostState, data, new EpochCache().GetCommitteeCache(chain.First.PostState, ForkCrossingChain.ForkEpoch), 0, sign: false);

        Assert.That(() => runner.OnAttestation(attestation, isFromBlock: true, verifySignature: false), Throws.Nothing);
    }

    private static ExecutionPayloadBid BidOf(SignedBeaconBlockGloas block) => block.Message!.Body!.SignedExecutionPayloadBid!.Message!;

    /// <summary>A self-built child of the block whose post-state is <paramref name="parentPostState"/>, at <paramref name="slot"/>, with its bid on <paramref name="bidParentBlockHash"/>.</summary>
    private static SignedBeaconBlockGloas ChildOf(BeaconStateGloas parentPostState, ulong slot, Hash256 bidParentBlockHash, out BeaconStateGloas postState)
    {
        postState = parentPostState.Clone();
        GloasSlotProcessing.ProcessSlots(postState, slot, new EpochCache());
        return MinimalBlock(postState, SelfBuildBid(postState, bidParentBlockHash, Hash(0xE1)));
    }

    /// <summary><paramref name="template"/> moved to <paramref name="slot"/> on <paramref name="parentRoot"/>, sharing its body.</summary>
    private static SignedBeaconBlockGloas WithSlotAndParent(SignedBeaconBlockGloas template, ulong slot, Hash256 parentRoot)
    {
        BeaconBlockGloas message = template.Message!;
        return new SignedBeaconBlockGloas
        {
            Message = new BeaconBlockGloas
            {
                Slot = slot,
                ProposerIndex = message.ProposerIndex,
                ParentRoot = parentRoot,
                StateRoot = message.StateRoot,
                Body = message.Body,
            },
            Signature = template.Signature,
        };
    }

    private static void TickToSlot(ForkChoiceRunner runner, ulong slot) =>
        runner.OnTick(runner.GenesisTime + slot * Presets.SecondsPerSlot);

    /// <summary>The weight of <paramref name="root"/> as a fresh <see cref="ForkChoiceRunner.GetHead"/> computes it.</summary>
    private static ulong Weight(ForkChoiceRunner runner, Hash256 root)
    {
        runner.GetHead();
        return runner.Snapshot().Nodes.Single(n => n.Root == root).Weight;
    }

    // specs/gloas/fork-choice.md get_node_children: an invalid FULL payload leaves the EMPTY branch viable.
    [Test]
    public void Invalid_verdict_on_a_full_head_removes_only_its_payload([Values] bool latestValidIsParentPayload)
    {
        GloasForkChoiceHarness harness = new();
        GloasForkChoiceHarness.Block first = ImportVerifiedFirst(harness);
        GloasForkChoiceHarness.Block b = harness.Child(first, first.Slot + 1, full: true, 0xB1);
        harness.Import(b);
        harness.Runner.OnExecutionPayloadVerified(b.Root);
        GloasForkChoiceHarness.Block onEmpty = harness.Child(b, first.Slot + 2, first.BidBlockHash, 0xE1);
        GloasForkChoiceHarness.Block onFull = harness.Child(b, first.Slot + 2, full: true, 0xF1);
        harness.Import(onEmpty);
        harness.Import(onFull);
        harness.Runner.OnExecutionPayloadVerified(onFull.Root);
        GloasForkChoiceHarness.Block onFullDescendant = harness.Child(onFull, first.Slot + 3, full: true, 0xF2);
        harness.Import(onFullDescendant);
        GloasForkChoiceHarness.Block grandchild = harness.Child(onEmpty, first.Slot + 3, first.BidBlockHash, 0xE2);

        harness.Runner.InvalidateExecutionChain(b.Root, b.BidBlockHash, latestValidIsParentPayload ? first.BidBlockHash : null);
        harness.Runner.OnExecutionPayloadVerified(b.Root);
        Assert.That(() => harness.Import(grandchild), Throws.Nothing, "a block on B's EMPTY branch is still accepted");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Runner.IsPayloadVerified(b.Root), Is.False, "B's FULL node leaves the tree, and a later optimistic envelope import does not bring it back");
            Assert.That(harness.Runner.GetBlockExecutionStatus(b.Root), Is.EqualTo(ExecutionStatus.Optimistic), "B builds on a payload the verdict does not name");
            Assert.That(harness.Runner.GetBlockExecutionStatus(onEmpty.Root), Is.EqualTo(ExecutionStatus.Optimistic));
            Assert.That(harness.Runner.GetBlockExecutionStatus(onFull.Root), Is.EqualTo(ExecutionStatus.Invalid));
            Assert.That(harness.Runner.GetBlockExecutionStatus(onFullDescendant.Root), Is.EqualTo(ExecutionStatus.Invalid));
            Assert.That(harness.Runner.GetHeadNode(), Is.EqualTo(new ForkChoiceNode(grandchild.Root, ForkChoicePayloadStatus.Empty)));
        }
    }

    [Test]
    public void Latest_valid_hash_walk_skips_a_block_whose_payload_the_chain_bypassed()
    {
        GloasForkChoiceHarness harness = new();
        GloasForkChoiceHarness.Block first = ImportVerifiedFirst(harness);
        GloasForkChoiceHarness.Block x = harness.Child(first, first.Slot + 1, full: true, 0xA1);
        harness.Import(x);
        harness.Runner.OnExecutionPayloadVerified(x.Root);
        GloasForkChoiceHarness.Block b = harness.Child(x, first.Slot + 2, first.BidBlockHash, 0xB1);
        harness.Import(b);
        harness.Runner.OnExecutionPayloadVerified(b.Root);
        GloasForkChoiceHarness.Block onEmpty = harness.Child(b, first.Slot + 3, first.BidBlockHash, 0xE1);
        harness.Import(onEmpty);

        harness.Runner.InvalidateExecutionChain(b.Root, b.BidBlockHash, first.BidBlockHash);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Runner.IsPayloadVerified(b.Root), Is.False);
            Assert.That(harness.Runner.IsPayloadVerified(x.Root), Is.True, "X's payload is not an execution ancestor of B's");
            Assert.That(harness.Runner.GetBlockExecutionStatus(x.Root), Is.EqualTo(ExecutionStatus.Optimistic));
            Assert.That(harness.Runner.GetBlockExecutionStatus(b.Root), Is.EqualTo(ExecutionStatus.Optimistic));
            Assert.That(harness.Runner.GetBlockExecutionStatus(onEmpty.Root), Is.EqualTo(ExecutionStatus.Optimistic));
        }
    }

    [TestCase(true, TestName = "Invalid verdict on an EMPTY head removes the ancestor payload its hash names")]
    [TestCase(false, TestName = "Invalid verdict with an older latest valid hash removes every payload after it")]
    public void Invalid_verdict_removes_the_payload_the_sent_hash_names(bool emptyHead)
    {
        GloasForkChoiceHarness harness = new();
        GloasForkChoiceHarness.Block first = ImportVerifiedFirst(harness);
        GloasForkChoiceHarness.Block invalidAncestor = first;
        if (!emptyHead)
        {
            invalidAncestor = harness.Child(first, first.Slot + 1, full: true, 0xA1);
            harness.Import(invalidAncestor);
            harness.Runner.OnExecutionPayloadVerified(invalidAncestor.Root);
        }
        GloasForkChoiceHarness.Block b = harness.Child(invalidAncestor, invalidAncestor.Slot + 1, full: true, 0xB1);
        harness.Import(b);
        harness.Runner.OnExecutionPayloadVerified(b.Root);
        GloasForkChoiceHarness.Block e = harness.Child(b, b.Slot + 1, invalidAncestor.BidBlockHash, 0xE1);
        harness.Import(e);

        if (emptyHead)
            harness.Runner.InvalidateExecutionChain(e.Root, invalidAncestor.BidBlockHash, latestValidHash: null);
        else
            harness.Runner.InvalidateExecutionChain(b.Root, b.BidBlockHash, first.BidBlockHash);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Runner.IsPayloadVerified(invalidAncestor.Root), Is.False, "the ancestor payload is invalid");
            Assert.That(harness.Runner.GetBlockExecutionStatus(invalidAncestor.Root), Is.EqualTo(ExecutionStatus.Optimistic));
            Assert.That(harness.Runner.GetBlockExecutionStatus(b.Root), Is.EqualTo(ExecutionStatus.Invalid));
            Assert.That(harness.Runner.GetBlockExecutionStatus(e.Root), Is.EqualTo(ExecutionStatus.Invalid));
            Assert.That(harness.Runner.GetHeadNode(), Is.EqualTo(new ForkChoiceNode(invalidAncestor.Root, ForkChoicePayloadStatus.Empty)));
        }
    }

    // specs/bellatrix/optimistic-sync.md: a VALID payload cannot become INVALID without intervention.
    [Test]
    public void Invalid_verdict_on_a_payload_called_valid_is_refused()
    {
        GloasForkChoiceHarness harness = new();
        GloasForkChoiceHarness.Block first = ImportVerifiedFirst(harness);
        GloasForkChoiceHarness.Block b = harness.Child(first, first.Slot + 1, full: true, 0xB1);
        harness.Import(b);
        harness.Runner.OnExecutionPayloadVerified(b.Root);
        harness.Runner.ValidateExecutionChain(b.Root, b.BidBlockHash);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => harness.Runner.InvalidateExecutionChain(b.Root, b.BidBlockHash, latestValidHash: null), Throws.TypeOf<ProtoArrayException>());
            Assert.That(harness.Runner.IsPayloadVerified(b.Root), Is.True);
        }
    }

    [Test]
    public void Valid_verdict_stops_at_a_pre_gloas_carrier([Values] bool throughEmptyHead)
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = chain.CreateRunner();
        Hash256 headRoot = chain.AnchorRoot;
        Hash256 payloadHash = runner.GetExecutionBlockHash(chain.AnchorRoot)!;
        if (throughEmptyHead)
        {
            TickToSlot(runner, BoundarySlot);
            BeaconStateGloas state = chain.UpgradedAnchor();
            SignedBeaconBlockGloas block = MinimalBlock(state, SelfBuildBid(state, payloadHash, Hash(0xE2)));
            runner.OnBlock(block, state);
            headRoot = SszRoots.HashTreeRoot(block.Message!);
        }

        ProtoNode carrier = runner.EnumerateAncestors(chain.AnchorRoot).First();
        Assert.That(carrier.ExecutionStatus, Is.EqualTo(ExecutionStatus.Valid));
        // An unreadable parent detects traversal past a validated carrier without a chain-depth timing assertion.
        carrier.Parent = int.MaxValue;

        Hash256? payloadRoot = runner.ValidateExecutionChainAndGetPayloadRoot(headRoot, payloadHash);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(payloadRoot, Is.Null, "a pre-Gloas payload needs no envelope verdict and must end the carrier search");
            Assert.That(runner.GetBlockExecutionStatus(headRoot), Is.EqualTo(ExecutionStatus.Valid));
        }
    }

    [Test]
    public void Valid_verdict_promotes_the_head_and_its_ancestors_only()
    {
        GloasForkChoiceHarness harness = new();
        GloasForkChoiceHarness.Block first = ImportVerifiedFirst(harness);
        GloasForkChoiceHarness.Block x = harness.Child(first, first.Slot + 1, full: true, 0xA1);
        GloasForkChoiceHarness.Block sibling = harness.Child(first, first.Slot + 1, full: true, 0xD1);
        harness.Import(x);
        harness.Import(sibling);
        GloasForkChoiceHarness.Block e = harness.Child(x, first.Slot + 2, first.BidBlockHash, 0xE1);
        harness.Import(e);

        harness.Runner.ValidateExecutionChain(e.Root, first.BidBlockHash);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Runner.GetBlockExecutionStatus(e.Root), Is.EqualTo(ExecutionStatus.Valid));
            Assert.That(harness.Runner.GetBlockExecutionStatus(x.Root), Is.EqualTo(ExecutionStatus.Valid));
            Assert.That(harness.Runner.GetBlockExecutionStatus(first.Root), Is.EqualTo(ExecutionStatus.Valid));
            Assert.That(harness.Runner.GetBlockExecutionStatus(sibling.Root), Is.EqualTo(ExecutionStatus.Optimistic));
        }
    }

    // specs/bellatrix/optimistic-sync.md: contradictory verdicts require intervention and must not partially change the tree.
    [Test]
    public void Valid_verdict_on_an_invalid_payload_is_refused_without_promoting_its_block()
    {
        GloasForkChoiceHarness harness = new();
        GloasForkChoiceHarness.Block first = ImportVerifiedFirst(harness);
        harness.Runner.InvalidateExecutionChain(first.Root, first.BidBlockHash, latestValidHash: null);

        Assert.That(() => harness.Runner.ValidateExecutionChain(first.Root, first.BidBlockHash), Throws.TypeOf<ProtoArrayException>());
        Assert.That(harness.Runner.GetBlockExecutionStatus(first.Root), Is.EqualTo(ExecutionStatus.Optimistic));
    }

    // specs/bellatrix/optimistic-sync.md: zero latestValidHash names the first execution block, contradicting a valid anchor.
    [Test]
    public void Zero_latest_valid_hash_is_refused_when_it_would_invalidate_the_valid_anchor()
    {
        GloasForkChoiceHarness harness = new();
        GloasForkChoiceHarness.Block first = ImportVerifiedFirst(harness);

        Assert.That(() => harness.Runner.InvalidateExecutionChain(first.Root, first.BidBlockHash, Hash256.Zero), Throws.TypeOf<ProtoArrayException>());
        Assert.That(harness.Runner.IsPayloadVerified(first.Root), Is.True);
    }

    /// <summary>
    /// specs/bellatrix/optimistic-sync.md: an envelope's payload is VALID once its own verdict says so, or once a VALID block builds on it.
    /// A block made VALID by a child that built EMPTY over its payload leaves that payload unverified.
    /// </summary>
    [Test]
    public void Snapshot_reports_an_envelope_payload_valid_only_when_a_verdict_covers_it([Values] bool ownVerdict)
    {
        GloasForkChoiceHarness harness = new();
        GloasForkChoiceHarness.Block first = ImportVerifiedFirst(harness);
        GloasForkChoiceHarness.Block x = harness.Child(first, first.Slot + 1, full: true, 0xA1);
        harness.Import(x);
        harness.Runner.OnExecutionPayloadVerified(x.Root);
        GloasForkChoiceHarness.Block skipped = harness.Child(x, first.Slot + 2, full: true, 0xB1);
        harness.Import(skipped);
        harness.Runner.OnExecutionPayloadVerified(skipped.Root);
        GloasForkChoiceHarness.Block head = harness.Child(skipped, first.Slot + 3, x.BidBlockHash, 0xE1);
        harness.Import(head);
        harness.Runner.OnExecutionPayloadVerified(head.Root);
        bool PayloadValid(GloasForkChoiceHarness.Block block) => harness.Runner.Snapshot().Nodes.Single(n => n.Root == block.Root).PayloadValid;
        Assert.That(new[] { first, x, skipped, head }.Select(PayloadValid), Is.All.False, "an optimistic envelope import verifies no payload");

        harness.Runner.ValidateExecutionChain(head.Root, ownVerdict ? head.BidBlockHash : x.BidBlockHash);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(PayloadValid(head), Is.EqualTo(ownVerdict));
            Assert.That(harness.Runner.GetBlockExecutionStatus(skipped.Root), Is.EqualTo(ExecutionStatus.Valid), "fixture: the skipped block is VALID");
            Assert.That(PayloadValid(skipped), Is.False, "its child built EMPTY over its payload");
            Assert.That(PayloadValid(x), Is.True, "the payload a VALID block builds on");
            Assert.That(PayloadValid(first), Is.True, "an execution ancestor of a VALID payload");
        }
    }

    private static GloasForkChoiceHarness.Block ImportVerifiedFirst(GloasForkChoiceHarness harness)
    {
        GloasForkChoiceHarness.Block first = harness.First;
        harness.TickTo(first.Slot + 3);
        harness.Import(first);
        harness.Runner.OnExecutionPayloadVerified(first.Root);
        return first;
    }
}
