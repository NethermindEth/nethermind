// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core.Test.Builders;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
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
            Action action = () => _blockTree.DeleteChainSlice(0, 10);
            action.Should().Throw<InvalidOperationException>();
        }

        [Timeout(Timeout.MaxTestTime)]
        [TestCase(10, 20, 15, null, false, true, TestName = "No corrupted block.")]
        [TestCase(10, 20, 15, 19, false, true, TestName = "Corrupted block too far.")]
        [TestCase(10, 20, 5, 19, false, true, TestName = "Start before head.")]
        [TestCase(0, 20, 5, 19, false, true, TestName = "Head genesis.")]
        [TestCase(null, 20, 5, 19, false, true, TestName = "Head null.")]
        [TestCase(10, 20, 15, 16, false, false, TestName = "Allow deletion.")]

        [TestCase(10, 20, 15, null, true, false, TestName = "Force - No corrupted block.")]
        [TestCase(10, 20, 15, 19, true, false, TestName = "Force - Corrupted block too far.")]
        [TestCase(10, 20, 5, 19, true, true, TestName = "Force - Start before head.")]
        [TestCase(0, 20, 5, 19, true, true, TestName = "Force - Head genesis.")]
        [TestCase(null, 20, 5, 19, true, true, TestName = "Force - Head null.")]
        public void DeleteChainSlice_throws_when_corrupted_blocks_not_found(long? head, long bestKnown, long start, long? corruptedBlock, bool force, bool throws)
        {
            _innerBlockTree.Head.Returns(head is null ? null : Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(head.Value).TestObject).TestObject);
            _innerBlockTree.BestKnownNumber.Returns(bestKnown);
            _innerBlockTree.FindHeader(Arg.Any<long>(), Arg.Any<BlockTreeLookupOptions>())
                .Returns(c => c.Arg<long>() == corruptedBlock ? null : Build.A.BlockHeader.WithNumber(c.Arg<long>()).TestObject);

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
}
