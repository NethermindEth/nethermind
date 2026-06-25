// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Facade.Simulate;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Facade.Test.Simulate;

[TestFixture]
public class SimulateBlockhashProviderTests
{
    [Test]
    public void Returns_null_when_inner_cannot_resolve_hash()
    {
        IBlockhashProvider inner = Substitute.For<IBlockhashProvider>();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.BestKnownNumber.Returns(100);
        inner.GetBlockhash(Arg.Any<BlockHeader>(), Arg.Any<long>(), Arg.Any<IReleaseSpec>()).Returns((Hash256?)null);

        SimulateBlockhashProvider sut = new(inner, blockTree);

        // eth_simulateV1 is best-effort: an unresolvable ancestor hash must yield 0 (null), not fail the request.
        Assert.That(sut.GetBlockhash(Build.A.BlockHeader.WithNumber(50).TestObject, 40, Substitute.For<IReleaseSpec>()), Is.Null);
    }

    [Test]
    public void Passes_through_inner_hash_in_normal_flow()
    {
        IBlockhashProvider inner = Substitute.For<IBlockhashProvider>();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.BestKnownNumber.Returns(100);
        BlockHeader current = Build.A.BlockHeader.WithNumber(50).TestObject;
        inner.GetBlockhash(current, 40, Arg.Any<IReleaseSpec>()).Returns(TestItem.KeccakA);

        SimulateBlockhashProvider sut = new(inner, blockTree);

        Assert.That(sut.GetBlockhash(current, 40, Substitute.For<IReleaseSpec>()), Is.EqualTo(TestItem.KeccakA));
    }

    [Test]
    public void Clamps_to_best_known_when_requesting_block_beyond_head()
    {
        IBlockhashProvider inner = Substitute.For<IBlockhashProvider>();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.BestKnownNumber.Returns(100);
        BlockHeader bestSuggested = Build.A.BlockHeader.WithNumber(100).TestObject;
        blockTree.BestSuggestedHeader.Returns(bestSuggested);
        inner.GetBlockhash(bestSuggested, 100, Arg.Any<IReleaseSpec>()).Returns(TestItem.KeccakB);

        SimulateBlockhashProvider sut = new(inner, blockTree);

        // Requesting a block beyond best-known (150 > 100) clamps to (BestSuggestedHeader, BestKnownNumber).
        Assert.That(sut.GetBlockhash(Build.A.BlockHeader.WithNumber(151).TestObject, 150, Substitute.For<IReleaseSpec>()), Is.EqualTo(TestItem.KeccakB));
    }
}
