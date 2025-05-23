// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

[Parallelizable(ParallelScope.Self)]
public class BlockchainProcessorTests
{
    private class ProcessingTestContext
    {
        private readonly ILogManager _logManager = LimboLogs.Instance;

        private class BlockProcessorMock : IBlockProcessor
        {
            private readonly ILogger _logger;

            private readonly HashSet<Hash256> _allowed = new();

            internal readonly HashSet<Hash256> Processed = new();

            private readonly HashSet<Hash256> _allowedToFail = new();

            private readonly HashSet<Hash256> _rootProcessed = new();

            public BlockProcessorMock(ILogManager logManager, IStateReader stateReader)
            {
                _logger = logManager.GetClassLogger();
                stateReader.HasStateForRoot(Arg.Any<Hash256>()).Returns(x => _rootProcessed.Contains(x[0]));
            }

            public void Allow(Hash256 hash)
            {
                _logger.Info($"Allowing {hash} to process");
                _allowed.Add(hash);
            }

            public void AllowToFail(Hash256 hash)
            {
                _logger.Info($"Allowing {hash} to fail");
                _allowedToFail.Add(hash);
            }

            public Block[] Process(Hash256 newBranchStateRoot, IReadOnlyList<Block> suggestedBlocks, ProcessingOptions processingOptions, IBlockTracer blockTracer, CancellationToken token)
            {
                if (blockTracer != NullBlockTracer.Instance)
                {
                    // this is for block reruns on failure for diag tracing
                    throw new InvalidBlockException(suggestedBlocks[0], "wrong tracer");
                }

                Processed.AddRange(suggestedBlocks.Select(x => x.Hash!));

                _logger.Info($"Processing {suggestedBlocks.Last().ToString(Block.Format.Short)}");
                while (true)
                {
                    bool notYet = false;
                    for (int i = 0; i < suggestedBlocks.Count; i++)
                    {
                        BlocksProcessing?.Invoke(this, new BlocksProcessingEventArgs(suggestedBlocks));
                        Block suggestedBlock = suggestedBlocks[i];
                        BlockProcessing?.Invoke(this, new BlockEventArgs(suggestedBlock));
                        Hash256 hash = suggestedBlock.Hash!;
                        if (!_allowed.Contains(hash))
                        {
                            if (_allowedToFail.Remove(hash))
                            {
                                BlockProcessed?.Invoke(this, new BlockProcessedEventArgs(suggestedBlocks.Last(), []));
                                throw new InvalidBlockException(suggestedBlock, "allowed to fail");
                            }

                            notYet = true;
                            break;
                        }
                    }

                    if (notYet)
                    {
                        Thread.Sleep(20);
                    }
                    else
                    {
                        _rootProcessed.Add(suggestedBlocks.Last().StateRoot!);
                        BlockProcessed?.Invoke(this, new BlockProcessedEventArgs(suggestedBlocks.Last(), []));
                        return suggestedBlocks.ToArray();
                    }
                }
            }

            public event EventHandler<BlocksProcessingEventArgs>? BlocksProcessing;

            public event EventHandler<BlockEventArgs>? BlockProcessing;

            public event EventHandler<BlockProcessedEventArgs>? BlockProcessed;

            public event EventHandler<TxProcessedEventArgs>? TransactionProcessed
            {
                add { }
                remove { }
            }
        }

        private class RecoveryStepMock : IBlockPreprocessorStep
        {
            private readonly ILogger _logger;
            private readonly ConcurrentDictionary<Hash256, object> _allowed = new();
            private readonly ConcurrentDictionary<Hash256, object> _allowedToFail = new();

            public RecoveryStepMock(ILogManager logManager)
            {
                _logger = logManager.GetClassLogger();
            }

            public void Allow(Hash256 hash)
            {
                _logger.Info($"Allowing {hash} to recover");
                _allowed[hash] = new object();
            }

            public void RecoverData(Block block)
            {
                _logger.Info($"Recovering data for {block.ToString(Block.Format.Short)}");
                if (block.Author is not null)
                {
                    _logger.Info($"Data was already there for {block.ToString(Block.Format.Short)}");
                    return;
                }

                while (true)
                {
                    Hash256 blockHash = block.Hash!;
                    if (!_allowed.ContainsKey(blockHash))
                    {
                        if (_allowedToFail.ContainsKey(blockHash))
                        {
                            _allowedToFail.Remove(blockHash, out _);
                            throw new Exception();
                        }

                        Thread.Sleep(20);
                        continue;
                    }

                    block.Header.Author = Address.Zero;
                    _allowed.Remove(blockHash, out _);
                    return;
                }
            }
        }

