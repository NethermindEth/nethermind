// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State.Repositories;
using Nethermind.Db.Blooms;
using Nethermind.State;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.Self)]
    public class BlockchainProcessorTests
    {
        private class ProcessingTestContext
        {
            private ILogManager _logManager = LimboLogs.Instance;

            private class BlockProcessorMock : IBlockProcessor
            {
                private ILogger _logger;

                private HashSet<Keccak> _allowed = new();

                private HashSet<Keccak> _allowedToFail = new();

                private HashSet<Keccak> _rootProcessed = new();

                public BlockProcessorMock(ILogManager logManager, IStateReader stateReader)
                {
                    _logger = logManager.GetClassLogger();
                    stateReader.When(it =>
                            it.RunTreeVisitor(Arg.Any<ITreeVisitor>(), Arg.Any<Keccak>(), Arg.Any<VisitingOptions>()))
                        .Do((info =>
                        {
                            // Simulate state root check
                            ITreeVisitor visitor = (ITreeVisitor)info[0];
                            Keccak stateRoot = (Keccak)info[1];
                            if (!_rootProcessed.Contains(stateRoot)) visitor.VisitMissingNode(stateRoot, new TrieVisitContext());
                        }));
                }

                public void Allow(Keccak hash)
                {
                    _logger.Info($"Allowing {hash} to process");
                    _allowed.Add(hash);
                }

                public void AllowToFail(Keccak hash)
                {
                    _logger.Info($"Allowing {hash} to fail");
                    _allowedToFail.Add(hash);
                }

                public Block[] Process(Keccak newBranchStateRoot, List<Block> suggestedBlocks, ProcessingOptions processingOptions, IBlockTracer blockTracer)
                {
                    if (blockTracer != NullBlockTracer.Instance)
                    {
                        // this is for block reruns on failure for diag tracing
                        throw new InvalidBlockException(suggestedBlocks[0]);
                    }

                    _logger.Info($"Processing {suggestedBlocks.Last().ToString(Block.Format.Short)}");
                    while (true)
                    {
                        bool notYet = false;
                        for (int i = 0; i < suggestedBlocks.Count; i++)
                        {
                            BlocksProcessing?.Invoke(this, new BlocksProcessingEventArgs(suggestedBlocks));
                            Block suggestedBlock = suggestedBlocks[i];
                            Keccak hash = suggestedBlock.Hash!;
                            if (!_allowed.Contains(hash))
                            {
                                if (_allowedToFail.Contains(hash))
                                {
                                    _allowedToFail.Remove(hash);
                                    BlockProcessed?.Invoke(this, new BlockProcessedEventArgs(suggestedBlocks.Last(), Array.Empty<TxReceipt>()));
                                    throw new InvalidBlockException(suggestedBlock);
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
                            _rootProcessed.Add(suggestedBlocks.Last().StateRoot);
                            BlockProcessed?.Invoke(this, new BlockProcessedEventArgs(suggestedBlocks.Last(), Array.Empty<TxReceipt>()));
                            return suggestedBlocks.ToArray();
                        }
                    }
                }

                public event EventHandler<BlocksProcessingEventArgs> BlocksProcessing;

                public event EventHandler<BlockProcessedEventArgs> BlockProcessed;

                public event EventHandler<TxProcessedEventArgs> TransactionProcessed
                {
                    add { }
                    remove { }
                }
            }

            private class RecoveryStepMock : IBlockPreprocessorStep
            {
                private ILogger _logger;

                private ConcurrentDictionary<Keccak, object> _allowed = new();

                private ConcurrentDictionary<Keccak, object> _allowedToFail = new();

                public RecoveryStepMock(ILogManager logManager)
                {
                    _logger = logManager.GetClassLogger();
                }

                public void Allow(Keccak hash)
                {
                    _logger.Info($"Allowing {hash} to recover");
                    _allowed[hash] = new object();
                }

                public void AllowToFail(Keccak hash)
                {
                    _logger.Info($"Allowing {hash} to fail recover");
                    _allowedToFail[hash] = new object();
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
                        if (!_allowed.ContainsKey(block.Hash))
                        {
                            if (_allowedToFail.ContainsKey(block.Hash))
                            {
                                _allowedToFail.Remove(block.Hash, out _);
                                throw new Exception();
                            }

                            Thread.Sleep(20);
                            continue;
                        }

                        block.Header.Author = Address.Zero;
                        _allowed.Remove(block.Hash, out _);
                        return;
                    }
                }
            }

            private BlockTree _blockTree;
            private AutoResetEvent _resetEvent;
            private AutoResetEvent _queueEmptyResetEvent;
            private BlockProcessorMock _blockProcessor;
            private RecoveryStepMock _recoveryStep;
            private BlockchainProcessor _processor;
            private ILogger _logger;
            private Keccak _headBefore;
            private int _processingQueueEmptyFired;
            public const int ProcessingWait = 2000;

            public ProcessingTestContext(bool startProcessor)
            {
                _logger = _logManager.GetClassLogger();
                MemDb blockDb = new();
                MemDb blockInfoDb = new();
                MemDb headersDb = new();

                IStateReader stateReader = Substitute.For<IStateReader>();

                _blockTree = new BlockTree(blockDb, headersDb, blockInfoDb, new ChainLevelInfoRepository(blockInfoDb), MainnetSpecProvider.Instance, NullBloomStorage.Instance, LimboLogs.Instance);
                _blockProcessor = new BlockProcessorMock(_logManager, stateReader);
                _recoveryStep = new RecoveryStepMock(_logManager);
                _processor = new BlockchainProcessor(_blockTree, _blockProcessor, _recoveryStep, stateReader, LimboLogs.Instance, BlockchainProcessor.Options.Default);
                _resetEvent = new AutoResetEvent(false);
                _queueEmptyResetEvent = new AutoResetEvent(false);

                _processor.ProcessingQueueEmpty += (_, _) =>
                {
                    _processingQueueEmptyFired++;
                    _queueEmptyResetEvent.Set();
                };

                _blockTree.NewHeadBlock += (sender, args) =>
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
                Assert.AreEqual(expectedIsProcessingBlocks, actual);
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
                _blockProcessor.BlockProcessed += (sender, args) =>
                {
                    if (args.Block.Hash == block.Hash)
                    {
                        wasProcessed = true;
                        processedEvent.Set();
                    }
                };

                _logger.Info($"Waiting for {block.ToString(Block.Format.Short)} to process");
                _blockProcessor.Allow(block.Hash);
                processedEvent.WaitOne(ProcessingWait);
                Assert.True(wasProcessed, $"Expected this block to get processed but it was not: {block.ToString(Block.Format.Short)}");

                return new AfterBlock(_logManager, this, block);
            }

            public AfterBlock ProcessedSkipped(Block block)
            {
                _headBefore = _blockTree.Head?.Hash;
                _logger.Info($"Waiting for {block.ToString(Block.Format.Short)} to be skipped");
                _blockProcessor.Allow(block.Hash);
                return new AfterBlock(_logManager, this, block);
            }

            public AfterBlock ProcessedFail(Block block)
            {
                _headBefore = _blockTree.Head?.Hash;
                ManualResetEvent processedEvent = new(false);
                bool wasProcessed = false;
                _blockProcessor.BlockProcessed += (sender, args) =>
                {
                    if (args.Block.Hash == block.Hash)
                    {
                        wasProcessed = true;
                        processedEvent.Set();
                    }
                };

                _logger.Info($"Waiting for {block.ToString(Block.Format.Short)} to fail processing");
                _blockProcessor.AllowToFail(block.Hash);
                processedEvent.WaitOne(ProcessingWait);
                Assert.True(wasProcessed, $"Block was never processed {block.ToString(Block.Format.Short)}");
                Assert.AreEqual(_headBefore, _blockTree.Head?.Hash, $"Processing did not fail - {block.ToString(Block.Format.Short)} became a new head block");
                _logger.Info($"Finished waiting for {block.ToString(Block.Format.Short)} to fail processing");
                return new AfterBlock(_logManager, this, block);
            }

            public ProcessingTestContext Suggested(Block block)
            {
                AddBlockResult result = _blockTree.SuggestBlock(block);
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
                _blockProcessor.Allow(block.Hash);
                _recoveryStep.Allow(block.Hash);

                return this;
            }

            public ProcessingTestContext StartProcessor()
            {
                _processor.Start();

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
                _recoveryStep.Allow(block.Hash);
                return this;
            }

            public ProcessingTestContext CountIs(int expectedCount)
            {
                var count = ((IBlockProcessingQueue)_processor).Count;
                Assert.AreEqual(count, expectedCount);
                return this;
            }

            public ProcessingTestContext ThenRecoveredFail(Block block)
            {
                _recoveryStep.AllowToFail(block.Hash);
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
                Assert.AreEqual(count, _processingQueueEmptyFired, $"Processing queue fired {_processingQueueEmptyFired} times.");
                return this;
            }

            public class AfterBlock
            {
                private ILogger _logger;
                public const int IgnoreWait = 200;
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
                    Assert.AreEqual(_block.Header.Hash, _processingTestContext._blockTree.Genesis.Hash, "genesis");
                    return _processingTestContext;
                }

                public ProcessingTestContext BecomesNewHead()
                {
                    _logger.Info($"Waiting for {_block.ToString(Block.Format.Short)} to become the new head block");
                    _processingTestContext._resetEvent.WaitOne(ProcessingWait);
                    Assert.That(() => _processingTestContext._blockTree.Head.Hash, Is.EqualTo(_block.Header.Hash).After(1000, 100));
                    return _processingTestContext;
                }

                public ProcessingTestContext IsKeptOnBranch()
                {
                    _logger.Info($"Waiting for {_block.ToString(Block.Format.Short)} to be ignored");
                    _processingTestContext._resetEvent.WaitOne(IgnoreWait);
                    Assert.AreEqual(_processingTestContext._headBefore, _processingTestContext._blockTree.Head.Hash, "head");
                    _logger.Info($"Finished waiting for {_block.ToString(Block.Format.Short)} to be ignored");
                    return _processingTestContext;
                }

                public ProcessingTestContext IsDeletedAsInvalid()
                {
                    _logger.Info($"Waiting for {_block.ToString(Block.Format.Short)} to be deleted");
                    _processingTestContext._resetEvent.WaitOne(IgnoreWait);
                    Assert.AreEqual(_processingTestContext._headBefore, _processingTestContext._blockTree.Head.Hash, "head");
                    _logger.Info($"Finished waiting for {_block.ToString(Block.Format.Short)} to be deleted");
                    Assert.Null(_processingTestContext._blockTree.FindBlock(_block.Hash, BlockTreeLookupOptions.None));
                    return _processingTestContext;
                }
            }

            public ProcessingTestContext Sleep(int milliseconds)
            {
                Thread.Sleep(milliseconds);
                return this;
            }
        }

        private static class When
        {
            public static ProcessingTestContext ProcessingBlocks => new(true);

            public static ProcessingTestContext ProcessorIsNotStarted => new(false);
        }

        private static Block _block0 = Build.A.Block.WithNumber(0).WithNonce(0).WithDifficulty(0).TestObject;
        private static Block _block1D2 = Build.A.Block.WithNumber(1).WithNonce(1).WithParent(_block0).WithDifficulty(2).TestObject;
        private static Block _block2D4 = Build.A.Block.WithNumber(2).WithNonce(2).WithParent(_block1D2).WithDifficulty(2).TestObject;
        private static Block _block3D6 = Build.A.Block.WithNumber(3).WithNonce(3).WithParent(_block2D4).WithDifficulty(2).TestObject;
        private static Block _block4D8 = Build.A.Block.WithNumber(4).WithNonce(4).WithParent(_block3D6).WithDifficulty(2).TestObject;
        private static Block _block5D10 = Build.A.Block.WithNumber(5).WithNonce(5).WithParent(_block4D8).WithDifficulty(2).TestObject;
        private static Block _blockB2D4 = Build.A.Block.WithNumber(2).WithNonce(6).WithParent(_block1D2).WithDifficulty(2).TestObject;
        private static Block _blockB3D8 = Build.A.Block.WithNumber(3).WithNonce(7).WithParent(_blockB2D4).WithDifficulty(4).TestObject;
        private static Block _blockC2D100 = Build.A.Block.WithNumber(3).WithNonce(8).WithParent(_block1D2).WithDifficulty(98).TestObject;
        private static Block _blockD2D200 = Build.A.Block.WithNumber(3).WithNonce(8).WithParent(_block1D2).WithDifficulty(198).TestObject;
        private static Block _blockE2D300 = Build.A.Block.WithNumber(3).WithNonce(8).WithParent(_block1D2).WithDifficulty(298).TestObject;

        [Test, Timeout(Timeout.MaxTestTime)]
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

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Can_ignore_same_difficulty()
        {
            When.ProcessingBlocks
                .FullyProcessed(_block0).BecomesGenesis()
                .FullyProcessed(_block1D2).BecomesNewHead()
                .FullyProcessed(_block2D4).BecomesNewHead()
                .FullyProcessedSkipped(_blockB2D4).IsKeptOnBranch();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Can_process_sequence()
        {
            When.ProcessingBlocks
                .FullyProcessed(_block0).BecomesGenesis()
                .FullyProcessed(_block1D2).BecomesNewHead()
                .FullyProcessed(_block2D4).BecomesNewHead()
                .FullyProcessed(_block3D6).BecomesNewHead()
                .FullyProcessed(_block4D8).BecomesNewHead();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        [Explicit("Does not work on CI")]
        public void Will_update_metrics_on_processing()
        {
            long metricsBefore = Metrics.LastBlockProcessingTimeInMs;

            When.ProcessingBlocks
                .FullyProcessed(_block0).BecomesGenesis();

            long metricsAfter = Metrics.LastBlockProcessingTimeInMs;
            metricsAfter.Should().NotBe(metricsBefore);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Can_process_fast_sync_transition()
        {
            When.ProcessingBlocks
                .FullyProcessed(_block0).BecomesGenesis()
                .FullyProcessed(_block1D2).BecomesNewHead()
                .FullyProcessed(_block2D4).BecomesNewHead()
                .Suggested(_block3D6.Header)
                .FullyProcessed(_block4D8).BecomesNewHead();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public async Task Can_process_fast_sync()
        {
            BasicTestBlockchain testBlockchain = await BasicTestBlockchain.Create();
            await testBlockchain.BuildSomeBlocks(5);

            When.ProcessingBlocks
                .FullyProcessed(testBlockchain.BlockTree.FindBlock(0)).BecomesGenesis()
                .SuggestedWithoutProcessingAndMoveToMain(testBlockchain.BlockTree.FindBlock(1))
                .SuggestedWithoutProcessingAndMoveToMain(testBlockchain.BlockTree.FindBlock(2))
                .SuggestedWithoutProcessingAndMoveToMain(testBlockchain.BlockTree.FindBlock(3))
                .SuggestedWithoutProcessingAndMoveToMain(testBlockchain.BlockTree.FindBlock(4))
                .FullyProcessed(testBlockchain.BlockTree.FindBlock(5)).BecomesNewHead();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
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

        [Test, Timeout(Timeout.MaxTestTime)]
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

        [Test, Timeout(Timeout.MaxTestTime), Retry(3)]
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

        [Test, Timeout(Timeout.MaxTestTime)]
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

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Can_reorganize_to_shorter_path()
        {
            When.ProcessingBlocks
                .FullyProcessed(_block0).BecomesGenesis()
                .FullyProcessed(_block1D2).BecomesNewHead()
                .FullyProcessed(_block2D4).BecomesNewHead()
                .FullyProcessed(_block3D6).BecomesNewHead()
                .FullyProcessed(_blockC2D100).BecomesNewHead();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
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
                            "BRANCH B | BLOCK 2 |   VALID | NEW HEAD"), Timeout(Timeout.MaxTestTime)]
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

        [Test, Timeout(Timeout.MaxTestTime)]
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

        [Test, Timeout(Timeout.MaxTestTime)]
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

        [Test, Timeout(Timeout.MaxTestTime)]
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

        [Test, Timeout(Timeout.MaxTestTime)]
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

        [Test, Timeout(Timeout.MaxTestTime)]
        public void ProcessorIsNotStarted_returns_false()
        {
            When.ProcessorIsNotStarted
                .IsProcessingBlocks(false, 10)
                .Sleep(1000)
                .IsProcessingBlocks(false, 10);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
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
    }
}
