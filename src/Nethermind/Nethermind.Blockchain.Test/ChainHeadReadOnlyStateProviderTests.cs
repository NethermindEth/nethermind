// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Test.Builders;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    public class ChainHeadReadOnlyStateProviderTests
    {
        [Test, Timeout(Timeout.MaxTestTime)]
        public void uses_block_tree_head_state_root()
        {
            BlockTree blockTree = Build.A.BlockTree(Build.A.Block.WithStateRoot(TestItem.KeccakA).TestObject).OfChainLength(10).TestObject;
            ChainHeadReadOnlyStateProvider chainHeadReadOnlyStateProvider = new(blockTree, Substitute.For<IStateReader>());
            chainHeadReadOnlyStateProvider.StateRoot.Should().BeEquivalentTo(blockTree.Head!.StateRoot);
        }
    }
}
