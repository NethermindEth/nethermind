// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Producers
{
    [Parallelizable(ParallelScope.All)]
    public class BuildBlocksOnlyWhenNotProcessingTests
    {
        [Test, Timeout(Timeout.MaxTestTime)]
        public async Task should_trigger_block_production_on_empty_queue()
        {
            Context context = new();
            context.BlockProcessingQueue.IsEmpty.Returns(true);
            Block block = await context.MainBlockProductionTrigger.BuildBlock();
            block.Should().Be(context.DefaultBlock);
            context.TriggeredCount.Should().Be(1);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public async Task should_trigger_block_production_when_queue_empties()
        {
            Context context = new();
            context.BlockProcessingQueue.IsEmpty.Returns(false);
            Task<Block> buildTask = context.MainBlockProductionTrigger.BuildBlock();

            await Task.Delay(BuildBlocksOnlyWhenNotProcessing.ChainNotYetProcessedMillisecondsDelay * 2);
            buildTask.IsCanceled.Should().BeFalse();

            context.BlockProcessingQueue.IsEmpty.Returns(true);
            Block block = await buildTask;
            block.Should().Be(context.DefaultBlock);
            context.TriggeredCount.Should().Be(1);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public async Task should_cancel_triggering_block_production()
        {
            Context context = new();
            context.BlockProcessingQueue.IsEmpty.Returns(false);
            using CancellationTokenSource cancellationTokenSource = new();
            Task<Block> buildTask = context.MainBlockProductionTrigger.BuildBlock(cancellationToken: cancellationTokenSource.Token);

            await Task.Delay(BuildBlocksOnlyWhenNotProcessing.ChainNotYetProcessedMillisecondsDelay * 2);
            buildTask.IsCanceled.Should().BeFalse();

            cancellationTokenSource.Cancel();

            Func<Task> f = async () => { await buildTask; };
            await f.Should().ThrowAsync<OperationCanceledException>();
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

            private void OnTriggerBlockProduction(object sender, BlockProductionEventArgs e)
            {
                TriggeredCount++;
                e.BlockProductionTask = Task.FromResult(DefaultBlock);
            }

            public BuildBlocksOnlyWhenNotProcessing Trigger { get; }
            public IBlockTree BlockTree { get; }
            public IBlockProcessingQueue BlockProcessingQueue { get; }
            public IManualBlockProductionTrigger MainBlockProductionTrigger { get; }
        }
    }
}
