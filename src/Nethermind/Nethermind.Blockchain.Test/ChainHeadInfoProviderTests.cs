// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

public class ChainHeadInfoProviderTests
{
    [TestCase(9_999_999UL, 12_000_000UL, 9_999_999UL, TestName = "seeds_from_the_processed_head_while_syncing")]
    [TestCase(9_999_999UL, 9_999_999UL, 9_999_999UL, TestName = "seeds_from_the_processed_head_when_synced")]
    public void Head_number_is_seeded_from_the_processed_head(ulong headNumber, ulong bestKnownNumber, ulong expected)
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithNumber(headNumber).TestObject);
        blockTree.BestKnownNumber.Returns(bestKnownNumber);

        Assert.That(CreateProvider(blockTree).HeadNumber, Is.EqualTo(expected));
    }

    [Test]
    public void Head_number_is_zero_on_a_fresh_database()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns((Block?)null);
        blockTree.BestKnownNumber.Returns(12_000_000UL);

        Assert.That(CreateProvider(blockTree).HeadNumber, Is.Zero);
    }

    private static ChainHeadInfoProvider CreateProvider(IBlockTree blockTree) =>
        new(Substitute.For<IChainHeadSpecProvider>(), blockTree, Substitute.For<IReadOnlyStateProvider>());
}
