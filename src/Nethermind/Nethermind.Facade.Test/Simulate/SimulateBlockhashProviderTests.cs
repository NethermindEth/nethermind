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
    public void TryGetBlockhash_on_the_7709_path_bypasses_the_inner_memo()
    {
        RecordingInner inner = new() { StateHash = TestItem.KeccakA };
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.BestKnownNumber.Returns(100ul);
        BlockHeader current = Build.A.BlockHeader.WithNumber(50).TestObject;
        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        spec.IsBlockHashInStateAvailable.Returns(true);

        SimulateBlockhashProvider sut = new(inner, blockTree);

        Assert.That(sut.TryGetBlockhash(current, 40, spec, out ReadOnlySpan<byte> hash), Is.True);
        Assert.That(hash.ToArray(), Is.EqualTo(TestItem.KeccakA.Bytes.ToArray()));
        // The memo-bearing overload must never be consulted: the simulate scope reuses one provider across
        // virtual blocks whose overridden states differ, so a memoized value would leak between them. The
        // allocating overload reads the same store with no memo, which is why it is the one used here.
        Assert.That(inner.TryGetBlockhashCalled, Is.False);
        Assert.That(inner.GetBlockhashCalledWith, Is.EqualTo((current, 40ul)));
    }

    [Test]
    public void TryGetBlockhash_off_the_7709_path_delegates_to_the_inner_provider()
    {
        RecordingInner inner = new();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.BestKnownNumber.Returns(100ul);
        BlockHeader current = Build.A.BlockHeader.WithNumber(50).TestObject;
        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        spec.IsBlockHashInStateAvailable.Returns(false);

        SimulateBlockhashProvider sut = new(inner, blockTree);

        sut.TryGetBlockhash(current, 40, spec, out _);

        Assert.That(inner.TryGetBlockhashCalled, Is.True, "the off-path must delegate to the inner provider");
        Assert.That(inner.GetBlockhashCalledWith, Is.Null);
    }

    [Test]
    public void TryGetBlockhash_clamps_to_best_known_on_the_7709_path()
    {
        RecordingInner inner = new() { StateHash = TestItem.KeccakB };
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.BestKnownNumber.Returns(100ul);
        BlockHeader bestSuggested = Build.A.BlockHeader.WithNumber(100).TestObject;
        blockTree.BestSuggestedHeader.Returns(bestSuggested);
        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        spec.IsBlockHashInStateAvailable.Returns(true);

        SimulateBlockhashProvider sut = new(inner, blockTree);

        // 150 > best-known 100 clamps to (BestSuggestedHeader, BestKnownNumber) on this overload too.
        Assert.That(sut.TryGetBlockhash(Build.A.BlockHeader.WithNumber(151).TestObject, 150, spec, out ReadOnlySpan<byte> hash), Is.True);
        Assert.That(hash.ToArray(), Is.EqualTo(TestItem.KeccakB.Bytes.ToArray()));
        Assert.That(inner.GetBlockhashCalledWith, Is.EqualTo((bestSuggested, 100ul)));
    }

    /// <summary>Concrete inner: NSubstitute cannot proxy the <c>out ReadOnlySpan&lt;byte&gt;</c> default
    /// interface method (a ref struct), so the tests that touch it use this instead.</summary>
    private sealed class RecordingInner : IBlockhashProvider
    {
        public bool TryGetBlockhashCalled;
        public (BlockHeader Header, ulong Number)? GetBlockhashCalledWith;
        public Hash256? StateHash;

        public Hash256? GetBlockhash(BlockHeader currentBlock, ulong number, IReleaseSpec spec)
        {
            GetBlockhashCalledWith = (currentBlock, number);
            return StateHash;
        }

        public bool TryGetBlockhash(BlockHeader currentBlock, ulong number, IReleaseSpec spec, out ReadOnlySpan<byte> hash)
        {
            TryGetBlockhashCalled = true;
            hash = default;
            return false;
        }

        public Task Prefetch(BlockHeader currentBlock, CancellationToken token) => Task.CompletedTask;
    }
}
