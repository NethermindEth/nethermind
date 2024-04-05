// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        [Test, Timeout(Timeout.MaxTestTime)]
        public void Can_upgrade_maybe_parent()
        {
            BlockHeader parent = Build.A.BlockHeader.TestObject;
            BlockHeader parentWithTotalDiff = Build.A.BlockHeader.WithTotalDifficulty(1).TestObject;
            BlockHeader child = Build.A.BlockHeader.WithParent(parent).TestObject;
            parent.TotalDifficulty.Should().BeNull(); // just to avoid the testing rig change without this test being updated

            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindHeader(child.ParentHash!, BlockTreeLookupOptions.TotalDifficultyNotNeeded, blockNumber: child.Number - 1).Returns(parent);
            blockFinder.FindHeader(child.ParentHash!, BlockTreeLookupOptions.None, blockNumber: child.Number - 1).Returns(parentWithTotalDiff);

            blockFinder.FindParentHeader(child, BlockTreeLookupOptions.TotalDifficultyNotNeeded).Should().Be(parent);
            blockFinder.FindParentHeader(child, BlockTreeLookupOptions.None)!.TotalDifficulty.Should().Be((UInt256?)UInt256.One);
        }
    }
}
