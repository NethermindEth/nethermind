// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.State;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.ParallelSync;

public class FullStateFinderTests
{
    private readonly Hash256 _goodRoot = Keccak.Compute("test");
    private readonly Hash256 _badRoot = Keccak.Compute("test2");

    [Test]
    public void TestWillCheckForState()
    {
        IBlockTree blockTree = Build.A.BlockTree()
            .WithStateRoot((b) => b.Number == 950 ? _goodRoot : _badRoot)
            .OfChainLength(1000)
            .TestObject;

        IStateReader stateReader = Substitute.For<IStateReader>();
        stateReader.HasStateForBlock(Arg.Is<BlockHeader>((header) => header.StateRoot == _goodRoot)).Returns(true);

        FullStateFinder finder = new FullStateFinder(blockTree, stateReader);
        finder.FindBestFullState().Should().Be(950);
    }

    [Test]
    public void TestWillCheckForStateWhenItWasPreviouslyFound()
    {
        IBlockTree blockTree = Build.A.BlockTree()
            .WithStateRoot((b) => b.Number == 50 ? _goodRoot : _badRoot)
            .OfChainLength(100)
            .TestObject;

        IStateReader stateReader = Substitute.For<IStateReader>();
        stateReader.HasStateForBlock(Arg.Is<BlockHeader>((header) => header.StateRoot == _goodRoot)).Returns(true);

        FullStateFinder finder = new FullStateFinder(blockTree, stateReader);
        finder.FindBestFullState().Should().Be(50);

        BlockHeader parent = blockTree.FindHeader(50, BlockTreeLookupOptions.None)!;

        for (int i = 0; i < 500; i++)
        {
            Block block = Build.A.Block
                .WithParent(parent)
                .WithStateRoot(_badRoot)
                .TestObject;

            blockTree.SuggestBlock(block);

            parent = block.Header;
        }

        finder.FindBestFullState().Should().Be(50);
    }

}
