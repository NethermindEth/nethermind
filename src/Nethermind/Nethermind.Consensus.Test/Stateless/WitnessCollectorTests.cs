// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain.Headers;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Stateless;

public class WitnessCollectorTests
{
    private IWorldState _state = null!;
    private IReadOnlyTrieStore _trieStore = null!;
    private IBlockProcessor _blockProcessor = null!;
    private WitnessCollector _collector = null!;
    private BlockHeader _parent = null!;
    private Block _block = null!;

    [SetUp]
    public void SetUp()
    {
        _state = Substitute.For<IWorldState>();
        _state.TryBeginScope(Arg.Any<BlockHeader>(), out Arg.Any<IDisposable>()).Returns(static call =>
        {
            call[1] = Substitute.For<IDisposable>();
            return true;
        });
        _trieStore = Substitute.For<IReadOnlyTrieStore>();
        _blockProcessor = Substitute.For<IBlockProcessor>();
        WitnessGeneratingWorldState worldState = new(
            _state, Substitute.For<IStateReader>(), _trieStore, new WitnessHeaderRecorder(), Substitute.For<IHeaderFinder>());
        _collector = new WitnessCollector(worldState, _blockProcessor, Substitute.For<ISpecProvider>(), Substitute.For<IPoSSwitcher>(), LimboLogs.Instance);

        _parent = Build.A.BlockHeader.WithNumber(7).WithStateRoot(TestItem.KeccakA).TestObject;
        _block = Build.A.Block.WithParent(_parent).WithStateRoot(TestItem.KeccakB).TestObject;
    }

    [TearDown]
    public void TearDown() => _trieStore.Dispose();

    [Test]
    public void Refuses_before_executing_when_the_parent_trie_is_not_available()
    {
        _trieStore.HasRoot(TestItem.KeccakA, 7).Returns(false);

        Assert.That(() => _collector.GetWitnessForExistingBlock(_parent, _block), Throws.TypeOf<StateNotRetainedException>());
        _blockProcessor.DidNotReceiveWithAnyArgs().ProcessOne(default!, default, default!, default!, default);
        _state.DidNotReceiveWithAnyArgs().TryBeginScope(default, out _);
    }

    [Test]
    public void Re_executes_a_copy_of_the_block_without_validation_and_rejects_a_different_header()
    {
        _trieStore.HasRoot(TestItem.KeccakA, 7).Returns(true);
        Block executed = null;
        ProcessingOptions options = ProcessingOptions.None;
        _blockProcessor.ProcessOne(Arg.Any<Block>(), Arg.Any<ProcessingOptions>(), Arg.Any<IBlockTracer>(), Arg.Any<IReleaseSpec>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                executed = call.ArgAt<Block>(0);
                options = call.ArgAt<ProcessingOptions>(1);
                Block processed = executed.WithReplacedHeader(executed.Header.Clone());
                processed.Header.StateRoot = TestItem.KeccakC;
                processed.Header.Hash = processed.Header.CalculateHash();
                return (processed, Array.Empty<TxReceipt>());
            });

        Assert.That(() => _collector.GetWitnessForExistingBlock(_parent, _block), Throws.TypeOf<InvalidBlockException>());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(options, Is.EqualTo(ProcessingOptions.ReadOnlyChain | ProcessingOptions.NoValidation));
            Assert.That(executed, Is.Not.Null.And.Not.SameAs(_block));
            Assert.That(executed.Header, Is.Not.SameAs(_block.Header));
            Assert.That(executed.Hash, Is.EqualTo(_block.Hash));
        }
    }

    [Test]
    public void Reports_a_trie_lost_during_re_execution_as_not_retained()
    {
        _trieStore.HasRoot(TestItem.KeccakA, 7).Returns(true, false);
        _blockProcessor.ProcessOne(Arg.Any<Block>(), Arg.Any<ProcessingOptions>(), Arg.Any<IBlockTracer>(), Arg.Any<IReleaseSpec>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Block executed = call.ArgAt<Block>(0);
                Block processed = executed.WithReplacedHeader(executed.Header.Clone());
                processed.Header.StateRoot = TestItem.KeccakA;
                processed.Header.Hash = processed.Header.CalculateHash();
                return (processed, Array.Empty<TxReceipt>());
            });

        Assert.That(() => _collector.GetWitnessForExistingBlock(_parent, _block),
            Throws.TypeOf<StateNotRetainedException>().With.Message.Contains("witness of block 8"));
    }
}
