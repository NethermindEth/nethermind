// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Facade.Simulate;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Facade.Test.Simulate;

[TestFixture]
public class SimulateBlockhashProviderTests
{
    private static IEnumerable<Hash256?> InnerResults()
    {
        yield return null;             // unresolvable ancestor -> 0
        yield return TestItem.KeccakA; // resolved hash
    }

    [TestCaseSource(nameof(InnerResults))]
    public void Forwards_request_and_returns_inner_result(Hash256? innerResult)
    {
        IBlockhashProvider inner = Substitute.For<IBlockhashProvider>();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.BestKnownNumber.Returns(100ul);
        BlockHeader current = Build.A.BlockHeader.WithNumber(50).TestObject;
        inner.GetBlockhash(current, 40, Arg.Any<IReleaseSpec>()).Returns(innerResult);

        SimulateBlockhashProvider sut = new(inner, blockTree);

        Assert.That(sut.GetBlockhash(current, 40, Substitute.For<IReleaseSpec>()), Is.EqualTo(innerResult));
    }

    [Test]
    public void Clamps_to_best_known_when_requesting_block_beyond_head()
    {
        IBlockhashProvider inner = Substitute.For<IBlockhashProvider>();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.BestKnownNumber.Returns(100ul);
        BlockHeader bestSuggested = Build.A.BlockHeader.WithNumber(100).TestObject;
        blockTree.BestSuggestedHeader.Returns(bestSuggested);
        inner.GetBlockhash(bestSuggested, 100, Arg.Any<IReleaseSpec>()).Returns(TestItem.KeccakB);

        SimulateBlockhashProvider sut = new(inner, blockTree);

        // Requesting 150 (> best-known 100) clamps to (BestSuggestedHeader, BestKnownNumber).
        Assert.That(sut.GetBlockhash(Build.A.BlockHeader.WithNumber(151).TestObject, 150, Substitute.For<IReleaseSpec>()), Is.EqualTo(TestItem.KeccakB));
    }

    [Test]
    public void TryGetBlockhash_delegates_to_the_inner_provider()
    {
        RecordingInner inner = new() { Hash = TestItem.KeccakA };
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.BestKnownNumber.Returns(100ul);
        BlockHeader current = Build.A.BlockHeader.WithNumber(50).TestObject;

        SimulateBlockhashProvider sut = new(inner, blockTree);

        Assert.That(sut.TryGetBlockhash(current, 40, Substitute.For<IReleaseSpec>(), out ReadOnlySpan<byte> hash), Is.True);
        Assert.That(hash, Is.SequenceEqualTo(TestItem.KeccakA.Bytes));
        Assert.That(inner.TryGetBlockhashCalledWith, Is.EqualTo((current, 40ul)));
    }

    [Test]
    public void TryGetBlockhash_clamps_to_best_known_when_requesting_block_beyond_head()
    {
        RecordingInner inner = new() { Hash = TestItem.KeccakB };
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.BestKnownNumber.Returns(100ul);
        BlockHeader bestSuggested = Build.A.BlockHeader.WithNumber(100).TestObject;
        blockTree.BestSuggestedHeader.Returns(bestSuggested);

        SimulateBlockhashProvider sut = new(inner, blockTree);

        Assert.That(sut.TryGetBlockhash(Build.A.BlockHeader.WithNumber(151).TestObject, 150, Substitute.For<IReleaseSpec>(), out ReadOnlySpan<byte> hash), Is.True);
        Assert.That(hash, Is.SequenceEqualTo(TestItem.KeccakB.Bytes));
        Assert.That(inner.TryGetBlockhashCalledWith, Is.EqualTo((bestSuggested, 100ul)));
    }

    /// <summary>Concrete inner: NSubstitute cannot proxy the <c>out ReadOnlySpan&lt;byte&gt;</c> default
    /// interface method (a ref struct), so the tests that touch it use this instead.</summary>
    private sealed class RecordingInner : IBlockhashProvider
    {
        public (BlockHeader Header, ulong Number)? TryGetBlockhashCalledWith;
        public Hash256? Hash;

        public Hash256? GetBlockhash(BlockHeader currentBlock, ulong number, IReleaseSpec spec) => Hash;

        public bool TryGetBlockhash(BlockHeader currentBlock, ulong number, IReleaseSpec spec, out ReadOnlySpan<byte> hash)
        {
            TryGetBlockhashCalledWith = (currentBlock, number);
            hash = Hash is null ? default : Hash.Bytes;
            return Hash is not null;
        }

        public Task Prefetch(BlockHeader currentBlock, CancellationToken token) => Task.CompletedTask;
    }
}
