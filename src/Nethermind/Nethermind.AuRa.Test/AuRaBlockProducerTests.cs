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

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Castle.DynamicProxy.Generators.Emitters.SimpleAST;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NSubstitute;
using NSubstitute.ReceivedExtensions;
using NUnit.Framework;

namespace Nethermind.AuRa.Test
{
    [Parallelizable(ParallelScope.All)]
    public class AuRaBlockProducerTests
    {
        private class Context
        {
            public ITxSource TransactionSource { get; }
            public IBlockchainProcessor BlockchainProcessor { get; }
            public ISealer Sealer { get; }
            public IBlockTree BlockTree { get; }
            public IBlockProcessingQueue BlockProcessingQueue { get; }
            public IStateProvider StateProvider { get; }
            public ITimestamper Timestamper { get; }
            public IAuRaStepCalculator AuRaStepCalculator { get; }
            public Address NodeAddress { get; }
            public AuRaBlockProducer AuRaBlockProducer { get; private set; }
            public TimeSpan StepDelay { get; }

            public Context()
            {
                StepDelay = TimeSpan.FromMilliseconds(20);
                TransactionSource = Substitute.For<ITxSource>();
                BlockchainProcessor = Substitute.For<IBlockchainProcessor>();
                Sealer = Substitute.For<ISealer>();
                BlockTree = Substitute.For<IBlockTree>();
                BlockProcessingQueue = Substitute.For<IBlockProcessingQueue>();
                StateProvider = Substitute.For<IStateProvider>();
                Timestamper = Substitute.For<ITimestamper>();
                AuRaStepCalculator = Substitute.For<IAuRaStepCalculator>();
                NodeAddress = TestItem.AddressA;
            	TransactionSource.GetTransactions(Arg.Any<BlockHeader>(), Arg.Any<long>()).Returns(Array.Empty<Transaction>());
                Sealer.CanSeal(Arg.Any<long>(), Arg.Any<Keccak>()).Returns(true);
                Sealer.SealBlock(Arg.Any<Block>(), Arg.Any<CancellationToken>()).Returns(c => Task.FromResult(c.Arg<Block>()));
                Sealer.Address.Returns(TestItem.AddressA);
                BlockProcessingQueue.IsEmpty.Returns(true);
                AuRaStepCalculator.TimeToNextStep.Returns(StepDelay);
                BlockTree.BestKnownNumber.Returns(1);
                BlockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithAura(10, Array.Empty<byte>()).TestObject).TestObject);
                BlockchainProcessor.Process(Arg.Any<Block>(), ProcessingOptions.ProducingBlock, Arg.Any<IBlockTracer>()).Returns(returnThis: c =>
                {
                    Block block = c.Arg<Block>();
                    block.TrySetTransactions(TransactionSource.GetTransactions(BlockTree.Head!.Header, block.GasLimit).ToArray());
                    return block;
                });
                InitProducer();
            }
            
                    private void InitProducer()
                    {
                        AuRaConfig auRaConfig = new AuRaConfig();
                        auRaConfig.ForceSealing = true;
                        InitProducer(auRaConfig);
                    }
                    
