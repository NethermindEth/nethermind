// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

[Parallelizable(ParallelScope.All)]
public class ChainHeadInfoProviderTests
{
    private const ulong HeadGasLimit = 30_000_000;

    [TestCase(9_999_999UL, 12_000_000UL, TestName = "seeds_from_the_processed_head_while_syncing")]
    [TestCase(9_999_999UL, 9_999_999UL, TestName = "seeds_from_the_processed_head_when_synced")]
    public void Head_number_is_seeded_from_the_processed_head(ulong headNumber, ulong bestKnownNumber)
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithNumber(headNumber).WithGasLimit(HeadGasLimit).TestObject);
        blockTree.BestKnownNumber.Returns(bestKnownNumber);

        Assert.That(CreateProvider(blockTree).HeadNumber, Is.EqualTo(headNumber));
    }

    [Test]
    public void Head_number_is_zero_on_a_fresh_database()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns((Block?)null);
        blockTree.BestKnownNumber.Returns(12_000_000UL);

        ChainHeadInfoProvider provider = CreateProvider(blockTree);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.HeadNumber, Is.Zero);
            Assert.That(provider.BlockGasLimit, Is.Null);
        }
    }

    [Test]
    public void Head_facts_are_seeded_from_a_processed_head()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithNumber(9_999_999).WithGasLimit(HeadGasLimit).WithBaseFeePerGas(7).TestObject);

        ChainHeadInfoProvider provider = CreateProvider(blockTree);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.BlockGasLimit, Is.EqualTo(HeadGasLimit));
            Assert.That(provider.CurrentBaseFee, Is.EqualTo((UInt256)7));
        }
    }

    [Test]
    public void Head_facts_are_not_seeded_from_a_genesis_head()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithNumber(0).WithGasLimit(5_000).WithBaseFeePerGas(7).TestObject);

        ChainHeadInfoProvider provider = CreateProvider(blockTree);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.HeadNumber, Is.Zero);
            Assert.That(provider.BlockGasLimit, Is.Null);
            Assert.That(provider.CurrentBaseFee, Is.EqualTo(UInt256.Zero));
        }
    }

    private static ChainHeadInfoProvider CreateProvider(IBlockTree blockTree)
    {
        IChainHeadSpecProvider specProvider = Substitute.For<IChainHeadSpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(Cancun.Instance);
        return new ChainHeadInfoProvider(specProvider, blockTree, Substitute.For<IReadOnlyStateProvider>());
    }
}
