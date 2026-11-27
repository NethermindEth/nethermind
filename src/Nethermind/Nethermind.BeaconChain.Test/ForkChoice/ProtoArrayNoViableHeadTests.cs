// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using static Nethermind.BeaconChain.Test.ForkChoice.ProtoArrayTestBlocks;
using static Nethermind.BeaconChain.Test.ForkChoice.TestHashes;

namespace Nethermind.BeaconChain.Test.ForkChoice;

public class ProtoArrayNoViableHeadTests
{
    [Test]
    public void Leaf_viability_is_refreshed_when_slot_or_checkpoints_change()
    {
        CheckpointRef genesis = new(0, GetRoot(0));
        CheckpointRef first = new(1, GetRoot(0));
        CheckpointRef second = new(2, GetRoot(0));
        ProtoArray tree = new(32, 40);
        tree.OnBlock(CreateProtoBlock(0, GetRoot(0), null, genesis, genesis, ExecutionStatus.Optimistic, GetRoot(0)), 32, genesis, genesis);
        tree.OnBlock(CreateProtoBlock(32, GetRoot(1), GetRoot(0), genesis, genesis, ExecutionStatus.Optimistic, GetRoot(1),
            unrealizedJustified: first), 32, genesis, genesis);

        using System.IDisposable scope = Assert.EnterMultipleScope();
        AssertHead(64, second, genesis, GetRoot(1));
        AssertHead(128, second, genesis, GetRoot(0));
        AssertHead(128, first, genesis, GetRoot(1));
        AssertHead(128, first, new CheckpointRef(1, GetRoot(2)), GetRoot(0));
        AssertHead(128, first, genesis, GetRoot(1));

        void AssertHead(ulong slot, CheckpointRef justified, CheckpointRef finalized, Hash256 expected)
        {
            tree.ApplyScoreChanges(new ScoreDeltas(new long[2], new long[2], new long[2]), justified, finalized,
                JustifiedBalances.FromEffectiveBalances([]), Hash256.Zero, slot);
            Assert.That(tree.FindHead(GetRoot(0), slot, justified, finalized), Is.EqualTo(expected));
        }
    }

    [Test]
    public void Shared_leaf_viability_is_refreshed_after_status_changes_and_pruning([Values(16, 512)] int length)
    {
        CheckpointRef genesis = new(0, GetRoot(0));
        ProtoArray tree = new(32, 40) { PruneThreshold = 0 };
        for (int i = 0; i <= length; i++)
        {
            Hash256 root = GetRoot((ulong)i);
            tree.OnBlock(CreateProtoBlock((ulong)i, root, i == 0 ? null : GetRoot((ulong)i - 1), genesis, genesis,
                ExecutionStatus.Optimistic, root), (ulong)length + 1, genesis, genesis);
        }

        for (int i = 1; i <= length; i++)
        {
            Hash256 root = GetRoot((ulong)(length + i));
            tree.OnBlock(CreateProtoBlock((ulong)length + 1, root, GetRoot((ulong)length), genesis, genesis,
                ExecutionStatus.Invalid, root), (ulong)length + 1, genesis, genesis);
        }

        Hash256 terminalRoot = GetRoot((ulong)length);
        Hash256 childRoot = GetRoot((ulong)length + 1);
        using System.IDisposable scope = Assert.EnterMultipleScope();
        AssertHead(terminalRoot);
        AssertHead(terminalRoot);
        tree.Nodes[tree.Indices[childRoot]].ExecutionStatus = ExecutionStatus.Optimistic;
        AssertHead(childRoot);
        tree.Nodes[tree.Indices[childRoot]].ExecutionStatus = ExecutionStatus.Invalid;
        AssertHead(terminalRoot);
        tree.Prune(GetRoot((ulong)length / 2));
        AssertHead(terminalRoot);
        tree.Nodes[tree.Indices[childRoot]].ExecutionStatus = ExecutionStatus.Valid;
        AssertHead(childRoot);

        void AssertHead(Hash256 expected)
        {
            int count = tree.Nodes.Count;
            tree.ApplyScoreChanges(new ScoreDeltas(new long[count], new long[count], new long[count]), genesis, genesis,
                JustifiedBalances.FromEffectiveBalances([]), Hash256.Zero, (ulong)length + 1);
            Assert.That(tree.FindHead(tree.Nodes[0].Root, (ulong)length + 1, genesis, genesis), Is.EqualTo(expected));
            for (int i = 0; i < tree.Indices[terminalRoot]; i++)
            {
                Assert.That(tree.Nodes[i].BestDescendant, Is.EqualTo(tree.Indices[expected]));
            }
        }
    }

