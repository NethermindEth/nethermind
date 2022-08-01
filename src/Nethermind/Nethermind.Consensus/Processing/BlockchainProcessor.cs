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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Metrics = Nethermind.Blockchain.Metrics;

namespace Nethermind.Consensus.Processing
{
    public class BlockchainProcessor : IBlockchainProcessor, IBlockProcessingQueue
    {
        public int SoftMaxRecoveryQueueSizeInTx = 10000; // adjust based on tx or gas
        public const int MaxProcessingQueueSize = 2000; // adjust based on tx or gas

        public ITracerBag Tracers => _compositeBlockTracer;

        private readonly IBlockProcessor _blockProcessor;
        private readonly IBlockPreprocessorStep _recoveryStep;
        private readonly IStateReader _stateReader;
        private readonly Options _options;
        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;

        private readonly BlockingCollection<BlockRef> _recoveryQueue = new(new ConcurrentQueue<BlockRef>());

        private readonly BlockingCollection<BlockRef> _blockQueue = new(new ConcurrentQueue<BlockRef>(),
            MaxProcessingQueueSize);

        private int _queueCount;

        private readonly ProcessingStats _stats;

        private CancellationTokenSource? _loopCancellationSource;
        private Task? _recoveryTask;
        private Task? _processorTask;
        private DateTime _lastProcessedBlock;

        private int _currentRecoveryQueueSize;
        private readonly CompositeBlockTracer _compositeBlockTracer = new();
        private readonly Stopwatch _stopwatch = new();

        public event EventHandler<IBlockchainProcessor.InvalidBlockEventArgs> InvalidBlock;

        /// <summary>
        ///
        /// </summary>
        /// <param name="blockTree"></param>
        /// <param name="blockProcessor"></param>
        /// <param name="recoveryStep"></param>
        /// <param name="stateReader"></param>
        /// <param name="logManager"></param>
        /// <param name="options"></param>
        public BlockchainProcessor(
            IBlockTree? blockTree,
            IBlockProcessor? blockProcessor,
            IBlockPreprocessorStep? recoveryStep,
            IStateReader stateReader,
            ILogManager? logManager,
            Options options)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _blockProcessor = blockProcessor ?? throw new ArgumentNullException(nameof(blockProcessor));
            _recoveryStep = recoveryStep ?? throw new ArgumentNullException(nameof(recoveryStep));
            _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
            _options = options;

            _blockTree.NewBestSuggestedBlock += OnNewBestBlock;
            _blockTree.NewHeadBlock += OnNewHeadBlock;

            _stats = new ProcessingStats(_logger);
        }

        private void OnNewHeadBlock(object? sender, BlockEventArgs e)
        {
            _lastProcessedBlock = DateTime.UtcNow;
        }

        private void OnNewBestBlock(object sender, BlockEventArgs blockEventArgs)
        {
            ProcessingOptions options = ProcessingOptions.None;
            if (_options.StoreReceiptsByDefault)
            {
                options |= ProcessingOptions.StoreReceipts;
            }

            if (blockEventArgs.Block is not null)
            {
                Enqueue(blockEventArgs.Block, options);
            }
        }

        public void Enqueue(Block block, ProcessingOptions processingOptions)
        {
            if (_logger.IsTrace) _logger.Trace($"Enqueuing a new block {block.ToString(Block.Format.Short)} for processing.");

            int currentRecoveryQueueSize = Interlocked.Add(ref _currentRecoveryQueueSize, block.Transactions.Length);
            Keccak? blockHash = block.Hash!;
            BlockRef blockRef = currentRecoveryQueueSize >= SoftMaxRecoveryQueueSizeInTx
                ? new BlockRef(blockHash, processingOptions)
                : new BlockRef(block, processingOptions);

            if (!_recoveryQueue.IsAddingCompleted)
            {
                Interlocked.Increment(ref _queueCount);
                try
                {
                    _recoveryQueue.Add(blockRef);
                    if (_logger.IsTrace) _logger.Trace($"A new block {block.ToString(Block.Format.Short)} enqueued for processing.");
                }
                catch (Exception e)
                {
                    Interlocked.Decrement(ref _queueCount);
                    BlockRemoved?.Invoke(this, new BlockHashEventArgs(blockHash, ProcessingResult.QueueException, e));
                    if (e is not InvalidOperationException || !_recoveryQueue.IsAddingCompleted)
                    {
                        throw;
                    }
                }
            }
        }