        private readonly BlockTree _blockTree;
        private readonly AutoResetEvent _resetEvent;
        private readonly AutoResetEvent _queueEmptyResetEvent;
        private readonly IStateReader _stateReader;
        private readonly BlockProcessorMock _blockProcessor;
        private readonly RecoveryStepMock _recoveryStep;
        private readonly BlockchainProcessor _processor;
        private readonly ILogger _logger;

        private Hash256? _headBefore;
        private int _processingQueueEmptyFired;
        private const int ProcessingWait = 2000;

        public ProcessingTestContext(bool startProcessor)
        {
            _logger = _logManager.GetClassLogger();
            _stateReader = Substitute.For<IStateReader>();

            _blockTree = Build.A.BlockTree()
                .WithoutSettingHead
                .TestObject;
            _blockProcessor = new BlockProcessorMock(_logManager, _stateReader);
            _recoveryStep = new RecoveryStepMock(_logManager);
            _processor = new BlockchainProcessor(_blockTree, _blockProcessor, _recoveryStep, _stateReader, LimboLogs.Instance, BlockchainProcessor.Options.Default);
            _resetEvent = new AutoResetEvent(false);
            _queueEmptyResetEvent = new AutoResetEvent(false);

            _processor.ProcessingQueueEmpty += (_, _) =>
            {
                _processingQueueEmptyFired++;
                _queueEmptyResetEvent.Set();
            };

            _blockTree.NewHeadBlock += (_, args) =>
            {
                _logger.Info($"Finished waiting for {args.Block.ToString(Block.Format.Short)} as block became the new head block");
                _resetEvent.Set();
            };

            if (startProcessor)
                _processor.Start();
        }

        public ProcessingTestContext IsProcessingBlocks(bool expectedIsProcessingBlocks, ulong maxInterval)
        {
            bool actual = _processor.IsProcessingBlocks(maxInterval);
            Assert.That(actual, Is.EqualTo(expectedIsProcessingBlocks));
            return this;
        }

        public ProcessingTestContext AndRecoveryQueueLimitHasBeenReached()
        {
            _processor.SoftMaxRecoveryQueueSizeInTx = 0;
            return this;
        }

        public AfterBlock Processed(Block block)
        {
            _headBefore = _blockTree.Head?.Hash;
            ManualResetEvent processedEvent = new(false);
            bool wasProcessed = false;
            _blockProcessor.BlockProcessed += (_, args) =>
            {
                if (args.Block.Hash == block.Hash)
                {
                    wasProcessed = true;
                    processedEvent.Set();
                }
            };

            _logger.Info($"Waiting for {block.ToString(Block.Format.Short)} to process");
            _blockProcessor.Allow(block.Hash!);
            processedEvent.WaitOne(ProcessingWait);
            Assert.That(wasProcessed, Is.True, $"Expected this block to get processed but it was not: {block.ToString(Block.Format.Short)}");

            return new AfterBlock(_logManager, this, block);
        }

        public AfterBlock ProcessedSkipped(Block block)
        {
            _headBefore = _blockTree.Head?.Hash;
            _logger.Info($"Waiting for {block.ToString(Block.Format.Short)} to be skipped");
            _blockProcessor.Allow(block.Hash!);
            return new AfterBlock(_logManager, this, block);
        }

        public AfterBlock ProcessedFail(Block block)
        {
            _headBefore = _blockTree.Head?.Hash;
            ManualResetEvent processedEvent = new(false);
            bool wasProcessed = false;
            _blockProcessor.BlockProcessed += (_, args) =>
            {
                if (args.Block.Hash == block.Hash)
                {
                    wasProcessed = true;
                    processedEvent.Set();
                }
            };

            _logger.Info($"Waiting for {block.ToString(Block.Format.Short)} to fail processing");
            _blockProcessor.AllowToFail(block.Hash!);
            processedEvent.WaitOne(ProcessingWait);
            Assert.That(wasProcessed, Is.True, $"Block was never processed {block.ToString(Block.Format.Short)}");
            Assert.That(_blockTree.Head?.Hash, Is.EqualTo(_headBefore), $"Processing did not fail - {block.ToString(Block.Format.Short)} became a new head block");
            _logger.Info($"Finished waiting for {block.ToString(Block.Format.Short)} to fail processing");
            return new AfterBlock(_logManager, this, block);
        }

