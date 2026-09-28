// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.ForkChoice;
using Nethermind.Core.Crypto;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.ForkChoice.TestHashes;

namespace Nethermind.BeaconChain.Test.ForkChoice;

public class ProtoArrayNoViableHeadTests
{
    /// <summary>
    /// specs/phase0/fork-choice.md get_head starts at the justified root and walks only get_filtered_block_tree, so when
    /// no node is viable the head is the justified root. Here the store's justified epoch 5 is more than two epochs
    /// ahead of every node's voting source (epoch 0) at epoch 10, so no node is viable.
    /// </summary>
    /// <remarks>
    /// With siblings, a vote on the lower-root child makes it the heavier one, and the best-child update keeps a
    /// non-viable best child when both siblings are non-viable, so the justified node's best descendant is not viable.
    /// </remarks>
    [Test]
    public void Head_is_the_justified_root_when_no_node_is_viable([Values] bool withSiblings)
    {
        CheckpointRef genesis = new(0, GetRoot(0));
        CheckpointRef justified = new(5, GetRoot(0));
        ProtoArrayForkChoice forkChoice = new(0, 0, Hash256.Zero, genesis, genesis, ExecutionStatus.Optimistic, Hash256.Zero);
        if (withSiblings) forkChoice.ProcessBlock(ChildOfAnchor(GetRoot(2), genesis), 1, genesis, genesis);
        forkChoice.ProcessBlock(ChildOfAnchor(GetRoot(1), genesis), 1, genesis, genesis);
        forkChoice.ProcessAttestation(0, GetRoot(1), 0);

        Hash256 head = forkChoice.GetHead(justified, genesis, JustifiedBalances.FromEffectiveBalances([1]), null, 10 * ProtoArrayForkChoice.DefaultSlotsPerEpoch);

        Assert.That(head, Is.EqualTo(GetRoot(0)));
    }

    private static ProtoBlock ChildOfAnchor(Hash256 root, CheckpointRef checkpoint) =>
        new(
            Slot: 1,
            Root: root,
            ParentRoot: GetRoot(0),
            StateRoot: Hash256.Zero,
            JustifiedCheckpoint: checkpoint,
            FinalizedCheckpoint: checkpoint,
            ExecutionStatus: ExecutionStatus.Optimistic,
            ExecutionBlockHash: root,
            UnrealizedJustifiedCheckpoint: checkpoint,
            UnrealizedFinalizedCheckpoint: checkpoint);
}
