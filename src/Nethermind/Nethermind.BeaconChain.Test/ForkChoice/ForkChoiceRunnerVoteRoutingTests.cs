// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;

namespace Nethermind.BeaconChain.Test.ForkChoice;

/// <summary>
/// specs/gloas/fork-choice.md <c>update_latest_messages</c> and <c>get_supported_node</c> through
/// <see cref="ForkChoiceRunner.OnAttestation(AttestationGloas, bool, bool)"/>: a Gloas-slot vote for a block from an
/// earlier slot supports its FULL node when <c>data.index</c> is 1 and its EMPTY node when it is 0; a vote in the
/// block's own slot, or in a pre-Gloas slot, supports its PENDING node only.
/// </summary>
public class ForkChoiceRunnerVoteRoutingTests
{
    private const ulong EffectiveBalance = 32 * Gwei;

    /// <summary>With 2048 validators and 32 slots, each slot has one committee of 64.</summary>
    private const ulong CommitteeSize = 64;

    private static IEnumerable<TestCaseData> Votes()
    {
        foreach (bool queued in new[] { false, true })
        {
            string source = queued ? ", queued from gossip" : ", from a block";
            yield return new TestCaseData(false, 1, 1ul, ForkChoicePayloadStatus.Full, queued).SetName($"Gloas slot after the block, index 1: FULL{source}");
            yield return new TestCaseData(false, 1, 0ul, ForkChoicePayloadStatus.Empty, queued).SetName($"Gloas slot after the block, index 0: EMPTY{source}");
            yield return new TestCaseData(false, 0, 0ul, ForkChoicePayloadStatus.Pending, queued).SetName($"Gloas slot of the block: PENDING{source}");
            yield return new TestCaseData(true, -1, 0ul, ForkChoicePayloadStatus.Pending, queued).SetName($"Pre-Gloas slot after the block: PENDING{source}");
        }
    }

    /// <summary>
    /// specs/gloas/fork-choice.md <c>is_parent_node_full</c>: a Gloas block builds on its parent's FULL node exactly when its
    /// bid's <c>parent_block_hash</c> is the parent bid's <c>block_hash</c>; without that, every vote is routed as if FULL.
    /// </summary>
    [Test]
    public void Gloas_blocks_record_whether_they_build_on_their_parents_full_or_empty_payload([Values] bool full)
    {
        SignedGloasChain chain = new();
        ulong forkSlot = ForkCrossingChain.ForkEpoch * Presets.SlotsPerEpoch;
        SignedGloasChain.Block parent = chain.Next(null, forkSlot, full: false, 0xA1);
        SignedGloasChain.Block child = chain.Next(parent, forkSlot + 1, full, 0xB1);
        ForkChoiceRunner runner = ForkCrossingChain.Instance.CreateRunner();
        runner.OnTick(runner.GenesisTime + (forkSlot + 1) * Presets.SecondsPerSlot);
        runner.OnBlock(parent.Signed, parent.PostState);
        runner.OnExecutionPayloadVerified(parent.Root);
        runner.OnBlock(child.Signed, child.PostState);

        ProtoNode parentNode = runner.EnumerateAncestors(parent.Root).First();
        ProtoNode childNode = runner.EnumerateAncestors(child.Root).First();
        using (Assert.EnterMultipleScope())
        {
            Assert.That((parentNode.IsGloas, childNode.IsGloas), Is.EqualTo((true, true)));
            Assert.That(child.Bid.ParentBlockHash == parent.Bid.BlockHash, Is.EqualTo(full), "fixture: the child's bid names its parent's payload only when full");
            Assert.That(childNode.ParentPayloadStatus, Is.EqualTo(full ? ForkChoicePayloadStatus.Full : ForkChoicePayloadStatus.Empty));
        }
    }

    [TestCaseSource(nameof(Votes))]
    public void Vote_is_counted_in_the_node_it_supports(bool anchorVote, int slotAfterBoundary, ulong index, ForkChoicePayloadStatus supported, bool queuedFromGossip)
    {
        ulong slot = (ulong)((long)BoundarySlot + slotAfterBoundary);
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkChoiceRunner runner = chain.CreateRunner();
        void TickToSlot(ulong s) => runner.OnTick(runner.GenesisTime + s * Presets.SecondsPerSlot);

        // A gossip vote from the current slot waits in the queue until the next slot.
        TickToSlot(queuedFromGossip ? slot : BoundarySlot + 2);
        if (!anchorVote)
        {
            runner.OnBlock(chain.First.Block, chain.First.PostState);
            runner.OnExecutionPayloadVerified(chain.First.Root);
        }

        Hash256 root = anchorVote ? chain.AnchorRoot : chain.First.Root;
        ulong targetEpoch = slot / Presets.SlotsPerEpoch;
        AttestationData data = new()
        {
            Slot = slot,
            Index = index,
            BeaconBlockRoot = root,
            Source = new Checkpoint { Epoch = 0, Root = chain.AnchorRoot },
            Target = new Checkpoint { Epoch = targetEpoch, Root = root },
        };
        AttestationGloas attestation = CommitteeAttestation(
            chain.First.PostState, data, new EpochCache().GetCommitteeCache(chain.First.PostState, targetEpoch), 0, sign: false);

        runner.OnAttestation(attestation, isFromBlock: !queuedFromGossip, verifySignature: false);
        TickToSlot(BoundarySlot + 2);
        runner.GetHead();

        ProtoNode node = runner.EnumerateAncestors(root).First();
        const ulong weight = CommitteeSize * EffectiveBalance;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Weight, Is.EqualTo(weight));
            Assert.That(node.EmptyWeight, Is.EqualTo(supported == ForkChoicePayloadStatus.Empty ? weight : 0));
            Assert.That(node.FullWeight, Is.EqualTo(supported == ForkChoicePayloadStatus.Full ? weight : 0));
        }
    }
}