        public ProcessingTestContext Suggested(Block block, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess)
        {
            AddBlockResult result = _blockTree.SuggestBlock(block, options);
            if (result != AddBlockResult.Added)
            {
                _logger.Info($"Finished waiting for {block.ToString(Block.Format.Short)} as block was ignored");
                _resetEvent.Set();
            }

            return this;
        }

        public ProcessingTestContext SuggestedWithoutProcessingAndMoveToMain(Block block)
        {
            AddBlockResult result = _blockTree.SuggestBlock(block, BlockTreeSuggestOptions.None);
            if (result != AddBlockResult.Added)
            {
                Assert.Fail($"Block {block} was expected to be added");
            }

            _blockTree.UpdateMainChain(new[] { block }, false);
            _blockProcessor.Allow(block.Hash!);
            _recoveryStep.Allow(block.Hash!);

            return this;
        }

        public ProcessingTestContext Suggested(BlockHeader block)
        {
            AddBlockResult result = _blockTree.SuggestHeader(block);
            if (result != AddBlockResult.Added)
            {
                _logger.Info($"Finished waiting for {block.ToString(BlockHeader.Format.Short)} as block was ignored");
                _resetEvent.Set();
            }

            return this;
        }

        public ProcessingTestContext Recovered(Block block)
        {
            _recoveryStep.Allow(block.Hash!);
            return this;
        }

        public ProcessingTestContext CountIs(int expectedCount)
        {
            var count = ((IBlockProcessingQueue)_processor).Count;
            Assert.That(expectedCount, Is.EqualTo(count));
            return this;
        }

        public AfterBlock FullyProcessed(Block block)
        {
            return Suggested(block)
                .Recovered(block)
                .Processed(block);
        }

        public AfterBlock FullyProcessedSkipped(Block block)
        {
            return Suggested(block)
                .Recovered(block)
                .ProcessedSkipped(block);
        }

        public AfterBlock FullyProcessedFail(Block block)
        {
            return Suggested(block)
                .Recovered(block)
                .ProcessedFail(block);
        }

        public ProcessingTestContext QueueIsEmpty(int count)
        {
            _queueEmptyResetEvent.WaitOne(ProcessingWait);
            Assert.That(_processingQueueEmptyFired, Is.EqualTo(count), $"Processing queue fired {_processingQueueEmptyFired} times.");
            return this;
        }

        public class AfterBlock
        {
            public const int IgnoreWait = 200;

            private readonly ILogger _logger;
            private readonly Block _block;
            private readonly ProcessingTestContext _processingTestContext;

            public AfterBlock(ILogManager logManager, ProcessingTestContext processingTestContext, Block block)
            {
                _logger = logManager.GetClassLogger();
                _processingTestContext = processingTestContext;
                _block = block;
            }

            public ProcessingTestContext BecomesGenesis()
            {
                _logger.Info($"Waiting for {_block.ToString(Block.Format.Short)} to become genesis block");
                _processingTestContext._resetEvent.WaitOne(ProcessingWait);
                Assert.That(_processingTestContext._blockTree.Genesis!.Hash, Is.EqualTo(_block.Header.Hash), "genesis");
                return _processingTestContext;
            }

            public ProcessingTestContext BecomesNewHead()
            {
                _logger.Info($"Waiting for {_block.ToString(Block.Format.Short)} to become the new head block");
                _processingTestContext._resetEvent.WaitOne(ProcessingWait);
                Assert.That(() => _processingTestContext._blockTree.Head!.Hash, Is.EqualTo(_block.Header.Hash).After(1000, 100));
                return _processingTestContext;
            }

