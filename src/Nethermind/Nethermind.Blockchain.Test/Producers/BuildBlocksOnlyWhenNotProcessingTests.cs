// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Producers;

[Parallelizable(ParallelScope.All)]
public class BuildBlocksOnlyWhenNotProcessingTests
{
    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task should_trigger_block_production_on_empty_queue()
    {
        Context context = new();
        context.BlockProcessingQueue.IsEmpty.Returns(true);
        Block block = (await context.MainBlockProductionTrigger.BuildBlock())!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(block, Is.EqualTo(context.DefaultBlock));
            Assert.That(context.TriggeredCount, Is.EqualTo(1));
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task should_trigger_block_production_when_queue_empties()
    {
        Context context = new();
        context.BlockProcessingQueue.IsEmpty.Returns(false);
        Task<Block?> buildTask = context.MainBlockProductionTrigger.BuildBlock();

        Assert.That(() => buildTask.IsCanceled, Is.False.After(BuildBlocksOnlyWhenNotProcessing.ChainNotYetProcessedMillisecondsDelay * 2, 10));

        context.BlockProcessingQueue.IsEmpty.Returns(true);
        Block? block = await buildTask;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(block, Is.EqualTo(context.DefaultBlock));
            Assert.That(context.TriggeredCount, Is.EqualTo(1));
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task should_cancel_triggering_block_production()
    {
        Context context = new();
        context.BlockProcessingQueue.IsEmpty.Returns(false);
        using CancellationTokenSource cancellationTokenSource = new();
        Task<Block?> buildTask = context.MainBlockProductionTrigger.BuildBlock(cancellationToken: cancellationTokenSource.Token);

        Assert.That(() => buildTask.IsCanceled, Is.False.After(BuildBlocksOnlyWhenNotProcessing.ChainNotYetProcessedMillisecondsDelay * 2, 10));

        cancellationTokenSource.Cancel();

        Func<Task> f = async () => { await buildTask; };
        Assert.That(async () => await f(), Throws.InstanceOf<OperationCanceledException>());
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task should_trigger_block_production_after_a_rejected_block_was_processed_before_its_suggestion_event()
    {
        Context context = new();
        context.BlockProcessingQueue.IsEmpty.Returns(true);
        Block rejected = Build.A.Block.WithNumber(1).TestObject;
        context.BlockTree.Head.Returns(context.DefaultBlock);

        context.BlockProcessingQueue.ProcessingQueueEmpty += Raise.Event();
        context.BlockTree.NewBestSuggestedBlock += Raise.EventWith(new BlockEventArgs(rejected));

        using CancellationTokenSource cancellation = new();
        Task<Block?> buildTask = context.MainBlockProductionTrigger.BuildBlock(cancellationToken: cancellation.Token);
        bool producedAtOnce = buildTask.IsCompletedSuccessfully;
        await cancellation.CancelAsync();

        Assert.That(producedAtOnce, Is.True);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task should_delay_block_production_while_the_processing_queue_is_not_empty()
    {
        Context context = new();
        context.BlockProcessingQueue.ProcessingQueueEmpty += Raise.Event();
        context.BlockProcessingQueue.IsEmpty.Returns(false);

        Task<Block?> buildTask = context.MainBlockProductionTrigger.BuildBlock();
        Assert.That(buildTask.IsCompleted, Is.False);

        context.BlockProcessingQueue.IsEmpty.Returns(true);
        Block? block = await buildTask;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(block, Is.EqualTo(context.DefaultBlock));
            Assert.That(context.TriggeredCount, Is.EqualTo(1));
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime), NonParallelizable]
    public async Task should_report_that_blocks_cannot_be_produced_while_production_waits_for_processing()
    {
        Context context = new();
        context.BlockProcessingQueue.ProcessingQueueEmpty += Raise.Event();
        context.BlockProcessingQueue.IsEmpty.Returns(false);

        Task<Block?> buildTask = context.MainBlockProductionTrigger.BuildBlock();
        long whileWaiting = Metrics.CanProduceBlocks;

        context.BlockProcessingQueue.IsEmpty.Returns(true);
        context.BlockProcessingQueue.ProcessingQueueEmpty += Raise.Event();
        long afterProcessing = Metrics.CanProduceBlocks;
        Block? block = await buildTask;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(whileWaiting, Is.Zero);
            Assert.That(afterProcessing, Is.EqualTo(1));
            Assert.That(block, Is.EqualTo(context.DefaultBlock));
        }
    }

    private class Context
    {
        public Context()
        {
            MainBlockProductionTrigger = new BuildBlocksWhenRequested();
            BlockProcessingQueue = Substitute.For<IBlockProcessingQueue>();
            BlockTree = Substitute.For<IBlockTree>();
            Trigger = new BuildBlocksOnlyWhenNotProcessing(MainBlockProductionTrigger, BlockProcessingQueue, BlockTree, LimboLogs.Instance, false);
            DefaultBlock = Build.A.Block.TestObject;
            Trigger.TriggerBlockProduction += OnTriggerBlockProduction;
        }

        public Block DefaultBlock { get; }
        public int TriggeredCount { get; private set; }

        private void OnTriggerBlockProduction(object? sender, BlockProductionEventArgs e)
        {
            TriggeredCount++;
            e.BlockProductionTask = Task.FromResult<Block?>(DefaultBlock);
        }

        public BuildBlocksOnlyWhenNotProcessing Trigger { get; }
        public IBlockTree BlockTree { get; }
        public IBlockProcessingQueue BlockProcessingQueue { get; }
        public IManualBlockProductionTrigger MainBlockProductionTrigger { get; }
    }
}
