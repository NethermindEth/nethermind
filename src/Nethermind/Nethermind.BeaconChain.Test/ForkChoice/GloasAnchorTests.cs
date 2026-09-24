// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;

namespace Nethermind.BeaconChain.Test.ForkChoice;

/// <summary>
/// The Gloas <c>get_forkchoice_store</c> rooted at <see cref="ForkCrossingChain.First"/>, the state a node
/// checkpoint-synced after the fork finalizes starts from.
/// </summary>
public class GloasAnchorTests
{
    [Test]
    public void The_anchor_is_the_justified_and_finalized_checkpoint_at_its_own_epoch_and_carries_the_bid_block_hash()
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;

        ForkChoiceRunner runner = CreateRunner(chain, chain.Spec, chain.First.PostState, chain.First.Block.Message!);

        CheckpointRef anchor = new(ForkCrossingChain.ForkEpoch, chain.First.Root);
        ProtoNode node = runner.EnumerateAncestors(chain.First.Root).First();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(runner.JustifiedCheckpoint, Is.EqualTo(anchor));
            Assert.That(runner.FinalizedCheckpoint, Is.EqualTo(anchor));
            Assert.That(runner.CurrentSlot, Is.EqualTo(chain.First.Block.Message!.Slot));
            Assert.That(node.ExecutionStatus, Is.EqualTo(ExecutionStatus.Valid), "a finalized anchor must never be invalidated");
            Assert.That(node.ExecutionBlockHash, Is.EqualTo(chain.First.Block.Message!.Body!.SignedExecutionPayloadBid!.Message!.BlockHash),
                "the invalidation walk maps a latest valid hash to Gloas roots through the bid's block_hash");
            Assert.That(runner.GetHead(), Is.EqualTo(chain.First.Root));
        }
    }

    /// <summary>
    /// The anchor is the justified root, so the anchor's own checkpoint state weighs every vote; it resolves only
    /// through the Gloas provider. The children build on the anchor's empty payload, so no payload is needed.
    /// </summary>
    [Test]
    public void Children_import_onto_the_anchor_and_their_votes_are_weighed_from_its_state()
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = CreateRunner(chain, chain.Spec, chain.First.PostState, chain.First.Block.Message!);
        runner.OnTick(runner.GenesisTime + (3 * Presets.SlotsPerEpoch) * chain.Spec.SecondsPerSlot);

        foreach (ForkCrossingChain.ChainBlock block in chain.Voting)
        {
            runner.OnBlock(block.Block, block.PostState);
            foreach (AttestationGloas attestation in block.Block.Message!.Body!.Attestations!)
            {
                runner.OnAttestation(attestation, isFromBlock: true, verifySignature: false);
            }
        }

        Hash256 head = runner.GetHead();
        ulong firstWeight = runner.Snapshot().Nodes.Single(n => n.Root == chain.First.Root).Weight;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(head, Is.EqualTo(chain.Voting[^1].Root));
            Assert.That(firstWeight, Is.EqualTo((ulong)(ForkCrossingChain.VotingSlotCount * chain.Committee32.Length) * chain.First.PostState.Validators![0].EffectiveBalance));
        }
    }

    [Test]
    public void An_anchor_state_that_is_not_the_anchor_block_post_state_is_refused()
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;

        ForkChoiceException ex = Assert.Throws<ForkChoiceException>(
            () => CreateRunner(chain, chain.Spec, chain.Voting[0].PostState, chain.First.Block.Message!))!;

        Assert.That(ex.Message, Does.Contain("state root"));
    }

    /// <summary>Every block state lookup picks the provider by the block's slot, so an anchor of the wrong fork could never resolve its own checkpoint state.</summary>
    [Test]
    public void An_anchor_block_outside_the_fork_of_the_constructor_is_refused([Values] bool gloasConstructor)
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        PubkeyCache pubkeys = new();
        pubkeys.Build(chain.AnchorState.Validators!);

        // Moving the fork epoch one epoch away puts each anchor on the wrong side of it.
        ForkChoiceException ex = Assert.Throws<ForkChoiceException>(() => _ = gloasConstructor
            ? CreateRunner(chain, SyntheticSpec(ForkCrossingChain.ForkEpoch + 1), chain.First.PostState, chain.First.Block.Message!)
            : new ForkChoiceRunner(SyntheticSpec(0), chain.AnchorState, chain.AnchorBlock, chain, pubkeys, chain))!;

        Assert.That(ex.Message, Does.Contain(gloasConstructor ? "before the Gloas fork" : "Gloas epoch"));
    }

    private static ForkChoiceRunner CreateRunner(ForkCrossingChain chain, BeaconChainSpec spec, BeaconStateGloas anchorState, BeaconBlockGloas anchorBlock)
    {
        PubkeyCache pubkeys = new();
        pubkeys.Build(anchorState.Validators!);
        return new ForkChoiceRunner(spec, anchorState, anchorBlock, chain, pubkeys, chain);
    }
}
