// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

[Parallelizable(ParallelScope.All)]
public class BlockTreePersistedStateRecoveryTests
{
    [Test]
    public void Suggest_WhenPersistedStateAheadOfHead_AbsorbsGapBlocksAndFastForwardsHead()
    {
        PersistedStateRecoverySetup setup = new(persistedNumber: 8, persistedRoot: null);

        foreach (Block block in setup.GapBlocks)
        {
            Assert.That(setup.Tree.SuggestBlock(block), Is.EqualTo(AddBlockResult.Added));
        }

        Assert.That(setup.NewBestSuggestedBlockCount, Is.Zero);
        Assert.That(setup.Tree.Head!.Number, Is.EqualTo(8L));
        Assert.That(setup.Tree.Head!.Hash, Is.EqualTo(setup.GapBlocks[^1].Hash));
    }

    [Test]
    public void Suggest_WhenJunctionStateRootMismatches_RaisesNewBestSuggestedBlock()
    {
        PersistedStateRecoverySetup setup = new(persistedNumber: 8, persistedRoot: TestItem.KeccakB);

        foreach (Block block in setup.GapBlocks)
        {
            setup.Tree.SuggestBlock(block);
        }

        Assert.That(setup.NewBestSuggestedBlockCount, Is.EqualTo(1));
        Assert.That(setup.Tree.Head!.Number, Is.EqualTo(4L));
    }

    [Test]
    public void Suggest_WhenHeadAtOrAbovePersistedState_SuggestsNormally()
    {
        PersistedStateRecoverySetup setup = new(persistedNumber: 3, persistedRoot: TestItem.KeccakA);

        foreach (Block block in setup.GapBlocks)
        {
            setup.Tree.SuggestBlock(block);
        }

        Assert.That(setup.NewBestSuggestedBlockCount, Is.EqualTo(setup.GapBlocks.Length));
        Assert.That(setup.Tree.Head!.Number, Is.EqualTo(4L));
    }

    private sealed class PersistedStateRecoverySetup
    {
        public BlockTree Tree { get; }
        public Block[] GapBlocks { get; }
        public int NewBestSuggestedBlockCount { get; private set; }

        // Tree head is block 4; gap blocks 5..8 mirror the crash-gap shape where the state
        // backend has persisted state ahead of the head. A null persistedRoot means the
        // persisted state matches the junction block's state root.
        public PersistedStateRecoverySetup(ulong persistedNumber, Hash256? persistedRoot)
        {
            BlockTreeBuilder builder = Build.A.BlockTree().OfChainLength(5);
            Tree = builder.TestObject;

            Block parent = Tree.Head!;
            GapBlocks = new Block[4];
            for (int i = 0; i < GapBlocks.Length; i++)
            {
                GapBlocks[i] = Build.A.Block.WithNumber(parent.Number + 1).WithParent(parent).TestObject;
                parent = GapBlocks[i];
            }

            builder.StateBoundary.BestPersistedState = persistedNumber;
            builder.StateBoundary.BestPersistedStateRoot = persistedRoot ?? GapBlocks[^1].StateRoot!;
            Tree.NewBestSuggestedBlock += (_, _) => NewBestSuggestedBlockCount++;
        }
    }
}