                    public void InitProducer(IAuraConfig auraConfig)
                    {
                        IBlockProductionTrigger onAuRaSteps = new BuildBlocksOnAuRaSteps(LimboLogs.Instance, AuRaStepCalculator);
                        IBlockProductionTrigger onlyWhenNotProcessing = new BuildBlocksOnlyWhenNotProcessing(
                            onAuRaSteps, 
                            BlockProcessingQueue, 
                            BlockTree, 
                            LimboLogs.Instance, 
                            !auraConfig.AllowAuRaPrivateChains);

                        FollowOtherMiners gasLimitCalculator = new(MainnetSpecProvider.Instance);
                        
                        AuRaBlockProducer = new AuRaBlockProducer(
                            TransactionSource,
                            BlockchainProcessor,
                            onlyWhenNotProcessing,
                            StateProvider,
                            Sealer,
                            BlockTree,
                            Timestamper,
                            AuRaStepCalculator,
                            NullReportingValidator.Instance,
                            auraConfig,
                            gasLimitCalculator,
                            MainnetSpecProvider.Instance,
                            LimboLogs.Instance);

                        var suggester = new ProducedBlockSuggester(BlockTree, AuRaBlockProducer);
                    }
        }

        [Test, Retry(3)]
        public async Task Produces_block()
        {
            (await StartStop(new Context())).ShouldProduceBlocks(Quantity.AtLeastOne());
        }
        
        [Test]
        public async Task Can_produce_first_block_when_private_chains_allowed()
        {
            var context = new Context();
            context.InitProducer(new AuRaConfig{AllowAuRaPrivateChains = true, ForceSealing = true});
            (await StartStop(context,false)).ShouldProduceBlocks(Quantity.AtLeastOne());
        }
        
        [Test]
        public async Task Cannot_produce_first_block_when_private_chains_not_allowed()
        {
            (await StartStop(new Context(), false)).ShouldProduceBlocks(Quantity.None());
        }
        
        [Test]
        public async Task Does_not_produce_block_when_ProcessingQueueEmpty_not_raised()
        {
            (await StartStop(new Context(), false, true)).ShouldProduceBlocks(Quantity.None());
        }

        [Test]
        public async Task Does_not_produce_block_when_QueueNotEmpty()
        {
            Context context = new Context();
            context.BlockProcessingQueue.IsEmpty.Returns(false);
            (await StartStop(context)).ShouldProduceBlocks(Quantity.None());
        }
        
        [Test]
        public async Task Does_not_produce_block_when_cannot_seal()
        {
            Context context = new Context();
            context.Sealer.CanSeal(Arg.Any<long>(), Arg.Any<Keccak>()).Returns(false);
            (await StartStop(context)).ShouldProduceBlocks(Quantity.None());
        }
        
        [Test]
        public async Task Does_not_produce_block_when_ForceSealing_is_false_and_no_transactions()
        {
            var context = new Context();
            AuRaConfig auRaConfig = new AuRaConfig {ForceSealing = false};
            context.InitProducer(auRaConfig);
            (await StartStop(context)).ShouldProduceBlocks(Quantity.None());
        }
        
        [Test]
        public async Task Produces_block_when_ForceSealing_is_false_and_there_are_transactions()
        {
            var context = new Context();
            AuRaConfig auRaConfig = new AuRaConfig {ForceSealing = false};
            context.InitProducer(auRaConfig);
            context.TransactionSource.GetTransactions(Arg.Any<BlockHeader>(), Arg.Any<long>()).Returns(new[] {Build.A.Transaction.TestObject});
            (await StartStop(context)).ShouldProduceBlocks(Quantity.AtLeastOne());
        }
        
        [Test]
        public async Task Does_not_produce_block_when_sealing_fails()
        {
            var context = new Context();
            context.Sealer.SealBlock(Arg.Any<Block>(), Arg.Any<CancellationToken>()).Returns(c => Task.FromException(new Exception()));
            (await StartStop(context)).ShouldProduceBlocks(Quantity.None());
        }
        
        [Test]
        public async Task Does_not_produce_block_when_sealing_cancels()
        {
            var context = new Context();
            context.Sealer.SealBlock(Arg.Any<Block>(), Arg.Any<CancellationToken>()).Returns(c => Task.FromCanceled(new CancellationToken(true)));
            (await StartStop(context)).ShouldProduceBlocks(Quantity.None());
        }
        
        [Test]
        public async Task Does_not_produce_block_when_head_is_null()
        {
            var context = new Context();
            context.BlockTree.Head.Returns((Block) null);
            (await StartStop(context)).ShouldProduceBlocks(Quantity.None());
        }
        
        [Test]
        public async Task Does_not_produce_block_when_processing_fails()
        {
            var context = new Context();
            context.BlockchainProcessor.Process(Arg.Any<Block>(), ProcessingOptions.ProducingBlock, Arg.Any<IBlockTracer>()).Returns((Block) null);
            (await StartStop(context)).ShouldProduceBlocks(Quantity.None());
        }
        
        [Test]
        public async Task Does_not_produce_block_when_there_is_new_best_suggested_block_not_yet_processed()
        {
            (await StartStop(new Context(), true, true)).ShouldProduceBlocks(Quantity.None());
        }
        
        private async Task<TestResult> StartStop(Context context, bool processingQueueEmpty = true, bool newBestSuggestedBlock = false, int stepDelayMultiplier = 100)
        {
            AutoResetEvent processedEvent = new AutoResetEvent(false);
            context.BlockTree.SuggestBlock(Arg.Any<Block>(), Arg.Any<bool>())
                .Returns(AddBlockResult.Added)
                .AndDoes(c =>
                {
                    processedEvent.Set();
                });

            context.AuRaBlockProducer.Start();
            await processedEvent.WaitOneAsync(context.StepDelay * stepDelayMultiplier, CancellationToken.None);
            context.BlockTree.ClearReceivedCalls();
            
            try
            {
                await Task.Delay(context.StepDelay);
                if (processingQueueEmpty)
                {
                    context.BlockProcessingQueue.ProcessingQueueEmpty += Raise.Event();
                }

                if (newBestSuggestedBlock)
                {
                    context.BlockTree.NewBestSuggestedBlock += Raise.EventWith(new BlockEventArgs(Build.A.Block.TestObject));
                    context.BlockTree.ClearReceivedCalls();
                }
                
                await processedEvent.WaitOneAsync(context.StepDelay * stepDelayMultiplier, CancellationToken.None);

            }
            finally
            {
                await context.AuRaBlockProducer.StopAsync();
            }

            return new TestResult(q => context.BlockTree.Received(q).SuggestBlock(Arg.Any<Block>(), Arg.Any<bool>()));
        }
        
        private class TestResult
        {
            private readonly Action<Quantity> _assert;

            public TestResult(Action<Quantity> assert)
            {
                _assert = assert;
            }

            public void ShouldProduceBlocks(Quantity quantity)
            {
                _assert(quantity);
            }
        }
    }
}
