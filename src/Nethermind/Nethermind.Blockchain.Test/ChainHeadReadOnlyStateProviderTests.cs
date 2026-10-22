// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

[Parallelizable(ParallelScope.All)]
public class ChainHeadReadOnlyStateProviderTests
{
    [Test, MaxTime(Timeout.MaxTestTime)]
    public void uses_block_tree_head_state_root()
    {
        BlockTree blockTree = Build.A.BlockTree(Build.A.Block.WithStateRoot(TestItem.KeccakA).TestObject).OfChainLength(10).TestObject;
        ChainHeadReadOnlyStateProvider chainHeadReadOnlyStateProvider = new(blockTree, Substitute.For<IStateReader>());
        Assert.That(chainHeadReadOnlyStateProvider.StateRoot, Is.EqualTo(blockTree.Head!.StateRoot));
    }
}
