//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

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
        [Test]
        public async Task should_trigger_block_production_on_empty_queue()
        {
            Context context = new();
            context.BlockProcessingQueue.IsEmpty.Returns(true);
            Block block = await context.MainBlockProductionTrigger.BuildBlock();
            block.Should().Be(context.DefaultBlock);
            context.TriggeredCount.Should().Be(1);
        }
        
        [Test]
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
        
        [Test]
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
