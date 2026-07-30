// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.BalRecorder.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class BalRecordingBlockProcessorTests
{
    [TestCase(true, TestName = "Records the generated BAL when recording is enabled")]
    [TestCase(false, TestName = "Does not record when recording is disabled")]
    public void Records_each_processed_block_when_recording_enabled(bool recordingEnabled)
    {
        FakeRecordedBalStore store = new() { RecordingEnabled = recordingEnabled };
        GeneratedBlockAccessList generated = new();
        IBlockAccessListManager balManager = Substitute.For<IBlockAccessListManager>();
        balManager.GeneratedBlockAccessList.Returns(generated);

        Block processed = Build.A.Block.WithNumber(9).TestObject;
        StubBlockProcessor inner = new() { ProcessedBlock = processed };
        BalRecordingBlockProcessor sut = new(inner, store, balManager);

        (Block block, _) = sut.ProcessOne(processed, ProcessingOptions.None, NullBlockTracer.Instance, Substitute.For<IReleaseSpec>(), CancellationToken.None);

        Assert.That(block, Is.SameAs(processed));
        balManager.Received(1).ForceConstructGeneratedBlockAccessList = recordingEnabled;
        if (recordingEnabled)
        {
            Assert.That(store.Inserted, Has.Count.EqualTo(1));
            Assert.That(store.Inserted[0], Is.EqualTo((9L, generated)));
        }
        else
        {
            Assert.That(store.Inserted, Is.Empty);
        }
    }

    [Test]
    public void Forwards_the_TransactionsExecuted_event_to_the_inner_processor()
    {
        StubBlockProcessor inner = new();
        BalRecordingBlockProcessor sut = new(inner, new FakeRecordedBalStore(), Substitute.For<IBlockAccessListManager>());

        int count = 0;
        sut.TransactionsExecuted += () => count++;
        inner.RaiseTransactionsExecuted();

        Assert.That(count, Is.EqualTo(1));
    }

    private sealed class StubBlockProcessor : IBlockProcessor
    {
        public Block? ProcessedBlock { get; set; }

        public (Block Block, TxReceipt[] Receipts) ProcessOne(Block suggestedBlock, ProcessingOptions options, IBlockTracer blockTracer, IReleaseSpec spec, CancellationToken token)
            => (ProcessedBlock ?? suggestedBlock, []);

        public event Action? TransactionsExecuted;

        public void RaiseTransactionsExecuted() => TransactionsExecuted?.Invoke();
    }
}