        public void Start()
        {
            _loopCancellationSource = new CancellationTokenSource();
            _recoveryTask = Task.Factory.StartNew(
                RunRecoveryLoop,
                _loopCancellationSource.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    if (_logger.IsError) _logger.Error("Sender address recovery encountered an exception.", t.Exception);
                }
                else if (t.IsCanceled)
                {
                    if (_logger.IsDebug) _logger.Debug("Sender address recovery stopped.");
                }
                else if (t.IsCompleted)
                {
                    if (_logger.IsDebug) _logger.Debug("Sender address recovery complete.");
                }
            });

            _processorTask = Task.Factory.StartNew(
                RunProcessingLoop,
                _loopCancellationSource.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    if (_logger.IsError) _logger.Error($"{nameof(BlockchainProcessor)} encountered an exception.", t.Exception);
                }
                else if (t.IsCanceled)
                {
                    if (_logger.IsDebug) _logger.Debug($"{nameof(BlockchainProcessor)} stopped.");
                }
                else if (t.IsCompleted)
                {
                    if (_logger.IsDebug) _logger.Debug($"{nameof(BlockchainProcessor)} complete.");
                }
            });
        }

        public async Task StopAsync(bool processRemainingBlocks = false)
        {
            if (processRemainingBlocks)
            {
                _recoveryQueue.CompleteAdding();
                await (_recoveryTask ?? Task.CompletedTask);
                _blockQueue.CompleteAdding();
            }
            else
            {
                _loopCancellationSource?.Cancel();
                _recoveryQueue.CompleteAdding();
                _blockQueue.CompleteAdding();
            }

            await Task.WhenAll((_recoveryTask ?? Task.CompletedTask), (_processorTask ?? Task.CompletedTask));
            if (_logger.IsInfo) _logger.Info("Blockchain Processor shutdown complete.. please wait for all components to close");
        }

        private void RunRecoveryLoop()
        {
            void DecrementQueue(Keccak blockHash, ProcessingResult processingResult, Exception? exception = null)
            {
                Interlocked.Decrement(ref _queueCount);
                BlockRemoved?.Invoke(this, new BlockHashEventArgs(blockHash, processingResult, exception));
                FireProcessingQueueEmpty();
            }

            if (_logger.IsDebug) _logger.Debug($"Starting recovery loop - {_blockQueue.Count} blocks waiting in the queue.");
            _lastProcessedBlock = DateTime.UtcNow;
            foreach (BlockRef blockRef in _recoveryQueue.GetConsumingEnumerable(_loopCancellationSource.Token))
            {
                try
                {
                    if (blockRef.Resolve(_blockTree))
                    {
                        Interlocked.Add(ref _currentRecoveryQueueSize, -blockRef.Block!.Transactions.Length);
                        if (_logger.IsTrace) _logger.Trace($"Recovering addresses for block {blockRef.BlockHash}.");
                        _recoveryStep.RecoverData(blockRef.Block);

                        try
                        {
                            _blockQueue.Add(blockRef);
                        }
                        catch (Exception e)
                        {
                            DecrementQueue(blockRef.BlockHash, ProcessingResult.QueueException, e);

                            if (e is InvalidOperationException)
                            {
                                if (_logger.IsDebug) _logger.Debug($"Recovery loop stopping.");
                                return;
                            }

                            throw;
                        }
                    }
                    else
                    {
                        DecrementQueue(blockRef.BlockHash, ProcessingResult.MissingBlock);
                        if (_logger.IsTrace) _logger.Trace("Block was removed from the DB and cannot be recovered (it belonged to an invalid branch). Skipping.");
                    }
                }
                catch (Exception e)
                {
                    DecrementQueue(blockRef.BlockHash, ProcessingResult.Exception, e);
                    throw;
                }
            }
        }

        private void RunProcessingLoop()
        {
            if (_logger.IsDebug) _logger.Debug($"Starting block processor - {_blockQueue.Count} blocks waiting in the queue.");

            FireProcessingQueueEmpty();

            foreach (BlockRef blockRef in _blockQueue.GetConsumingEnumerable(_loopCancellationSource.Token))
            {
                try
                {
                    if (blockRef.IsInDb || blockRef.Block == null)
                    {
                        BlockRemoved?.Invoke(this, new BlockHashEventArgs(blockRef.BlockHash, ProcessingResult.MissingBlock));
                        throw new InvalidOperationException("Processing loop expects only resolved blocks");
                    }

                    Block block = blockRef.Block;

                    if (_logger.IsTrace) _logger.Trace($"Processing block {block.ToString(Block.Format.Short)}).");
                    _stats.Start();

                    Block processedBlock = Process(block, blockRef.ProcessingOptions, _compositeBlockTracer.GetTracer());

                    if (processedBlock is null)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Failed / skipped processing {block.ToString(Block.Format.Full)}");
                        BlockRemoved?.Invoke(this, new BlockHashEventArgs(blockRef.BlockHash, ProcessingResult.ProcessingError));
                    }
                    else
                    {
                        if (_logger.IsTrace) _logger.Trace($"Processed block {block.ToString(Block.Format.Full)}");
                        _stats.UpdateStats(block, _recoveryQueue.Count, _blockQueue.Count);
                        BlockRemoved?.Invoke(this, new BlockHashEventArgs(blockRef.BlockHash, ProcessingResult.Success));
                    }
                }
                catch (Exception exception)
                {
                    if (_logger.IsWarn) _logger.Warn($"Processing loop threw an exception. Block: {blockRef}, Exception: {exception}");
                    BlockRemoved?.Invoke(this, new BlockHashEventArgs(blockRef.BlockHash, ProcessingResult.Exception, exception));
                }
                finally
                {
                    Interlocked.Decrement(ref _queueCount);
                }

                if (_logger.IsTrace) _logger.Trace($"Now {_blockQueue.Count} blocks waiting in the queue.");
                FireProcessingQueueEmpty();
            }

            if (_logger.IsInfo) _logger.Info("Block processor queue stopped.");
        }

        private void FireProcessingQueueEmpty()
        {
            if (((IBlockProcessingQueue)this).IsEmpty)
            {
                ProcessingQueueEmpty?.Invoke(this, EventArgs.Empty);
            }
        }

        public event EventHandler? ProcessingQueueEmpty;
        public event EventHandler<BlockHashEventArgs>? BlockRemoved;

        int IBlockProcessingQueue.Count => _queueCount;

        public Block? Process(Block suggestedBlock, ProcessingOptions options, IBlockTracer tracer)
        {
            if (!RunSimpleChecksAheadOfProcessing(suggestedBlock, options))
            {
                return null;
            }

            UInt256 totalDifficulty = suggestedBlock.TotalDifficulty ?? 0;
            if (_logger.IsTrace) _logger.Trace($"Total difficulty of block {suggestedBlock.ToString(Block.Format.Short)} is {totalDifficulty}");

            bool shouldProcess =
                suggestedBlock.IsGenesis
                || _blockTree.IsBetterThanHead(suggestedBlock.Header)
                || options.ContainsFlag(ProcessingOptions.ForceProcessing);

            if (!shouldProcess)
            {
                if (_logger.IsDebug) _logger.Debug($"Skipped processing of {suggestedBlock.ToString(Block.Format.FullHashAndNumber)}, Head = {_blockTree.Head?.Header?.ToString(BlockHeader.Format.Short)}, total diff = {totalDifficulty}, head total diff = {_blockTree.Head?.TotalDifficulty}");
                return null;
            }

            ProcessingBranch processingBranch = PrepareProcessingBranch(suggestedBlock, options);
            PrepareBlocksToProcess(suggestedBlock, options, processingBranch);

            _stopwatch.Restart();
            Block[]? processedBlocks = ProcessBranch(processingBranch, options, tracer);
            if (processedBlocks == null)
            {
                return null;
            }

            Block? lastProcessed = null;
            if (processedBlocks.Length > 0)
            {
                lastProcessed = processedBlocks[^1];
                if (_logger.IsTrace) _logger.Trace($"Setting total on last processed to {lastProcessed.ToString(Block.Format.Short)}");
                lastProcessed.Header.TotalDifficulty = suggestedBlock.TotalDifficulty;
            }
            else
            {
                if (_logger.IsDebug) _logger.Debug($"Skipped processing of {suggestedBlock.ToString(Block.Format.FullHashAndNumber)}, last processed is null: {true}, processedBlocks.Length: {processedBlocks.Length}");
            }

            bool updateHead = !options.ContainsFlag(ProcessingOptions.DoNotUpdateHead);
            if (updateHead)
            {
                if (_logger.IsTrace) _logger.Trace($"Updating main chain: {lastProcessed}, blocks count: {processedBlocks.Length}");
                _blockTree.UpdateMainChain(processingBranch.Blocks, true);
            }

            bool notReadOnlyChain = !options.ContainsFlag(ProcessingOptions.ReadOnlyChain);
            if (notReadOnlyChain)
            {
                Metrics.LastBlockProcessingTimeInMs = _stopwatch.ElapsedMilliseconds;
            }

            if ((options & ProcessingOptions.MarkAsProcessed) == ProcessingOptions.MarkAsProcessed)
            {
                if (_logger.IsTrace) _logger.Trace($"Marked blocks as processed {lastProcessed}, blocks count: {processedBlocks.Length}");
                _blockTree.MarkChainAsProcessed(processingBranch.Blocks);

                Metrics.LastBlockProcessingTimeInMs = _stopwatch.ElapsedMilliseconds;
            }

            if (!notReadOnlyChain)
            {
                _stats.UpdateStats(lastProcessed, _recoveryQueue.Count, _blockQueue.Count);
            }

            return lastProcessed;
        }

        public bool IsProcessingBlocks(ulong? maxProcessingInterval)
        {
            if (_processorTask == null || _recoveryTask == null || _processorTask.IsCompleted ||
                _recoveryTask.IsCompleted)
                return false;

            if (maxProcessingInterval != null)
                return _lastProcessedBlock.AddSeconds(maxProcessingInterval.Value) > DateTime.UtcNow;
            else // user does not setup interval and we cannot set interval time based on chainspec
                return true;
        }

        private void TraceFailingBranch(ProcessingBranch processingBranch, ProcessingOptions options, IBlockTracer blockTracer, DumpOptions dumpType)
        {
            if ((_options.DumpOptions & dumpType) != 0)
            {
                try
                {
                    _blockProcessor.Process(
                        processingBranch.Root,
                        processingBranch.BlocksToProcess,
                        options,
                        blockTracer);
                }
                catch (InvalidBlockException ex)
                {
                    BlockTraceDumper.LogDiagnosticTrace(blockTracer, ex.InvalidBlockHash, _logger);
                }
                catch (Exception ex)
                {
                    BlockTraceDumper.LogTraceFailure(blockTracer, processingBranch.Root, ex, _logger);
                }
            }
        }

        private Block[]? ProcessBranch(ProcessingBranch processingBranch, ProcessingOptions options, IBlockTracer tracer)
        {
            void DeleteInvalidBlocks(Keccak invalidBlockHash)
            {
                for (int i = 0; i < processingBranch.BlocksToProcess.Count; i++)
                {
                    if (processingBranch.BlocksToProcess[i].Hash == invalidBlockHash)
                    {
                        _blockTree.DeleteInvalidBlock(processingBranch.BlocksToProcess[i]);
                        if (_logger.IsDebug) _logger.Debug($"Skipped processing of {processingBranch.BlocksToProcess[^1].ToString(Block.Format.FullHashAndNumber)} because of {processingBranch.BlocksToProcess[i].ToString(Block.Format.FullHashAndNumber)} is invalid");
                    }
                }
            }

            Keccak? invalidBlockHash = null;
            Block[]? processedBlocks;
            try
            {
                processedBlocks = _blockProcessor.Process(
                    processingBranch.Root,
                    processingBranch.BlocksToProcess,
                    options,
                    tracer);
            }
            catch (InvalidBlockException ex)
            {
                InvalidBlock?.Invoke(this, new IBlockchainProcessor.InvalidBlockEventArgs
                {
                    InvalidBlockHash = ex.InvalidBlockHash,
                });

                invalidBlockHash = ex.InvalidBlockHash;
                TraceFailingBranch(
                    processingBranch,
                    options,
                    new BlockReceiptsTracer(),
                    DumpOptions.Receipts);

                TraceFailingBranch(
                    processingBranch,
                    options,
                    new ParityLikeBlockTracer(ParityTraceTypes.StateDiff | ParityTraceTypes.Trace),
                    DumpOptions.Parity);

                TraceFailingBranch(
                    processingBranch,
                    options,
                    new GethLikeBlockTracer(GethTraceOptions.Default),
                    DumpOptions.Geth);

                processedBlocks = null;
            }

            finally
            {
                if (invalidBlockHash is not null && !options.ContainsFlag(ProcessingOptions.ReadOnlyChain))
                {
                    DeleteInvalidBlocks(invalidBlockHash);
                }
            }

            return processedBlocks;
        }

        private void PrepareBlocksToProcess(Block suggestedBlock, ProcessingOptions options,
            ProcessingBranch processingBranch)
        {
            List<Block> blocksToProcess = processingBranch.BlocksToProcess;
            if (options.ContainsFlag(ProcessingOptions.ForceProcessing))
            {
                processingBranch.Blocks.Clear();
                blocksToProcess.Add(suggestedBlock);
            }
            else
            {
                foreach (Block block in processingBranch.Blocks)
                {
                    _loopCancellationSource?.Token.ThrowIfCancellationRequested();

                    if (block.Hash != null && _blockTree.WasProcessed(block.Number, block.Hash))
                    {
                        if (_logger.IsInfo)
                            _logger.Info(
                                $"Rerunning block after reorg or pruning: {block.ToString(Block.Format.FullHashAndNumber)}");
                    }

                    blocksToProcess.Add(block);
                }
            }

            if (_logger.IsTrace)
                _logger.Trace($"Processing {blocksToProcess.Count} blocks from state root {processingBranch.Root}");
            for (int i = 0; i < blocksToProcess.Count; i++)
            {
                /* this can happen if the block was loaded as an ancestor and did not go through the recovery queue */
                _recoveryStep.RecoverData(blocksToProcess[i]);
            }
        }

        private ProcessingBranch PrepareProcessingBranch(Block suggestedBlock, ProcessingOptions options)
        {
            BlockHeader branchingPoint = null;
            List<Block> blocksToBeAddedToMain = new();

            bool preMergeFinishBranchingCondition;
            bool suggestedBlockIsPostMerge = suggestedBlock.IsPostMerge;

            Block toBeProcessed = suggestedBlock;
            do
            {
                blocksToBeAddedToMain.Add(toBeProcessed);
                if (_logger.IsTrace)
                    _logger.Trace(
                        $"To be processed (of {suggestedBlock.ToString(Block.Format.Short)}) is {toBeProcessed?.ToString(Block.Format.Short)}");
                if (toBeProcessed.IsGenesis)
                {
                    break;
                }

                branchingPoint = _blockTree.FindParentHeader(toBeProcessed.Header,
                    BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                if (branchingPoint == null)
                {
                    // genesis block
                    break;
                }

                // !!!
                // for beam sync we do not expect previous blocks to necessarily be there and we
                // do not need them since we can requests state from outside
                // TODO: remove this and verify the current usage scenarios - seems wrong
                // !!!
                if (options.ContainsFlag(ProcessingOptions.IgnoreParentNotOnMainChain))
                {
                    break;
                }

                bool headIsGenesis = _blockTree.Head?.IsGenesis ?? false;
                bool toBeProcessedIsNotBlockOne = toBeProcessed.Number > 1;
                if (_logger.IsTrace)
                    _logger.Trace($"Finding parent of {toBeProcessed.ToString(Block.Format.Short)}");
                toBeProcessed = _blockTree.FindParent(toBeProcessed.Header, BlockTreeLookupOptions.None);
                if (_logger.IsTrace) _logger.Trace($"Found parent {toBeProcessed?.ToString(Block.Format.Short)}");
                bool isFastSyncTransition = headIsGenesis && toBeProcessedIsNotBlockOne;
                if (toBeProcessed == null)
                {
                    if (_logger.IsDebug)
                        _logger.Debug(
                            $"Treating this as fast sync transition for {suggestedBlock.ToString(Block.Format.Short)}");
                    break;
                }

                if (isFastSyncTransition)
                {
                    // if we have parent state it means that we don't need to go deeper
                    if (toBeProcessed?.StateRoot == null || _stateReader.HasStateForBlock(toBeProcessed.Header))
                    {
                        if (_logger.IsInfo) _logger.Info($"Found state for parent: {toBeProcessed}, StateRoot: {toBeProcessed?.StateRoot}");
                        break;
                    }
                    else
                    {
                        if (_logger.IsInfo) _logger.Info($"A new block {toBeProcessed} in fast sync transition branch - state not found");
                    }
                }

                // TODO: there is no test for the second condition
                // generally if we finish fast sync at block, e.g. 8 and then have 6 blocks processed and close Neth
                // then on restart we would find 14 as the branch head (since 14 is on the main chain)
                // we need to dig deeper to go all the way to the false (reorg boundary) head
                // otherwise some nodes would be missing
                bool notFoundTheBranchingPointYet = !_blockTree.IsMainChain(branchingPoint.Hash!);
                bool notReachedTheReorgBoundary = branchingPoint.Number > (_blockTree.Head?.Header.Number ?? 0);
                preMergeFinishBranchingCondition = (notFoundTheBranchingPointYet || notReachedTheReorgBoundary);
                if (_logger.IsTrace)
                    _logger.Trace(
                        $" Current branching point: {branchingPoint.Number}, {branchingPoint.Hash} TD: {branchingPoint.TotalDifficulty} Processing conditions notFoundTheBranchingPointYet {notFoundTheBranchingPointYet}, notReachedTheReorgBoundary: {notReachedTheReorgBoundary}, suggestedBlockIsPostMerge {suggestedBlockIsPostMerge}");
            } while (preMergeFinishBranchingCondition);

            if (branchingPoint != null && branchingPoint.Hash != _blockTree.Head?.Hash)
            {
                if (_logger.IsTrace)
                    _logger.Trace($"Head block was: {_blockTree.Head?.Header?.ToString(BlockHeader.Format.Short)}");
                if (_logger.IsTrace)
                    _logger.Trace($"Branching from: {branchingPoint.ToString(BlockHeader.Format.Short)}");
            }
            else
            {
                if (_logger.IsTrace)
                    _logger.Trace(branchingPoint == null
                        ? "Setting as genesis block"
                        : $"Adding on top of {branchingPoint.ToString(BlockHeader.Format.Short)}");
            }

            Keccak stateRoot = branchingPoint?.StateRoot;
            if (_logger.IsTrace) _logger.Trace($"State root lookup: {stateRoot}");
            blocksToBeAddedToMain.Reverse();
            return new ProcessingBranch(stateRoot, blocksToBeAddedToMain);
        }

        [Todo(Improve.Refactor, "This probably can be made conditional (in DEBUG only)")]
        private bool RunSimpleChecksAheadOfProcessing(Block suggestedBlock, ProcessingOptions options)
        {
            /* a bit hacky way to get the invalid branch out of the processing loop */
            if (suggestedBlock.Number != 0 &&
                !_blockTree.IsKnownBlock(suggestedBlock.Number - 1, suggestedBlock.ParentHash))
            {
                if (_logger.IsDebug)
                    _logger.Debug(
                        $"Skipping processing block {suggestedBlock.ToString(Block.Format.FullHashAndNumber)} with unknown parent");
                return false;
            }

            if (suggestedBlock.Header.TotalDifficulty == null)
            {
                if (_logger.IsDebug)
                    _logger.Debug(
                        $"Skipping processing block {suggestedBlock.ToString(Block.Format.FullHashAndNumber)} without total difficulty");
                throw new InvalidOperationException(
                    "Block without total difficulty calculated was suggested for processing");
            }

            if (!options.ContainsFlag(ProcessingOptions.NoValidation) && suggestedBlock.Hash is null)
            {
                if (_logger.IsDebug) _logger.Debug($"Skipping processing block {suggestedBlock.ToString(Block.Format.FullHashAndNumber)} without calculated hash");
                throw new InvalidOperationException("Block hash should be known at this stage if running in a validating mode");
            }

            for (int i = 0; i < suggestedBlock.Uncles.Length; i++)
            {
                if (suggestedBlock.Uncles[i].Hash == null)
                {
                    if (_logger.IsDebug) _logger.Debug($"Skipping processing block {suggestedBlock.ToString(Block.Format.FullHashAndNumber)} with null uncle hash ar {i}");
                    throw new InvalidOperationException($"Uncle's {i} hash is null when processing block");
                }
            }

            return true;
        }

        public void Dispose()
        {
            _recoveryQueue.Dispose();
            _blockQueue.Dispose();
            _loopCancellationSource?.Dispose();
            _recoveryTask?.Dispose();
            _processorTask?.Dispose();
            _blockTree.NewBestSuggestedBlock -= OnNewBestBlock;
            _blockTree.NewHeadBlock -= OnNewHeadBlock;
        }

        [DebuggerDisplay("Root: {Root}, Length: {BlocksToProcess.Count}")]
        private readonly struct ProcessingBranch
        {
            public ProcessingBranch(Keccak root, List<Block> blocks)
            {
                Root = root;
                Blocks = blocks;
                BlocksToProcess = new List<Block>();
            }

            public Keccak Root { get; }
            public List<Block> Blocks { get; }
            public List<Block> BlocksToProcess { get; }
        }

        public class Options
        {
            public static Options NoReceipts = new() { StoreReceiptsByDefault = true };
            public static Options Default = new();

            public bool StoreReceiptsByDefault { get; set; } = true;

            public DumpOptions DumpOptions { get; set; } = DumpOptions.None;
        }
    }
}
