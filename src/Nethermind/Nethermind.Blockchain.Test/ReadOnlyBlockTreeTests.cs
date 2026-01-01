// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core.Test.Builders;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

public class ReadOnlyBlockTreeTests
{
    private IBlockTree _innerBlockTree = null!;
    private ReadOnlyBlockTree _blockTree = null!;

    [SetUp]
    public void SetUp()
    {
        _innerBlockTree = Substitute.For<IBlockTree>();
        _blockTree = new ReadOnlyBlockTree(_innerBlockTree);
    }

    [TestCase]
    public void DeleteChainSlice_throws_when_endNumber_other_than_bestKnownNumber()
    {
        Action action = () => _blockTree.DeleteChainSlice(0ul, 10ul);
        action.Should().Throw<InvalidOperationException>();
    }

    [MaxTime(Timeout.MaxTestTime)]
    [TestCase(10ul, 20ul, 15ul, null, false, true, TestName = "No corrupted block.")]
    [TestCase(10ul, 20ul, 15ul, 19ul, false, true, TestName = "Corrupted block too far.")]
    [TestCase(10ul, 20ul, 5ul, 19ul, false, true, TestName = "Start before head.")]
    [TestCase(0ul, 20ul, 5ul, 19ul, false, true, TestName = "Head genesis.")]
    [TestCase(null, 20ul, 5ul, 19ul, false, true, TestName = "Head null.")]
    [TestCase(10ul, 20ul, 15ul, 16ul, false, false, TestName = "Allow deletion.")]

    [TestCase(10ul, 20ul, 15ul, null, true, false, TestName = "Force - No corrupted block.")]
    [TestCase(10ul, 20ul, 15ul, 19ul, true, false, TestName = "Force - Corrupted block too far.")]
    [TestCase(10ul, 20ul, 5ul, 19ul, true, true, TestName = "Force - Start before head.")]
    [TestCase(0ul, 20ul, 5ul, 19ul, true, true, TestName = "Force - Head genesis.")]
    [TestCase(null, 20ul, 5ul, 19ul, true, true, TestName = "Force - Head null.")]
    public void DeleteChainSlice_throws_when_corrupted_blocks_not_found(ulong? head, ulong bestKnown, ulong start, ulong? corruptedBlock, bool force, bool throws)
    {
        _innerBlockTree.Head.Returns(head is null ? null : Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(head.Value).TestObject).TestObject);
        _innerBlockTree.BestKnownNumber.Returns(bestKnown);
        _innerBlockTree.FindHeader(Arg.Any<ulong>(), Arg.Any<BlockTreeLookupOptions>())
            .Returns(c => c.Arg<ulong>() == corruptedBlock ? null : Build.A.BlockHeader.WithNumber(c.Arg<ulong>()).TestObject);

        Action action = () => _blockTree.DeleteChainSlice(start, force: force);

        if (throws)
        {
            action.Should().Throw<InvalidOperationException>();
        }
        else
        {
            action.Should().NotThrow<InvalidOperationException>();
        }
    }
}
