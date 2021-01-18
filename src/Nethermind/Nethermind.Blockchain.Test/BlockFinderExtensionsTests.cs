//  Copyright (c) 2021 Demerzel Solutions Limited
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

using FluentAssertions;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    [TestFixture]
    public class BlockFinderExtensionsTests
    {
        [Test]
        public void Can_upgrade_maybe_parent()
        {
            BlockHeader parent = Build.A.BlockHeader.TestObject;
            BlockHeader parentWithTotalDiff = Build.A.BlockHeader.WithTotalDifficulty(1).TestObject;
            BlockHeader child = Build.A.BlockHeader.WithParent(parent).TestObject;
            parent.TotalDifficulty.Should().BeNull(); // just to avoid the testing rig change without this test being updated
            
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindHeader(child.ParentHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded).Returns(parent);
            blockFinder.FindHeader(child.ParentHash, BlockTreeLookupOptions.None).Returns(parentWithTotalDiff);
            
            blockFinder.FindParentHeader(child, BlockTreeLookupOptions.TotalDifficultyNotNeeded).Should().Be(parent);
            blockFinder.FindParentHeader(child, BlockTreeLookupOptions.None).TotalDifficulty.Should().Be((UInt256?)UInt256.One);
        }
    }
}
