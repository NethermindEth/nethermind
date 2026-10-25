// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
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
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Evm.State;
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
            public IBlockProducerRunner BlockProducerRunner { get; set; }
            public TimeSpan StepDelay { get; }
            private BuildBlocksOnlyWhenNotProcessing _trigger;

            public Context(IAuraConfig auraConfig = null)
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
                TransactionSource.GetTransactions(Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>(), Arg.Any<ulong>()).Returns(Array.Empty<Transaction>());
                Sealer.CanSeal(Arg.Any<ulong>(), Arg.Any<Hash256>()).Returns(true);
                Sealer.SealBlock(Arg.Any<Block>(), Arg.Any<CancellationToken>()).Returns(c => Task.FromResult(c.Arg<Block>()));
                Sealer.Address.Returns(TestItem.AddressA);
                BlockProcessingQueue.IsEmpty.Returns(true);
                AuRaStepCalculator.TimeToNextStep.Returns(StepDelay);
                BlockTree.BestKnownNumber.Returns(1UL);
                BlockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithAura(10, []).TestObject).TestObject);
                BlockchainProcessor.Process(Arg.Any<Block>(), ProcessingOptions.ProducingBlock, Arg.Any<IBlockTracer>(), Arg.Any<CancellationToken>()).Returns(returnThis: c =>
                {
                    Block block = c.Arg<Block>();
                    block.TrySetTransactions(TransactionSource.GetTransactions(BlockTree.Head!.Header, block.Header, block.GasLimit).ToArray());
                    return block;
                });
                StateProvider.HasStateForTargetBlock(Arg.Any<BlockHeader>()).Returns(x => true);
                InitProducer(auraConfig ?? new AuRaConfig { ForceSealing = true });
            }

            private void InitProducer(IAuraConfig auraConfig)
            {
                IBlockProductionTrigger onAuRaSteps = new BuildBlocksOnAuRaSteps(AuRaStepCalculator, LimboLogs.Instance);
                _trigger = new BuildBlocksOnlyWhenNotProcessing(
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

                BlockProducerRunner = new StandardBlockProducerRunner(
                    _trigger,
                    BlockTree,
                    AuRaBlockProducer);
                _ = new
                ProducedBlockSuggester(BlockTree, BlockProducerRunner);
            }

            public async Task StopAsync()
            {
                await BlockProducerRunner.StopAsync();
                await _trigger.DisposeAsync();
            }
        }

        [Test]
        public async Task Produces_block() =>
            await StartStop(new Context(), expectBlock: true);

        [Test]
        public async Task Can_produce_first_block_when_private_chains_allowed() =>
            await StartStop(new Context(new AuRaConfig { AllowAuRaPrivateChains = true, ForceSealing = true }), expectBlock: true, processingQueueEmpty: false);

        [Test]
        public async Task Cannot_produce_first_block_when_private_chains_not_allowed() =>
            await StartStop(new Context(), expectBlock: false, processingQueueEmpty: false);

        [Test]
        public async Task Does_not_produce_block_when_ProcessingQueueEmpty_not_raised() =>
            await StartStop(new Context(), expectBlock: false, processingQueueEmpty: false, newBestSuggestedBlock: true);

        [Test]
        public async Task Does_not_produce_block_when_QueueNotEmpty()
        {
            Context context = new();
            context.BlockProcessingQueue.IsEmpty.Returns(false);
            await StartStop(context, expectBlock: false);
        }

        [Test]
        public async Task Does_not_produce_block_when_cannot_seal()
        {
            Context context = new();
            context.Sealer.CanSeal(Arg.Any<ulong>(), Arg.Any<Hash256>()).Returns(false);
            await StartStop(context, expectBlock: false);
        }

        [Test]
        public async Task Does_not_produce_block_when_ForceSealing_is_false_and_no_transactions() =>
            await StartStop(new Context(new AuRaConfig { ForceSealing = false }), expectBlock: false);

        [Test]
        public async Task Produces_block_when_ForceSealing_is_false_and_there_are_transactions([Values(0, 1000)] int sealDelayMs)
        {
            Context context = new(new AuRaConfig { ForceSealing = false });
            context.TransactionSource.GetTransactions(Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>(), Arg.Any<ulong>()).Returns(new[] { Build.A.Transaction.TestObject });
            context.Sealer.SealBlock(Arg.Any<Block>(), Arg.Any<CancellationToken>()).Returns(async c =>
            {
                Block block = c.Arg<Block>();
                await Task.Delay(sealDelayMs);
                return block;
            });
            await StartStop(context, expectBlock: true);
        }

        [Test]
        public async Task Does_not_produce_block_when_sealing_fails()
        {
            Context context = new();
            context.Sealer.SealBlock(Arg.Any<Block>(), Arg.Any<CancellationToken>()).Returns(static c => Task.FromException(new Exception()));
            await StartStop(context, expectBlock: false);
        }

        [Test]
        public async Task Does_not_produce_block_when_sealing_cancels()
        {
            Context context = new();
            context.Sealer.SealBlock(Arg.Any<Block>(), Arg.Any<CancellationToken>()).Returns(static c => Task.FromCanceled(new CancellationToken(true)));
            await StartStop(context, expectBlock: false);
        }

        [Test]
        public async Task Does_not_produce_block_when_head_is_null()
        {
            Context context = new();
            context.BlockTree.Head.Returns((Block)null);
            await StartStop(context, expectBlock: false);
        }

        [Test]
        public async Task Does_not_produce_block_when_processing_fails()
        {
            Context context = new();
            context.BlockchainProcessor.Process(Arg.Any<Block>(), ProcessingOptions.ProducingBlock, Arg.Any<IBlockTracer>(), Arg.Any<CancellationToken>()).Returns((Block)null);
            await StartStop(context, expectBlock: false);
        }

        [Test]
        public async Task Does_not_produce_block_when_target_block_state_is_unavailable()
        {
            Context context = new();
            context.StateProvider.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(true);
            context.StateProvider.HasStateForTargetBlock(Arg.Any<BlockHeader>()).Returns(false);
            await StartStop(context, expectBlock: false);
        }

        [Test]
        public async Task Does_not_produce_block_when_there_is_new_best_suggested_block_not_yet_processed() =>
            await StartStop(new Context(), expectBlock: false, newBestSuggestedBlock: true);

        private async Task StartStop(Context context, bool expectBlock, bool processingQueueEmpty = true, bool newBestSuggestedBlock = false)
        {
            TaskCompletionSource processedEvent = new(TaskCreationOptions.RunContinuationsAsynchronously);
            context.BlockTree.SuggestBlock(Arg.Any<Block>(), Arg.Any<BlockTreeSuggestOptions>())
                .Returns(AddBlockResult.Added)
                .AndDoes(c =>
                {
                    Volatile.Read(ref processedEvent).TrySetResult();
                });

            context.BlockProducerRunner.Start();
            await Task.WhenAny(processedEvent.Task, Task.Delay(context.StepDelay * 20));
            context.BlockTree.ClearReceivedCalls();
            await Task.Delay(context.StepDelay * 2);
            Interlocked.Exchange(ref processedEvent, new(TaskCreationOptions.RunContinuationsAsynchronously));

            try
            {
                if (processingQueueEmpty)
                {
                    context.BlockProcessingQueue.ProcessingQueueEmpty += Raise.Event();
                }

                if (newBestSuggestedBlock)
                {
                    context.BlockTree.NewBestSuggestedBlock += Raise.EventWith(new BlockEventArgs(Build.A.Block.TestObject));
                    await Task.Delay(context.StepDelay * 5);
                    context.BlockTree.ClearReceivedCalls();
                    Interlocked.Exchange(ref processedEvent, new(TaskCreationOptions.RunContinuationsAsynchronously));
                }

                // A fixed window only suits the negative cases: a produced block reaches SuggestBlock through
                // thread-pool continuations, which a busy test run can delay past any short window.
                if (expectBlock)
                {
                    await processedEvent.Task.WaitAsync(TimeSpan.FromSeconds(10));
                }
                else
                {
                    await Task.WhenAny(processedEvent.Task, Task.Delay(context.StepDelay * 20));
                }
            }
            finally
            {
                await context.StopAsync();
            }

            context.BlockTree.Received(expectBlock ? Quantity.AtLeastOne() : Quantity.None()).SuggestBlock(Arg.Any<Block>(), Arg.Any<BlockTreeSuggestOptions>());
        }
    }
}
