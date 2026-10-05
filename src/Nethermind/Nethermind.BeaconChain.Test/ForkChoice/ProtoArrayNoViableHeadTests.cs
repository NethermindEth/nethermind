// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.ForkChoice.ProtoArrayTestBlocks;
using static Nethermind.BeaconChain.Test.ForkChoice.TestHashes;

namespace Nethermind.BeaconChain.Test.ForkChoice;

public class ProtoArrayNoViableHeadTests
{
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
    public void Head_is_the_parent_once_each_child_is_invalidated_in_turn()
    {
        CheckpointRef genesis = new(0, GetRoot(0));
        ProtoArrayForkChoice forkChoice = new(0, 0, Hash256.Zero, genesis, genesis, ExecutionStatus.Optimistic, Hash256.Zero);
        ProtoBlock a = ChildOfAnchor(GetRoot(1), genesis);
        ProtoBlock b = a with { Slot = 2, Root = GetRoot(2), ParentRoot = a.Root, ExecutionBlockHash = GetRoot(2) };
        ProtoBlock c = b with { Root = GetRoot(3), ExecutionBlockHash = GetRoot(3) };
        forkChoice.ProcessBlock(a, 2, genesis, genesis);
        forkChoice.ProcessBlock(b, 2, genesis, genesis);
        forkChoice.ProcessBlock(c, 2, genesis, genesis);
        JustifiedBalances balances = JustifiedBalances.FromEffectiveBalances([1]);

        Hash256 first = forkChoice.GetHead(genesis, genesis, balances, null, 2);
        forkChoice.ProcessExecutionPayloadInvalidation(InvalidationOperation.InvalidateOne(c.Root), genesis);
        Hash256 second = forkChoice.GetHead(genesis, genesis, balances, null, 2);
        forkChoice.ProcessExecutionPayloadInvalidation(InvalidationOperation.InvalidateOne(b.Root), genesis);
        Hash256 head = forkChoice.GetHead(genesis, genesis, balances, null, 2);

        Assert.That((first, second, head), Is.EqualTo((c.Root, b.Root, a.Root)));
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
