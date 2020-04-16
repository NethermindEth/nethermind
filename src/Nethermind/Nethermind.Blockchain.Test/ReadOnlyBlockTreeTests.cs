﻿//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using FluentAssertions;
using Nethermind.Core.Test.Builders;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    public class ReadOnlyBlockTreeTests
    {
        private IBlockTree _innerBlockTree;
        private ReadOnlyBlockTree _blockTree;

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
        
        [TestCase(10, 20, 15, null, true, TestName = "No corrupted block.")]
        [TestCase(10, 20, 15, 19, true, TestName = "Corrupted block too far.")]
        [TestCase(10, 20, 5, 19, true, TestName = "Start before head.")]
        [TestCase(0, 20, 5, 19, true, TestName = "Head genesis.")]
        [TestCase(null, 20, 5, 19, true, TestName = "Head null.")]
        [TestCase(10, 20, 15, 16, false, TestName = "Allow deletion.")]
        public void DeleteChainSlice_throws_when_corrupted_blocks_not_found(long? head, long bestKnown, long start, long? corruptedBlock, bool throws)
        {
            _innerBlockTree.Head.Returns(head == null ? null : Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(head.Value).TestObject).TestObject);
            _innerBlockTree.BestKnownNumber.Returns(bestKnown);
            _innerBlockTree.FindHeader(Arg.Any<long>(), Arg.Any<BlockTreeLookupOptions>())
                .Returns(c => c.Arg<long>() == corruptedBlock ? null : Build.A.BlockHeader.WithNumber(c.Arg<long>()).TestObject);
            
            Action action = () => _blockTree.DeleteChainSlice(start);
            
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