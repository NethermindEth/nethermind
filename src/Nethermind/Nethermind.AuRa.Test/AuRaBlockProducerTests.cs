// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Castle.DynamicProxy.Generators.Emitters.SimpleAST;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
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
            public IWorldState StateProvider { get; }
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
                StateProvider = Substitute.For<IWorldState>();
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
                AuRaConfig auRaConfig = new();
                auRaConfig.ForceSealing = true;
                InitProducer(auRaConfig);
            }

            public void InitProducer(IAuraConfig auraConfig)
            {
                IBlockProductionTrigger onAuRaSteps = new BuildBlocksOnAuRaSteps(AuRaStepCalculator, LimboLogs.Instance);
                IBlockProductionTrigger onlyWhenNotProcessing = new BuildBlocksOnlyWhenNotProcessing(
                    onAuRaSteps,
                    BlockProcessingQueue,
                    BlockTree,
                    LimboLogs.Instance,
                    !auraConfig.AllowAuRaPrivateChains);
                IBlocksConfig blocksConfig = new BlocksConfig();
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
                    LimboLogs.Instance,
                    blocksConfig);

                ProducedBlockSuggester suggester = new(BlockTree, AuRaBlockProducer);
            }
        }

        [Test, Retry(6)]
        public async Task Produces_block()
        {
            (await StartStop(new Context())).ShouldProduceBlocks(Quantity.AtLeastOne());
        }

        [Test, Retry(6)]
        public async Task Can_produce_first_block_when_private_chains_allowed()
        {
            Context context = new();
            context.InitProducer(new AuRaConfig { AllowAuRaPrivateChains = true, ForceSealing = true });
            (await StartStop(context, false)).ShouldProduceBlocks(Quantity.AtLeastOne());
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
            Context context = new();
            context.BlockProcessingQueue.IsEmpty.Returns(false);
            (await StartStop(context)).ShouldProduceBlocks(Quantity.None());
        }

        [Test]
        public async Task Does_not_produce_block_when_cannot_seal()
        {
            Context context = new();
            context.Sealer.CanSeal(Arg.Any<long>(), Arg.Any<Keccak>()).Returns(false);
            (await StartStop(context)).ShouldProduceBlocks(Quantity.None());
        }

        [Test]
        public async Task Does_not_produce_block_when_ForceSealing_is_false_and_no_transactions()
        {
            Context context = new();
            AuRaConfig auRaConfig = new() { ForceSealing = false };
            context.InitProducer(auRaConfig);
            (await StartStop(context)).ShouldProduceBlocks(Quantity.None());
        }

        [Test, Retry(9)]
        public async Task Produces_block_when_ForceSealing_is_false_and_there_are_transactions()
        {
            Context context = new();
            AuRaConfig auRaConfig = new() { ForceSealing = false };
            context.InitProducer(auRaConfig);
            context.TransactionSource.GetTransactions(Arg.Any<BlockHeader>(), Arg.Any<long>()).Returns(new[] { Build.A.Transaction.TestObject });
            (await StartStop(context)).ShouldProduceBlocks(Quantity.AtLeastOne());
        }

        [Test]
        public async Task Does_not_produce_block_when_sealing_fails()
        {
            Context context = new();
            context.Sealer.SealBlock(Arg.Any<Block>(), Arg.Any<CancellationToken>()).Returns(c => Task.FromException(new Exception()));
            (await StartStop(context)).ShouldProduceBlocks(Quantity.None());
        }

        [Test]
        public async Task Does_not_produce_block_when_sealing_cancels()
        {
            Context context = new();
            context.Sealer.SealBlock(Arg.Any<Block>(), Arg.Any<CancellationToken>()).Returns(c => Task.FromCanceled(new CancellationToken(true)));
            (await StartStop(context)).ShouldProduceBlocks(Quantity.None());
        }

        [Test]
        public async Task Does_not_produce_block_when_head_is_null()
        {
            Context context = new();
            context.BlockTree.Head.Returns((Block)null);
            (await StartStop(context)).ShouldProduceBlocks(Quantity.None());
        }

        [Test]
        public async Task Does_not_produce_block_when_processing_fails()
        {
            Context context = new();
            context.BlockchainProcessor.Process(Arg.Any<Block>(), ProcessingOptions.ProducingBlock, Arg.Any<IBlockTracer>()).Returns((Block)null);
            (await StartStop(context)).ShouldProduceBlocks(Quantity.None());
        }

        [Test, Retry(6)]
        public async Task Does_not_produce_block_when_there_is_new_best_suggested_block_not_yet_processed()
        {
            (await StartStop(new Context(), true, true)).ShouldProduceBlocks(Quantity.None());
        }

        private async Task<TestResult> StartStop(Context context, bool processingQueueEmpty = true, bool newBestSuggestedBlock = false, int stepDelayMultiplier = 100)
        {
            AutoResetEvent processedEvent = new(false);
            context.BlockTree.SuggestBlock(Arg.Any<Block>(), Arg.Any<BlockTreeSuggestOptions>())
                .Returns(AddBlockResult.Added)
                .AndDoes(c =>
                {
                    processedEvent.Set();
                });

            await context.AuRaBlockProducer.Start();
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

            return new TestResult(q => context.BlockTree.Received(q).SuggestBlock(Arg.Any<Block>(), Arg.Any<BlockTreeSuggestOptions>()));
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
