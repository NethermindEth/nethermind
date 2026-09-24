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
        BeaconStateGloas state = UpgradedAnchor(chain);
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
    /// The bid parent hashes are pruned with the blocks they describe: a finalized chain long enough for the
    /// proto-array to prune (<see cref="ProtoArrayForkChoice.DefaultPruneThreshold"/> nodes) must not keep
    /// answering for blocks fork choice dropped, or the map grows for the life of the node.
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

    /// <summary>The anchor state taken across the fork: Fulu slot processing to the boundary, then the upgrade.</summary>
    private static BeaconStateGloas UpgradedAnchor(ForkCrossingChain chain)
    {
        BeaconStateFulu fulu = chain.AnchorState.Clone();
        SlotProcessing.ProcessSlots(fulu, BoundarySlot, new EpochCache());
        return GloasForkTransition.UpgradeToGloas(fulu, chain.Spec);
    }

    private static void TickToSlot(ForkChoiceRunner runner, ulong slot) =>
        runner.OnTick(runner.GenesisTime + slot * Presets.SecondsPerSlot);

    /// <summary>The weight of <paramref name="root"/> as a fresh <see cref="ForkChoiceRunner.GetHead"/> computes it.</summary>
    private static ulong Weight(ForkChoiceRunner runner, Hash256 root)
    {
        runner.GetHead();
        return runner.Snapshot().Nodes.Single(n => n.Root == root).Weight;
    }
}