            public ProcessingTestContext IsKeptOnBranch()
            {
                _logger.Info($"Waiting for {_block.ToString(Block.Format.Short)} to be ignored");
                _processingTestContext._resetEvent.WaitOne(IgnoreWait);
                Assert.That(_processingTestContext._blockTree.Head!.Hash, Is.EqualTo(_processingTestContext._headBefore), "head");
                _logger.Info($"Finished waiting for {_block.ToString(Block.Format.Short)} to be ignored");
                return _processingTestContext;
            }

            public ProcessingTestContext IsDeletedAsInvalid()
            {
                _logger.Info($"Waiting for {_block.ToString(Block.Format.Short)} to be deleted");
                _processingTestContext._resetEvent.WaitOne(IgnoreWait);
                Assert.That(_processingTestContext._blockTree.Head!.Hash, Is.EqualTo(_processingTestContext._headBefore), "head");
                _logger.Info($"Finished waiting for {_block.ToString(Block.Format.Short)} to be deleted");
                Assert.That(_processingTestContext._blockTree.FindBlock(_block.Hash, BlockTreeLookupOptions.None), Is.Null);
                return _processingTestContext;
            }
        }

        public ProcessingTestContext Sleep(int milliseconds)
        {
            Thread.Sleep(milliseconds);
            return this;
        }

        public ProcessingTestContext AssertProcessedBlocks(params IEnumerable<Block> blocks)
        {
            _blockProcessor.Processed.Should().BeEquivalentTo(blocks.Select(b => b.Hash));
            return this;
        }

        public ProcessingTestContext StateSyncedTo(Block block4D8)
        {
            _stateReader.HasStateForRoot(block4D8.StateRoot!).Returns(true);
            return this;
        }
    }

    private static class When
    {
        public static ProcessingTestContext ProcessingBlocks => new(true);

        public static ProcessingTestContext ProcessorIsNotStarted => new(false);
    }