    [Test]
    public void Head_excludes_an_interior_node_whose_only_child_lost_unrealized_justification()
    {
        UnsignedChain chain = UnsignedChain.Create();
        BeaconStateFulu state = chain.Anchor.AnchorState.Clone();
        state.Slot = 4 * ProtoArrayForkChoice.DefaultSlotsPerEpoch + 1;
        state.CurrentJustifiedCheckpoint = new Checkpoint { Epoch = 1, Root = GetRoot(0) };
        state.BlockRoots![4 * ProtoArrayForkChoice.DefaultSlotsPerEpoch] = GetRoot(0);
        for (int i = 0; i < 11; i++)
        {
            state.CurrentEpochParticipation![i] = (byte)(1 << Presets.TimelyTargetFlagIndex);
        }

        CheckpointRef beforeSlashing = CheckpointRef.From(EpochProcessing.ComputeJustificationAndFinalization(state, new EpochCache()).CurrentJustifiedCheckpoint);
        BlockProcessing.ProcessAttesterSlashing(state, chain.DoubleVote([0], 1, GetRoot(1), GetRoot(2)), new EpochCache(), chain.Anchor.Pubkeys, verifySignatures: false);
        CheckpointRef afterSlashing = CheckpointRef.From(EpochProcessing.ComputeJustificationAndFinalization(state, new EpochCache()).CurrentJustifiedCheckpoint);
        CheckpointRef genesis = new(0, GetRoot(0));
        CheckpointRef realized = new(1, GetRoot(0));
        ProtoArrayForkChoice forkChoice = new(0, 0, Hash256.Zero, genesis, genesis, ExecutionStatus.Optimistic, Hash256.Zero);
        ProtoBlock a = ChildOfAnchor(GetRoot(1), realized) with { Slot = state.Slot, UnrealizedJustifiedCheckpoint = beforeSlashing };
        ProtoBlock c = a with { Root = GetRoot(3), ExecutionBlockHash = GetRoot(3) };
        ProtoBlock b = a with { Slot = state.Slot + 1, Root = GetRoot(2), ParentRoot = a.Root, ExecutionBlockHash = GetRoot(2), UnrealizedJustifiedCheckpoint = afterSlashing };
        forkChoice.ProcessBlock(a, state.Slot, realized, genesis);
        forkChoice.ProcessBlock(c, state.Slot, realized, genesis);
        forkChoice.ProcessAttestation(0, a.Root, 4);
        forkChoice.ProcessAttestation(1, c.Root, 4);
        JustifiedBalances balances = JustifiedBalances.FromEffectiveBalances([2, 1]);
        ulong currentSlot = 5 * ProtoArrayForkChoice.DefaultSlotsPerEpoch;
        Assert.That(forkChoice.GetHead(beforeSlashing, genesis, balances, null, currentSlot), Is.EqualTo(a.Root));

        forkChoice.ProcessBlock(b, b.Slot, realized, genesis);
        Assert.That(forkChoice.GetHead(realized, genesis, balances, null, b.Slot), Is.EqualTo(b.Root));

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(beforeSlashing.Epoch, Is.EqualTo(4UL));
        Assert.That(afterSlashing.Epoch, Is.EqualTo(1UL));
        Assert.That(forkChoice.GetHead(beforeSlashing, genesis, balances, null, currentSlot), Is.EqualTo(c.Root));
    }

    [Test]
    public void Head_never_stops_at_an_interior_node_outside_the_viable_tree([Values] bool withViableSibling)
    {
        CheckpointRef genesis = new(0, GetRoot(0));
        CheckpointRef firstJustified = new(1, GetRoot(0));
        CheckpointRef justified = new(5, GetRoot(0));
        ProtoArrayForkChoice forkChoice = new(0, 0, Hash256.Zero, genesis, genesis, ExecutionStatus.Optimistic, Hash256.Zero);
        ProtoBlock y = ChildOfAnchor(GetRoot(1), firstJustified);
        ProtoBlock x = ChildOfAnchor(GetRoot(2), justified);
        ProtoBlock z = ChildOfAnchor(GetRoot(3), firstJustified) with { Slot = 2, ParentRoot = x.Root };
        ProtoBlock v = ChildOfAnchor(GetRoot(4), new CheckpointRef(8, GetRoot(0)));
        forkChoice.ProcessBlock(y, 2, genesis, genesis);
        forkChoice.ProcessBlock(x, 2, genesis, genesis);
        forkChoice.ProcessBlock(z, 2, genesis, genesis);
        if (withViableSibling) forkChoice.ProcessBlock(v, 2, genesis, genesis);
        forkChoice.ProcessAttestation(0, y.Root, 1);
        forkChoice.ProcessAttestation(1, v.Root, 1);
        JustifiedBalances balances = JustifiedBalances.FromEffectiveBalances([2, 1]);
        ulong currentSlot = 10 * ProtoArrayForkChoice.DefaultSlotsPerEpoch;
        Hash256 firstHead = forkChoice.GetHead(firstJustified, genesis, balances, null, currentSlot);

        forkChoice.ProcessAttestation(0, z.Root, 2);
        Hash256 head = forkChoice.GetHead(justified, genesis, balances, null, currentSlot);

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(firstHead, Is.EqualTo(y.Root), "fixture: Y is the best child before it loses viability");
        Assert.That(head, Is.EqualTo(withViableSibling ? v.Root : GetRoot(0)));
    }


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
        CreateProtoBlock(1, root, GetRoot(0), checkpoint, checkpoint, ExecutionStatus.Optimistic, root,
            unrealizedJustified: checkpoint, unrealizedFinalized: checkpoint);
}
