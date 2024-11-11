// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

public class BlockTreeSuggestPacerTests
{
    [Test]
    public void WillNotBlockIfInBatchLimit()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithNumber(0).TestObject);
        using BlockTreeSuggestPacer pacer = new BlockTreeSuggestPacer(blockTree, 10, 5);

        pacer.WaitForQueue(1, default).IsCompleted.Should().BeTrue();
    }

    [Test]
    public void WillBlockIfBatchTooLarge()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithNumber(0).TestObject);
        using BlockTreeSuggestPacer pacer = new BlockTreeSuggestPacer(blockTree, 10, 5);

        pacer.WaitForQueue(11, default).IsCompleted.Should().BeFalse();
    }

    [Test]
    public void WillOnlyUnblockOnceHeadReachHighEnough()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithNumber(0).TestObject);
        using BlockTreeSuggestPacer pacer = new BlockTreeSuggestPacer(blockTree, 10, 5);

        Task waitTask = pacer.WaitForQueue(11, default);
        waitTask.IsCompleted.Should().BeFalse();

        blockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(Build.A.Block.WithNumber(1).TestObject));
        waitTask.IsCompleted.Should().BeFalse();

        blockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(Build.A.Block.WithNumber(5).TestObject));
        waitTask.IsCompleted.Should().BeFalse();

        blockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(Build.A.Block.WithNumber(6).TestObject));
        waitTask.IsCompleted.Should().BeTrue();
    }
}