    private static readonly Block _block0 = Build.A.Block.WithNumber(0).WithNonce(0).WithDifficulty(0).TestObject;
    private static readonly Block _block1D2 = Build.A.Block.WithNumber(1).WithNonce(1).WithParent(_block0).WithDifficulty(2).TestObject;
    private static readonly Block _block2D4 = Build.A.Block.WithNumber(2).WithNonce(2).WithParent(_block1D2).WithDifficulty(2).TestObject;
    private static readonly Block _block3D6 = Build.A.Block.WithNumber(3).WithNonce(3).WithParent(_block2D4).WithDifficulty(2).TestObject;
    private static readonly Block _block4D8 = Build.A.Block.WithNumber(4).WithNonce(4).WithParent(_block3D6).WithDifficulty(2).TestObject;
    private static readonly Block _block5D10 = Build.A.Block.WithNumber(5).WithNonce(5).WithParent(_block4D8).WithDifficulty(2).TestObject;
    private static readonly Block _blockB2D4 = Build.A.Block.WithNumber(2).WithNonce(6).WithParent(_block1D2).WithDifficulty(2).TestObject;
    private static readonly Block _blockB3D8 = Build.A.Block.WithNumber(3).WithNonce(7).WithParent(_blockB2D4).WithDifficulty(4).TestObject;
    private static readonly Block _blockC2D100 = Build.A.Block.WithNumber(3).WithNonce(8).WithParent(_block1D2).WithDifficulty(98).TestObject;
    private static readonly Block _blockD2D200 = Build.A.Block.WithNumber(3).WithNonce(8).WithParent(_block1D2).WithDifficulty(198).TestObject;
    private static readonly Block _blockE2D300 = Build.A.Block.WithNumber(3).WithNonce(8).WithParent(_block1D2).WithDifficulty(298).TestObject;

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_ignore_lower_difficulty()
    {
        When.ProcessingBlocks
            .FullyProcessed(_block0).BecomesGenesis()
            .FullyProcessed(_block1D2).BecomesNewHead()
            .FullyProcessed(_blockB2D4).BecomesNewHead()
            .FullyProcessed(_blockB3D8).BecomesNewHead()
            .FullyProcessedSkipped(_block2D4).IsKeptOnBranch()
            .FullyProcessedSkipped(_block3D6).IsKeptOnBranch();
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_ignore_same_difficulty()
    {
        When.ProcessingBlocks
            .FullyProcessed(_block0).BecomesGenesis()
            .FullyProcessed(_block1D2).BecomesNewHead()
            .FullyProcessed(_block2D4).BecomesNewHead()
            .FullyProcessedSkipped(_blockB2D4).IsKeptOnBranch();
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_process_sequence()
    {
        When.ProcessingBlocks
            .FullyProcessed(_block0).BecomesGenesis()
            .FullyProcessed(_block1D2).BecomesNewHead()
            .FullyProcessed(_block2D4).BecomesNewHead()
            .FullyProcessed(_block3D6).BecomesNewHead()
            .FullyProcessed(_block4D8).BecomesNewHead();
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    [Explicit("Does not work on CI")]
    public void Will_update_metrics_on_processing()
    {
        long metricsBefore = Metrics.LastBlockProcessingTimeInMs;

        When.ProcessingBlocks
            .FullyProcessed(_block0).BecomesGenesis();

        long metricsAfter = Metrics.LastBlockProcessingTimeInMs;
        metricsAfter.Should().NotBe(metricsBefore);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_process_fast_sync_transition()
    {
        When.ProcessingBlocks
            .FullyProcessed(_block0).BecomesGenesis()
            .FullyProcessed(_block1D2).BecomesNewHead()
            .FullyProcessed(_block2D4).BecomesNewHead()
            .Suggested(_block3D6.Header)
            .FullyProcessed(_block4D8).BecomesNewHead();
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task Can_process_fast_sync()
    {
        BasicTestBlockchain testBlockchain = await BasicTestBlockchain.Create(configurer: builder =>
        {
            // Need the release spec to be fixed
            builder.AddSingleton<IChainHeadInfoProvider, IComponentContext>((ctx) =>
            {
                ISpecProvider specProvider = ctx.Resolve<ISpecProvider>();
                IBlockTree blockTree = ctx.Resolve<IBlockTree>();
                IReadOnlyStateProvider readOnlyState = ctx.Resolve<IReadOnlyStateProvider>();
                return new ChainHeadInfoProvider(
                    new FixedForkActivationChainHeadSpecProvider(specProvider, fixedBlock: 10_000_000),
                    blockTree,
                    readOnlyState,
                    new CodeInfoRepository())
                {
                    HasSynced = true
                };
            });
        });
        await testBlockchain.BuildSomeBlocks(5);

        When.ProcessingBlocks
            .FullyProcessed(testBlockchain.BlockTree.FindBlock(0)!).BecomesGenesis()
            .SuggestedWithoutProcessingAndMoveToMain(testBlockchain.BlockTree.FindBlock(1)!)
            .SuggestedWithoutProcessingAndMoveToMain(testBlockchain.BlockTree.FindBlock(2)!)
            .SuggestedWithoutProcessingAndMoveToMain(testBlockchain.BlockTree.FindBlock(3)!)
            .SuggestedWithoutProcessingAndMoveToMain(testBlockchain.BlockTree.FindBlock(4)!)
            .FullyProcessed(testBlockchain.BlockTree.FindBlock(5)!).BecomesNewHead()
            .AssertProcessedBlocks(
                testBlockchain.BlockTree.FindBlock(0)!,
                testBlockchain.BlockTree.FindBlock(1)!,
                testBlockchain.BlockTree.FindBlock(2)!,
                testBlockchain.BlockTree.FindBlock(3)!,
                testBlockchain.BlockTree.FindBlock(4)!,
                testBlockchain.BlockTree.FindBlock(5)!
            );
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_reorganize_just_head_block_twice()
    {
        When.ProcessingBlocks
            .FullyProcessed(_block0).BecomesGenesis()
            .FullyProcessed(_block1D2).BecomesNewHead()
            .FullyProcessed(_block2D4).BecomesNewHead()
            .FullyProcessed(_blockC2D100).BecomesNewHead()
            .FullyProcessed(_blockD2D200).BecomesNewHead()
            .FullyProcessed(_blockE2D300).BecomesNewHead();
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_reorganize_there_and_back()
    {
        When.ProcessingBlocks
            .FullyProcessed(_block0).BecomesGenesis()
            .FullyProcessed(_block1D2).BecomesNewHead()
            .FullyProcessed(_block2D4).BecomesNewHead()
            .FullyProcessed(_block3D6).BecomesNewHead()
            .FullyProcessedSkipped(_blockB2D4).IsKeptOnBranch()
            .FullyProcessed(_blockB3D8).BecomesNewHead()
            .FullyProcessedSkipped(_block4D8).IsKeptOnBranch()
            .FullyProcessed(_block5D10).BecomesNewHead();
    }

    [Test, MaxTime(Timeout.MaxTestTime), Retry(3)]
    public void Can_reorganize_to_longer_path()
    {
        When.ProcessingBlocks
            .FullyProcessed(_block0).BecomesGenesis()
            .FullyProcessed(_block1D2).BecomesNewHead()
            .FullyProcessed(_blockB2D4).BecomesNewHead()
            .FullyProcessed(_blockB3D8).BecomesNewHead()
            .FullyProcessedSkipped(_block2D4).IsKeptOnBranch()
            .FullyProcessedSkipped(_block3D6).IsKeptOnBranch()
            .FullyProcessedSkipped(_block4D8).IsKeptOnBranch()
            .FullyProcessed(_block5D10).BecomesNewHead();
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_reorganize_to_same_length()
    {
        When.ProcessingBlocks
            .FullyProcessed(_block0).BecomesGenesis()
            .FullyProcessed(_block1D2).BecomesNewHead()
            .FullyProcessed(_block2D4).BecomesNewHead()
            .FullyProcessed(_block3D6).BecomesNewHead()
            .FullyProcessedSkipped(_blockB2D4).IsKeptOnBranch()
            .FullyProcessed(_blockB3D8).BecomesNewHead();
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_reorganize_to_shorter_path()
    {
        When.ProcessingBlocks
            .FullyProcessed(_block0).BecomesGenesis()
            .FullyProcessed(_block1D2).BecomesNewHead()
            .FullyProcessed(_block2D4).BecomesNewHead()
            .FullyProcessed(_block3D6).BecomesNewHead()
            .FullyProcessed(_blockC2D100).BecomesNewHead();
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    [Retry(3)] // some flakiness
    public void Can_change_branch_on_invalid_block()
    {
        When.ProcessingBlocks
            .FullyProcessed(_block0).BecomesGenesis()
            .FullyProcessed(_block1D2).BecomesNewHead()
            .FullyProcessedFail(_block2D4).IsDeletedAsInvalid()
            .FullyProcessed(_blockB2D4).BecomesNewHead();
    }

    [Test(Description = "Covering scenario when we have an invalid block followed by its descendants." +
                        "All the descandant blocks should get discarded and an alternative branch should get selected." +
                        "BRANCH A | BLOCK 2 | INVALID |  DISCARD" +
                        "BRANCH A | BLOCK 3 |   VALID |  DISCARD" +
                        "BRANCH A | BLOCK 4 |   VALID |  DISCARD" +
                        "BRANCH B | BLOCK 2 |   VALID | NEW HEAD"), MaxTime(Timeout.MaxTestTime)]
    public void Can_change_branch_on_invalid_block_when_invalid_branch_is_in_the_queue()
    {
        When.ProcessingBlocks
            .FullyProcessed(_block0).BecomesGenesis()
            .Suggested(_block1D2)
            .Suggested(_block2D4)
            .Suggested(_block3D6)
            .Suggested(_block4D8)
            .Recovered(_block1D2)
            .Recovered(_block2D4)
            .Recovered(_block3D6)
            .Recovered(_block4D8)
            .Processed(_block1D2).BecomesNewHead()
            .ProcessedFail(_block2D4).IsDeletedAsInvalid()
            .ProcessedSkipped(_block3D6).IsDeletedAsInvalid()
            .ProcessedSkipped(_block4D8).IsDeletedAsInvalid()
            .FullyProcessed(_blockB2D4).BecomesNewHead();
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_change_branch_on_invalid_block_when_invalid_branch_is_in_the_queue_and_recovery_queue_max_has_been_reached()
    {
        When.ProcessingBlocks
            .AndRecoveryQueueLimitHasBeenReached()
            .FullyProcessed(_block0).BecomesGenesis()
            .Suggested(_block1D2)
            .Suggested(_block2D4)
            .Suggested(_block3D6)
            .Suggested(_block4D8)
            .Recovered(_block1D2)
            .Recovered(_block2D4)
            .Processed(_block1D2).BecomesNewHead()
            .ProcessedFail(_block2D4).IsDeletedAsInvalid()
            .Recovered(_block3D6)
            .Recovered(_block4D8)
            .ProcessedSkipped(_block3D6).IsDeletedAsInvalid()
            .ProcessedSkipped(_block4D8).IsDeletedAsInvalid()
            .FullyProcessed(_blockB2D4).BecomesNewHead();
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    [Ignore("Not implemented yet - scenario when from suggested blocks we can see that previously suggested will not be winning")]
    [Todo(Improve.Performance, "We can skip processing losing branches by implementing code to pass this test")]
    public void Never_process_branches_that_are_known_to_lose_in_the_future()
    {
        // this can be solved easily by resetting the hash to follow whenever suggesting a block that is not a child of the previously suggested block
        When.ProcessingBlocks
            .FullyProcessed(_block0).BecomesGenesis()
            .Suggested(_block1D2)
            .Suggested(_block2D4)
            .Suggested(_block3D6)
            .Suggested(_blockB2D4)
            .Suggested(_blockB3D8)
            .Recovered(_block1D2)
            .Recovered(_block2D4)
            .Recovered(_block3D6)
            .Recovered(_blockB2D4)
            .Recovered(_blockB3D8)
            .Processed(_block1D2).BecomesNewHead()
            .ProcessedSkipped(_block2D4).IsKeptOnBranch();
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void IsProcessingBlocks_returns_true_when_processing_blocks()
    {
        When.ProcessingBlocks
            .IsProcessingBlocks(true, 1)
            .FullyProcessed(_block0).BecomesGenesis()
            .FullyProcessed(_block1D2).BecomesNewHead()
            .IsProcessingBlocks(true, 1)
            .FullyProcessed(_block2D4).BecomesNewHead()
            .IsProcessingBlocks(true, 1)
            .FullyProcessed(_block3D6).BecomesNewHead()
            .IsProcessingBlocks(true, 1);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void IsProcessingBlocks_returns_false_when_max_interval_elapsed()
    {
        When.ProcessingBlocks
            .IsProcessingBlocks(true, 1)
            .FullyProcessed(_block0).BecomesGenesis()
            .FullyProcessed(_block1D2).BecomesNewHead()
            .IsProcessingBlocks(true, 1)
            .FullyProcessed(_block2D4).BecomesNewHead()
            .Sleep(2000)
            .IsProcessingBlocks(false, 1)
            .FullyProcessed(_block3D6).BecomesNewHead()
            .IsProcessingBlocks(true, 1);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void ProcessorIsNotStarted_returns_false()
    {
        When.ProcessorIsNotStarted
            .IsProcessingBlocks(false, 10)
            .Sleep(1000)
            .IsProcessingBlocks(false, 10);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void QueueCount_returns_correctly()
    {
        When.ProcessingBlocks
            .QueueIsEmpty(1)
            .FullyProcessed(_block0)
            .BecomesGenesis()
            .QueueIsEmpty(2)


            .Suggested(_block1D2)
            .Recovered(_block1D2)
            .CountIs(1)

            .Suggested(_block2D4)
            .Suggested(_block3D6)
            .Recovered(_block2D4)
            .Recovered(_block3D6)
            .CountIs(3)

            .Processed(_block1D2)
            .BecomesNewHead()
            .Sleep(10)
            .CountIs(2)
            .ProcessedFail(_block2D4)
            .IsDeletedAsInvalid()
            .ProcessedSkipped(_block3D6)
            .IsDeletedAsInvalid()
            .Sleep(10)
            .CountIs(0)
            .QueueIsEmpty(3);
    }

    [Test]
    public void ProcessingLongRangeFastSync_ProcessOnlyLastBlock()
    {
        When.ProcessingBlocks
            .QueueIsEmpty(1)

            .FullyProcessed(_block0).BecomesNewHead()
            .FullyProcessed(_block1D2).BecomesNewHead()

            .Suggested(_block2D4, BlockTreeSuggestOptions.None)
            .Suggested(_block3D6, BlockTreeSuggestOptions.None)
            .Suggested(_block4D8, BlockTreeSuggestOptions.None)
            .StateSyncedTo(_block4D8)

            .FullyProcessed(_block5D10).BecomesNewHead()

            .AssertProcessedBlocks(
                _block0,
                _block1D2,
                _block5D10
            );
    }
}
