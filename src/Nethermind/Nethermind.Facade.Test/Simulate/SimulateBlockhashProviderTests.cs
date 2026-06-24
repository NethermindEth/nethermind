// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Facade.Simulate;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Facade.Test.Simulate;

[TestFixture]
public class SimulateBlockhashProviderTests
{
    [Test]
    public void Returns_null_when_inner_cannot_resolve_hash()
    {
        FakeBlockhashProvider inner = new(resolvable: false, hash: null);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.BestKnownNumber.Returns(100);

        SimulateBlockhashProvider sut = new(inner, blockTree, LimboLogs.Instance);

        // eth_simulateV1 is best-effort: an unresolvable ancestor hash must yield 0 (null), not bubble up.
        Assert.That(sut.GetBlockhash(Build.A.BlockHeader.WithNumber(50).TestObject, 40, Substitute.For<IReleaseSpec>()), Is.Null);
    }

    [Test]
    public void Passes_through_inner_hash_in_normal_flow()
    {
        FakeBlockhashProvider inner = new(resolvable: true, hash: TestItem.KeccakA);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.BestKnownNumber.Returns(100);
        BlockHeader current = Build.A.BlockHeader.WithNumber(50).TestObject;

        SimulateBlockhashProvider sut = new(inner, blockTree, LimboLogs.Instance);

        Assert.That(sut.GetBlockhash(current, 40, Substitute.For<IReleaseSpec>()), Is.EqualTo(TestItem.KeccakA));
        Assert.That(inner.LastHeader, Is.SameAs(current));
        Assert.That(inner.LastNumber, Is.EqualTo(40));
    }

    [Test]
    public void Clamps_to_best_known_when_requesting_block_beyond_head()
    {
        FakeBlockhashProvider inner = new(resolvable: true, hash: TestItem.KeccakB);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.BestKnownNumber.Returns(100);
        BlockHeader bestSuggested = Build.A.BlockHeader.WithNumber(100).TestObject;
        blockTree.BestSuggestedHeader.Returns(bestSuggested);

        SimulateBlockhashProvider sut = new(inner, blockTree, LimboLogs.Instance);

        // Requesting a block beyond best-known (150 > 100) clamps to (BestSuggestedHeader, BestKnownNumber).
        Assert.That(sut.GetBlockhash(Build.A.BlockHeader.WithNumber(151).TestObject, 150, Substitute.For<IReleaseSpec>()), Is.EqualTo(TestItem.KeccakB));
        Assert.That(inner.LastHeader, Is.SameAs(bestSuggested));
        Assert.That(inner.LastNumber, Is.EqualTo(100));
    }

    private sealed class FakeBlockhashProvider(bool resolvable, Hash256? hash) : IBlockhashProvider
    {
        public BlockHeader? LastHeader { get; private set; }
        public long LastNumber { get; private set; }

        public Hash256? GetBlockhash(BlockHeader currentBlock, long number, IReleaseSpec spec) =>
            TryGetBlockhash(currentBlock, number, spec, out Hash256? result) ? result : throw new System.IO.InvalidDataException();

        public bool TryGetBlockhash(BlockHeader currentBlock, long number, IReleaseSpec spec, out Hash256? result)
        {
            LastHeader = currentBlock;
            LastNumber = number;
            result = resolvable ? hash : null;
            return resolvable;
        }

        public Task Prefetch(BlockHeader currentBlock, CancellationToken token) => Task.CompletedTask;
    }
}
