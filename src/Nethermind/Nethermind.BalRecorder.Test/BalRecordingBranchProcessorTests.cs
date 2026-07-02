// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using NUnit.Framework;

namespace Nethermind.BalRecorder.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class BalRecordingBranchProcessorTests
{
    [TestCase(true, false, true, true, TestName = "Replay attaches the stored BAL and flips the switch before delegating")]
    [TestCase(true, false, false, false, TestName = "Replay without a stored BAL leaves the block untouched and the switch off")]
    [TestCase(false, true, false, true, TestName = "Recording flips the switch before delegating")]
    public void Interception_is_applied_before_delegating(bool replay, bool recording, bool seedBal, bool expectedSwitchDuringProcess)
    {
        ReadOnlyBlockAccessList seeded = new();
        FakeRecordedBalStore store = new() { ReplayEnabled = replay, RecordingEnabled = recording };
        if (seedBal) store.Seed(1, seeded);

        BalRecorderSpecSwitch balSwitch = new();
        StubBranchProcessor inner = new();
        bool switchDuringProcess = false;
        Block? observedBlock = null;
        inner.OnProcess = blocks =>
        {
            observedBlock = blocks[0];
            switchDuringProcess = balSwitch.Enabled;
        };

        BalRecordingBranchProcessor sut = new(inner, store, balSwitch);
        Block block = Build.A.Block.WithNumber(1).TestObject;

        Block[] result = sut.Process(null, [block], ProcessingOptions.None, NullBlockTracer.Instance);

        Assert.That(switchDuringProcess, Is.EqualTo(expectedSwitchDuringProcess), "the prewarmer reads the spec switch inside inner.Process");
        Assert.That(balSwitch.Enabled, Is.False, "the switch must be reset once the branch is processed");
        Assert.That(observedBlock, Is.SameAs(block));
        if (seedBal)
            Assert.That(block.BlockAccessList, Is.SameAs(seeded), "the replayed BAL must be attached before the prewarmer runs");
        else
            Assert.That(block.BlockAccessList, Is.Null);
        Assert.That(result, Has.Length.EqualTo(1));
        Assert.That(result[0], Is.SameAs(block));
    }

    [Test]
    public void Forwards_branch_processor_events_to_the_inner_processor()
    {
        StubBranchProcessor inner = new();
        BalRecordingBranchProcessor sut = new(inner, new FakeRecordedBalStore(), new BalRecorderSpecSwitch());
        Block block = Build.A.Block.WithNumber(1).TestObject;

        Block? processed = null;
        Block? processing = null;
        IReadOnlyList<Block>? batch = null;
        IReadOnlyList<Block>? processedBatch = null;
        sut.BlockProcessed += (_, e) => processed = e.Block;
        sut.BlocksProcessing += (_, e) => batch = e.Blocks;
        sut.BlocksProcessed += (_, e) => processedBatch = e.Blocks;
        sut.BlockProcessing += (_, e) => processing = e.Block;

        inner.RaiseBlockProcessed(block);
        inner.RaiseBlocksProcessing([block]);
        inner.RaiseBlocksProcessed([block]);
        inner.RaiseBlockProcessing(block);

        Assert.That(processed, Is.SameAs(block));
        Assert.That(processing, Is.SameAs(block));
        Assert.That(batch, Is.EqualTo(new[] { block }));
        Assert.That(processedBatch, Is.EqualTo(new[] { block }));
    }

    private sealed class StubBranchProcessor : IBranchProcessor
    {
        public Action<IReadOnlyList<Block>>? OnProcess { get; set; }

        public Block[] Process(BlockHeader? baseBlock, IReadOnlyList<Block> suggestedBlocks, ProcessingOptions processingOptions, IBlockTracer blockTracer, CancellationToken token = default)
        {
            OnProcess?.Invoke(suggestedBlocks);
            return [.. suggestedBlocks];
        }

        public event EventHandler<BlockProcessedEventArgs>? BlockProcessed;
        public event EventHandler<BlocksProcessingEventArgs>? BlocksProcessing;
        public event EventHandler<BlocksProcessingEventArgs>? BlocksProcessed;
        public event EventHandler<BlockEventArgs>? BlockProcessing;

        public void RaiseBlockProcessed(Block block) => BlockProcessed?.Invoke(this, new BlockProcessedEventArgs(block, []));
        public void RaiseBlocksProcessing(IReadOnlyList<Block> blocks) => BlocksProcessing?.Invoke(this, new BlocksProcessingEventArgs(blocks));
        public void RaiseBlocksProcessed(IReadOnlyList<Block> blocks) => BlocksProcessed?.Invoke(this, new BlocksProcessingEventArgs(blocks));
        public void RaiseBlockProcessing(Block block) => BlockProcessing?.Invoke(this, new BlockEventArgs(block));
    }
}
