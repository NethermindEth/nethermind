// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;

namespace Nethermind.BeaconChain.Test.ForkChoice;

[HardTimeout(60_000)]
public class ForkChoiceRunnerVoteRoutingTests
{
    private const ulong EffectiveBalance = 32 * Gwei;

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
        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That((parentNode.IsGloas, childNode.IsGloas), Is.EqualTo((true, true)));
        Assert.That(child.Bid.ParentBlockHash == parent.Bid.BlockHash, Is.EqualTo(full), "fixture: the child's bid names its parent's payload only when full");
        Assert.That(childNode.ParentPayloadStatus, Is.EqualTo(full ? ForkChoicePayloadStatus.Full : ForkChoicePayloadStatus.Empty));
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
        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(node.Weight, Is.EqualTo(weight));
        Assert.That(node.EmptyWeight, Is.EqualTo(supported == ForkChoicePayloadStatus.Empty ? weight : 0));
        Assert.That(node.FullWeight, Is.EqualTo(supported == ForkChoicePayloadStatus.Full ? weight : 0));
    }
}
