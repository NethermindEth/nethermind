// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using ConcurrentCollections;
using FluentAssertions;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class BlockchainProcessorTests
{
    private class ProcessingTestContext
    {
        private readonly ILogManager _logManager = LimboLogs.Instance;

        // Thread-safe per-instance trace log for debugging race conditions.
        private readonly string _traceLogPath = $"/tmp/bpt-trace-{Guid.NewGuid():N}.log";
        private readonly object _traceLock = new();
        private readonly long _traceStart = Environment.TickCount64;

        private void Trace(string msg)
        {
            long elapsed = Environment.TickCount64 - _traceStart;
            string line = $"[{elapsed,7}ms] [T{Environment.CurrentManagedThreadId,3}] {msg}";
            lock (_traceLock)
            {
                System.IO.File.AppendAllText(_traceLogPath, line + "\n");
            }
        }

        private class BranchProcessorMock : IBranchProcessor
        {
            private readonly ILogger _logger;
            private readonly Action<string> _trace;

            private readonly ConcurrentHashSet<Hash256> _allowed = new();

            internal readonly HashSet<Hash256> Processed = new();

            private readonly ConcurrentHashSet<Hash256> _allowedToFail = new();

            private readonly HashSet<Hash256> _rootProcessed = new();

            private readonly object _gate = new(); // Must be object — Monitor.PulseAll/Wait require it

            public BranchProcessorMock(ILogManager logManager, IStateReader stateReader, Action<string> trace)
            {
                _logger = logManager.GetClassLogger();
                _trace = trace;
                stateReader.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(x => _rootProcessed.Contains(((BlockHeader?)x[0])?.StateRoot!));
            }

            public void Allow(Hash256 hash)
            {
                _trace($"BranchProcessor.Allow({hash.ToString()[..12]})");
                _logger.Info($"Allowing {hash} to process");
                lock (_gate)
                {
                    _allowed.Add(hash);
                    Monitor.PulseAll(_gate);
                }
            }

            public void AllowToFail(Hash256 hash)
            {
                _trace($"BranchProcessor.AllowToFail({hash.ToString()[..12]})");
                _logger.Info($"Allowing {hash} to fail");
                lock (_gate)
                {
                    _allowedToFail.Add(hash);
                    Monitor.PulseAll(_gate);
                }
            }

            public Block[] Process(BlockHeader? baseBlock, IReadOnlyList<Block> suggestedBlocks, ProcessingOptions processingOptions, IBlockTracer blockTracer, CancellationToken token)
            {
                if (blockTracer != NullBlockTracer.Instance)
                {
                    // this is for block reruns on failure for diag tracing
                    throw new InvalidBlockException(suggestedBlocks[0], "wrong tracer");
                }

                Processed.AddRange(suggestedBlocks.Select(x => x.Hash!));

                _trace($"BranchProcessor.Process() called with {suggestedBlocks.Count} blocks: [{string.Join(", ", suggestedBlocks.Select(b => b.ToString(Block.Format.Short)))}]");
                _logger.Info($"Processing {suggestedBlocks.Last().ToString(Block.Format.Short)}");
                int nextBlock = 0;
                while (true)
                {
                    lock (_gate)
                    {
                        bool notYet = false;
                        for (int i = nextBlock; i < suggestedBlocks.Count; i++)
                        {
                            BlocksProcessing?.Invoke(this, new BlocksProcessingEventArgs(suggestedBlocks));
                            Block suggestedBlock = suggestedBlocks[i];
                            BlockProcessing?.Invoke(this, new BlockEventArgs(suggestedBlock));
                            Hash256 hash = suggestedBlock.Hash!;
                            if (!_allowed.Contains(hash))
                            {
                                if (_allowedToFail.TryRemove(hash))
                                {
                                    _trace($"BranchProcessor: block {suggestedBlock.ToString(Block.Format.Short)} FAIL (in allowedToFail)");
                                    BlockProcessed?.Invoke(this, new BlockProcessedEventArgs(suggestedBlock, []));
                                    throw new InvalidBlockException(suggestedBlock, "allowed to fail");
                                }

                                _trace($"BranchProcessor: block {suggestedBlock.ToString(Block.Format.Short)} NOT YET (not in allowed or allowedToFail), waiting 200ms");
                                notYet = true;
                                break;
                            }

                            _trace($"BranchProcessor: block {suggestedBlock.ToString(Block.Format.Short)} ALLOWED, processing");
                            BlockProcessed?.Invoke(this, new BlockProcessedEventArgs(suggestedBlock, []));
                            nextBlock = i + 1;
                        }

                        if (notYet)
                        {
                            Monitor.Wait(_gate, MockRecheckInterval);
                        }
                        else
                        {
                            _trace($"BranchProcessor: all blocks processed, returning");
                            _rootProcessed.Add(suggestedBlocks.Last().StateRoot!);
                            return suggestedBlocks.ToArray();
                        }
                    }
                }
            }

            public event EventHandler<BlocksProcessingEventArgs>? BlocksProcessing;

            public event EventHandler<BlockEventArgs>? BlockProcessing;

            public event EventHandler<BlockProcessedEventArgs>? BlockProcessed;
        }

        private class RecoveryStepMock : IBlockPreprocessorStep
        {
            private readonly ILogger _logger;
            private readonly Action<string> _trace;
            private readonly ConcurrentDictionary<Hash256, object> _allowed = new();
            private readonly ConcurrentDictionary<Hash256, object> _allowedToFail = new();
            private readonly object _gate = new(); // Must be object — Monitor.PulseAll/Wait require it

            public RecoveryStepMock(ILogManager logManager, Action<string> trace)
            {
                _logger = logManager.GetClassLogger();
                _trace = trace;
            }

            public void Allow(Hash256 hash)
            {
                _trace($"Recovery.Allow({hash.ToString()[..12]})");
                _logger.Info($"Allowing {hash} to recover");
                lock (_gate)
                {
                    _allowed[hash] = new object();
                    Monitor.PulseAll(_gate);
                }
            }

            public void RecoverData(Block block)
            {
                _trace($"Recovery.RecoverData({block.ToString(Block.Format.Short)}) enter, Author={block.Author}");
                _logger.Info($"Recovering data for {block.ToString(Block.Format.Short)}");
                if (block.Author is not null)
                {
                    _trace($"Recovery.RecoverData({block.ToString(Block.Format.Short)}) already recovered, returning");
                    _logger.Info($"Data was already there for {block.ToString(Block.Format.Short)}");
                    return;
                }

                int waitCount = 0;
                while (true)
                {
                    lock (_gate)
                    {
                        Hash256 blockHash = block.Hash!;
                        if (!_allowed.ContainsKey(blockHash))
                        {
                            if (_allowedToFail.ContainsKey(blockHash))
                            {
                                _trace($"Recovery.RecoverData({block.ToString(Block.Format.Short)}) FAIL");
                                _allowedToFail.Remove(blockHash, out _);
                                throw new Exception();
                            }

                            waitCount++;
                            if (waitCount % 50 == 1)
                                _trace($"Recovery.RecoverData({block.ToString(Block.Format.Short)}) waiting (count={waitCount})");
                            Monitor.Wait(_gate, MockRecheckInterval);
                            continue;
                        }

                        _trace($"Recovery.RecoverData({block.ToString(Block.Format.Short)}) done after {waitCount} waits");
                        block.Header.Author = Address.Zero;
                        _allowed.Remove(blockHash, out _);
                        return;
                    }
                }
            }
        }

        private readonly BlockTree _blockTree;
        private readonly AutoResetEvent _resetEvent;
        private readonly AutoResetEvent _queueEmptyResetEvent;
        private readonly IStateReader _stateReader;
        private readonly BranchProcessorMock _branchProcessor;
        private readonly RecoveryStepMock _recoveryStep;
        private readonly BlockchainProcessor _processor;
        private readonly ILogger _logger;

        private Hash256? _headBefore;
        private int _processingQueueEmptyFired;
        private const int ProcessingWait = 10_000;
        private const int MockRecheckInterval = 200;

        public ProcessingTestContext(bool startProcessor)
        {
            _logger = _logManager.GetClassLogger();
            _stateReader = Substitute.For<IStateReader>();

            Trace($"=== ProcessingTestContext created, logFile={_traceLogPath} ===");

            _blockTree = Build.A.BlockTree()
                .WithoutSettingHead
                .TestObject;
            _branchProcessor = new BranchProcessorMock(_logManager, _stateReader, Trace);
            _recoveryStep = new RecoveryStepMock(_logManager, Trace);
            _processor = new BlockchainProcessor(_blockTree, _branchProcessor, _recoveryStep, _stateReader, LimboLogs.Instance, BlockchainProcessor.Options.Default, Substitute.For<IProcessingStats>());
            _processor.DebugTrace = Trace;
            _resetEvent = new AutoResetEvent(false);
            _queueEmptyResetEvent = new AutoResetEvent(false);

            _processor.ProcessingQueueEmpty += (_, _) =>
            {
                _processingQueueEmptyFired++;
                _queueEmptyResetEvent.Set();
            };

            _processor.BlockAdded += (_, args) =>
            {
                Trace($"BlockchainProcessor.BlockAdded: {args.Block.ToString(Block.Format.Short)}, queueCount={_processor.Count}");
            };

            _blockTree.NewHeadBlock += (_, args) =>
            {
                Trace($"NewHeadBlock: {args.Block.ToString(Block.Format.Short)}");
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
            _branchProcessor.BlockProcessed += (_, args) =>
            {
                if (args.Block.Hash == block.Hash)
                {
                    wasProcessed = true;
                    processedEvent.Set();
                }
            };

            Trace($"Processed({block.ToString(Block.Format.Short)}) enter");
            long procStart = Environment.TickCount64;
            _branchProcessor.Allow(block.Hash!);
            processedEvent.WaitOne(ProcessingWait);
            long procElapsed = Environment.TickCount64 - procStart;
            Trace($"Processed({block.ToString(Block.Format.Short)}) done: {procElapsed}ms, wasProcessed={wasProcessed}");
            Assert.That(wasProcessed, Is.True, $"Expected this block to get processed but it was not: {block.ToString(Block.Format.Short)} (waited {procElapsed}ms, trace={_traceLogPath})");

            return new AfterBlock(_logManager, this, block);
        }

        public AfterBlock ProcessedSkipped(Block block)
        {
            _headBefore = _blockTree.Head?.Hash;
            _logger.Info($"Waiting for {block.ToString(Block.Format.Short)} to be skipped");
            _branchProcessor.Allow(block.Hash!);
            return new AfterBlock(_logManager, this, block);
        }

        public AfterBlock ProcessedFail(Block block)
        {
            _headBefore = _blockTree.Head?.Hash;
            ManualResetEvent processedEvent = new(false);
            bool wasProcessed = false;
            _branchProcessor.BlockProcessed += (_, args) =>
            {
                if (args.Block.Hash == block.Hash)
                {
                    wasProcessed = true;
                    processedEvent.Set();
                }
            };

            Trace($"ProcessedFail({block.ToString(Block.Format.Short)}) enter");
            long failStart = Environment.TickCount64;
            _branchProcessor.AllowToFail(block.Hash!);
            processedEvent.WaitOne(ProcessingWait);
            long failElapsed = Environment.TickCount64 - failStart;
            Trace($"ProcessedFail({block.ToString(Block.Format.Short)}) done: {failElapsed}ms, wasProcessed={wasProcessed}");
            Assert.That(wasProcessed, Is.True, $"Block was never processed {block.ToString(Block.Format.Short)} (waited {failElapsed}ms, trace={_traceLogPath})");
            Assert.That(_blockTree.Head?.Hash, Is.EqualTo(_headBefore), $"Processing did not fail - {block.ToString(Block.Format.Short)} became a new head block");
            _logger.Info($"Finished waiting for {block.ToString(Block.Format.Short)} to fail processing");
            return new AfterBlock(_logManager, this, block);
        }

        public ProcessingTestContext Suggested(Block block, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess)
        {
            if ((options & BlockTreeSuggestOptions.ShouldProcess) != 0)
            {
                // Use Task.Run to avoid blocking when AllowSynchronousContinuations
                // causes inline processing on the calling thread.
                //
                // We wait for either BlockAdded (enqueued to processor) or SuggestBlock
                // completion (non-best blocks that aren't enqueued) before returning. This
                // prevents a race where two concurrent Enqueue calls both see _queueCount > 1
                // and go to the recovery queue in non-deterministic order, causing the
                // processor to batch blocks together and deadlock the test.
                //
                // TaskCompletionSource is used instead of ManualResetEventSlim because the
                // background Task.Run may outlive this method (when AllowSynchronousContinuations
                // causes SuggestBlock to block indefinitely) and TrySetResult is safe to call
                // on a completed TCS without disposal concerns.
                TaskCompletionSource suggestCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnBlockAdded(object? sender, BlockEventArgs args)
                {
                    if (args.Block.Hash == block.Hash)
                        suggestCompleted.TrySetResult();
                }

                _processor.BlockAdded += OnBlockAdded;
                long sugStart = Environment.TickCount64;
                Trace($"Suggested({block.ToString(Block.Format.Short)}) enter, queueCount={_processor.Count}");
                try
                {
                    // Track when Task.Run fully completes (after Enqueue + queue write).
                    // suggestCompleted may resolve early via OnBlockAdded (before queue write),
                    // so we also wait for the task itself to ensure ordering.
                    Task suggestTask = Task.Run(() =>
                    {
                        try
                        {
                            Trace($"Suggested({block.ToString(Block.Format.Short)}) Task.Run calling SuggestBlock");
                            AddBlockResult result = _blockTree.SuggestBlock(block, options);
                            Trace($"Suggested({block.ToString(Block.Format.Short)}) SuggestBlock returned {result}");
                            if (result != AddBlockResult.Added)
                            {
                                _logger.Info($"Finished waiting for {block.ToString(Block.Format.Short)} as block was ignored");
                                _resetEvent.Set();
                            }
                        }
                        finally
                        {
                            suggestCompleted.TrySetResult();
                        }
                    });
                    // Wait for BlockAdded (or Task.Run completion) first — this unblocks
                    // even if the block gets processed inline via AllowSynchronousContinuations.
                    bool completed = suggestCompleted.Task.Wait(ProcessingWait);
                    // Then give the Task.Run a brief window to finish the queue write.
                    // If it was inline-processed, this will time out harmlessly (task is stuck
                    // in BranchProcessor.Process waiting for Allow).
                    suggestTask.Wait(50);
                    long sugElapsed = Environment.TickCount64 - sugStart;
                    Trace($"Suggested({block.ToString(Block.Format.Short)}) done: {sugElapsed}ms, completed={completed}, taskDone={suggestTask.IsCompleted}");
                    Assert.That(completed,
                        Is.True,
                        $"Timed out waiting for {block.ToString(Block.Format.Short)} to complete suggestion after {sugElapsed}ms (trace={_traceLogPath})");
                }
                finally
                {
                    _processor.BlockAdded -= OnBlockAdded;
                }
            }
            else
            {
                AddBlockResult result = _blockTree.SuggestBlock(block, options);
                if (result != AddBlockResult.Added)
                {
                    _logger.Info($"Finished waiting for {block.ToString(Block.Format.Short)} as block was ignored");
                    _resetEvent.Set();
                }
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
            _branchProcessor.Allow(block.Hash!);
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
            Trace($"Recovered({block.ToString(Block.Format.Short)}) called");
            _recoveryStep.Allow(block.Hash!);
            return this;
        }

        public ProcessingTestContext CountIs(int expectedCount)
        {
            Assert.That(() => _processor.Count, Is.EqualTo(expectedCount).After(ProcessingWait, 10));
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

        public class AfterBlock(ILogManager logManager, ProcessingTestContext processingTestContext, Block block)
        {
            private const int IgnoreWait = 200;

            private readonly ILogger _logger = logManager.GetClassLogger();

            public ProcessingTestContext BecomesGenesis()
            {
                _logger.Info($"Waiting for {block.ToString(Block.Format.Short)} to become genesis block");
                processingTestContext._resetEvent.WaitOne(ProcessingWait);
                Assert.That(processingTestContext._blockTree.Genesis!.Hash, Is.EqualTo(block.Header.Hash), "genesis");
                return processingTestContext;
            }

            public ProcessingTestContext BecomesNewHead()
            {
                processingTestContext.Trace($"BecomesNewHead({block.ToString(Block.Format.Short)}) enter, currentHead={processingTestContext._blockTree.Head?.Hash?.ToString()[..12] ?? "null"}");
                long start = Environment.TickCount64;
                long deadline = start + ProcessingWait;
                int iterations = 0;
                while (processingTestContext._blockTree.Head?.Hash != block.Header.Hash)
                {
                    long remaining = deadline - Environment.TickCount64;
                    if (remaining <= 0)
                        break;
                    processingTestContext._resetEvent.WaitOne((int)remaining);
                    iterations++;
                }

                long elapsed = Environment.TickCount64 - start;
                bool matched = processingTestContext._blockTree.Head?.Hash == block.Header.Hash;
                processingTestContext.Trace($"BecomesNewHead({block.ToString(Block.Format.Short)}) done: {elapsed}ms, iterations={iterations}, matched={matched}, head={processingTestContext._blockTree.Head?.Hash?.ToString()[..12] ?? "null"}");

                Assert.That(processingTestContext._blockTree.Head!.Hash, Is.EqualTo(block.Header.Hash),
                    $"Expected {block.ToString(Block.Format.Short)} to become the head after {elapsed}ms (trace={processingTestContext._traceLogPath})");
                return processingTestContext;
            }

            public ProcessingTestContext IsKeptOnBranch()
            {
                _logger.Info($"Waiting for {block.ToString(Block.Format.Short)} to be ignored");
                processingTestContext._resetEvent.WaitOne(IgnoreWait);
                Assert.That(processingTestContext._blockTree.Head!.Hash, Is.EqualTo(processingTestContext._headBefore), "head");
                _logger.Info($"Finished waiting for {block.ToString(Block.Format.Short)} to be ignored");
                return processingTestContext;
            }

            public ProcessingTestContext IsDeletedAsInvalid()
            {
                processingTestContext.Trace($"IsDeletedAsInvalid({block.ToString(Block.Format.Short)}) enter");
                long delStart = Environment.TickCount64;
                // Drain any stale signal (no NewHeadBlock fires for invalid blocks, so this always times out).
                processingTestContext._resetEvent.WaitOne(IgnoreWait);
                Assert.That(processingTestContext._blockTree.Head!.Hash, Is.EqualTo(processingTestContext._headBefore), "head");
                // Poll until the block is actually deleted — the 200 ms drain above is not enough on slow CI.
                Assert.That(() => processingTestContext._blockTree.FindBlock(block.Hash, BlockTreeLookupOptions.None),
                    Is.Null.After(ProcessingWait, 50), $"block {block.ToString(Block.Format.Short)} should be deleted as invalid (trace={processingTestContext._traceLogPath})");
                long delElapsed = Environment.TickCount64 - delStart;
                processingTestContext.Trace($"IsDeletedAsInvalid({block.ToString(Block.Format.Short)}) done: {delElapsed}ms");
                return processingTestContext;
            }
        }

        public ProcessingTestContext Sleep(int milliseconds)
        {
            Thread.Sleep(milliseconds);
            return this;
        }

        public ProcessingTestContext AssertProcessedBlocks(params IEnumerable<Block> blocks)
        {
            _branchProcessor.Processed.Should().BeEquivalentTo(blocks.Select(b => b.Hash));
            return this;
        }

        public ProcessingTestContext StateSyncedTo(Block block4D8)
        {
            _stateReader.HasStateForBlock(block4D8.Header).Returns(true);
            return this;
        }
    }

    private static class When
    {
        public static ProcessingTestContext ProcessingBlocks => new(true);

        public static ProcessingTestContext ProcessorIsNotStarted => new(false);
    }

    // Instance fields — not static — so that parallel test instances do not share
    // mutable Block objects (RecoverData mutates Header.Author).
    private readonly Block _block0 = Build.A.Block.WithNumber(0).WithNonce(0).WithDifficulty(0).TestObject;
    private readonly Block _block1D2;
    private readonly Block _block2D4;
    private readonly Block _block3D6;
    private readonly Block _block4D8;
    private readonly Block _block5D10;
    private readonly Block _blockB2D4;
    private readonly Block _blockB3D8;
    private readonly Block _blockC2D100;
    private readonly Block _blockD2D200;
    private readonly Block _blockE2D300;

    public BlockchainProcessorTests()
    {
        _block1D2 = Build.A.Block.WithNumber(1).WithNonce(1).WithParent(_block0).WithDifficulty(2).TestObject;
        _block2D4 = Build.A.Block.WithNumber(2).WithNonce(2).WithParent(_block1D2).WithDifficulty(2).TestObject;
        _block3D6 = Build.A.Block.WithNumber(3).WithNonce(3).WithParent(_block2D4).WithDifficulty(2).TestObject;
        _block4D8 = Build.A.Block.WithNumber(4).WithNonce(4).WithParent(_block3D6).WithDifficulty(2).TestObject;
        _block5D10 = Build.A.Block.WithNumber(5).WithNonce(5).WithParent(_block4D8).WithDifficulty(2).TestObject;
        _blockB2D4 = Build.A.Block.WithNumber(2).WithNonce(6).WithParent(_block1D2).WithDifficulty(2).TestObject;
        _blockB3D8 = Build.A.Block.WithNumber(3).WithNonce(7).WithParent(_blockB2D4).WithDifficulty(4).TestObject;
        _blockC2D100 = Build.A.Block.WithNumber(3).WithNonce(8).WithParent(_block1D2).WithDifficulty(98).TestObject;
        _blockD2D200 = Build.A.Block.WithNumber(3).WithNonce(8).WithParent(_block1D2).WithDifficulty(198).TestObject;
        _blockE2D300 = Build.A.Block.WithNumber(3).WithNonce(8).WithParent(_block1D2).WithDifficulty(298).TestObject;
    }

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
                IStateReader stateReader = ctx.Resolve<IStateReader>();
                return new ChainHeadInfoProvider(
                    new FixedForkActivationChainHeadSpecProvider(specProvider, fixedBlock: 10_000_000),
                    blockTree,
                    new ChainHeadReadOnlyStateProvider(blockTree, stateReader)) // Need to use the non  ChainHeadSpecProvider constructor.
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

    [Test, MaxTime(Timeout.MaxTestTime)]
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
    public void Can_change_branch_on_invalid_block()
    {
        When.ProcessingBlocks
            .FullyProcessed(_block0).BecomesGenesis()
            .FullyProcessed(_block1D2).BecomesNewHead()
            .FullyProcessedFail(_block2D4).IsDeletedAsInvalid()
            .FullyProcessed(_blockB2D4).BecomesNewHead();
    }

    [Test(Description = "Covering scenario when we have an invalid block followed by its descendants." +
                        "All the descendant blocks should get discarded and an alternative branch should get selected." +
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
