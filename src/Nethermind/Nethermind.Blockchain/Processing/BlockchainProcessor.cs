//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;

namespace Nethermind.Blockchain.Processing
{
    public class BlockchainProcessor : IBlockchainProcessor, IBlockProcessingQueue
    {
        private readonly IBlockProcessor _blockProcessor;
        private readonly IBlockDataRecoveryStep _recoveryStep;
        private readonly bool _storeReceiptsByDefault;
        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;

        private readonly BlockingCollection<BlockRef> _recoveryQueue = new BlockingCollection<BlockRef>(new ConcurrentQueue<BlockRef>());
        private readonly BlockingCollection<BlockRef> _blockQueue = new BlockingCollection<BlockRef>(new ConcurrentQueue<BlockRef>(), MaxProcessingQueueSize);
        private readonly ProcessingStats _stats;

        private CancellationTokenSource _loopCancellationSource;
        private Task _recoveryTask;
        private Task _processorTask;

        private int _currentRecoveryQueueSize;
        public int SoftMaxRecoveryQueueSizeInTx = 10000; // adjust based on tx or gas
        private const int MaxProcessingQueueSize = 2000; // adjust based on tx or gas

        /// <summary>
        /// 
        /// </summary>
        /// <param name="blockTree"></param>
        /// <param name="blockProcessor"></param>
        /// <param name="recoveryStep"></param>
        /// <param name="logManager"></param>
        /// <param name="storeReceiptsByDefault"></param>
        /// <param name="autoProcess">Registers for OnNewHeadBlock events at block tree.</param>
        public BlockchainProcessor(
            IBlockTree blockTree,
            IBlockProcessor blockProcessor,
            IBlockDataRecoveryStep recoveryStep,
            ILogManager logManager,
            bool storeReceiptsByDefault,
            bool autoProcess = true)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _blockProcessor = blockProcessor ?? throw new ArgumentNullException(nameof(blockProcessor));
            _recoveryStep = recoveryStep ?? throw new ArgumentNullException(nameof(recoveryStep));
            _storeReceiptsByDefault = storeReceiptsByDefault;

            if (autoProcess)
            {
                _blockTree.NewBestSuggestedBlock += OnNewBestBlock;
            }

            _stats = new ProcessingStats(_logger);
        }

        private void OnNewBestBlock(object sender, BlockEventArgs blockEventArgs)
        {
            ProcessingOptions options = ProcessingOptions.None;
            if (_storeReceiptsByDefault)
            {
                options |= ProcessingOptions.StoreReceipts;
            }

            Enqueue(blockEventArgs.Block, options);
        }

        public void Enqueue(Block block, ProcessingOptions processingOptions)
        {
            if (_logger.IsTrace) _logger.Trace($"Enqueuing a new block {block.ToString(Block.Format.Short)} for processing.");

            int currentRecoveryQueueSize = Interlocked.Add(ref _currentRecoveryQueueSize, block.Transactions.Length);
            BlockRef blockRef = currentRecoveryQueueSize >= SoftMaxRecoveryQueueSizeInTx ? new BlockRef(block.Hash, processingOptions) : new BlockRef(block, processingOptions);
            if (!_recoveryQueue.IsAddingCompleted)
            {
                try
                {
                    _recoveryQueue.Add(blockRef);
                    if (_logger.IsTrace) _logger.Trace($"A new block {block.ToString(Block.Format.Short)} enqueued for processing.");
                }
                catch (InvalidOperationException)
                {
                    if (!_recoveryQueue.IsAddingCompleted)
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
                await _recoveryTask;
                _blockQueue.CompleteAdding();
            }
            else
            {
                _loopCancellationSource.Cancel();
                _recoveryQueue.CompleteAdding();
                _blockQueue.CompleteAdding();
            }

            await Task.WhenAll(_recoveryTask, _processorTask);
            if (_logger.IsInfo) _logger.Info("Blockchain Processor shutdown complete.. please wait for all components to close");
        }

        private void RunRecoveryLoop()
        {
            if (_logger.IsDebug) _logger.Debug($"Starting recovery loop - {_blockQueue.Count} blocks waiting in the queue.");
            foreach (BlockRef blockRef in _recoveryQueue.GetConsumingEnumerable(_loopCancellationSource.Token))
            {
                if (!blockRef.Resolve(_blockTree))
                {
                    if (_logger.IsTrace) _logger.Trace("Block was removed from the DB and cannot be recovered (it belonged to an invalid branch). Skipping.");
                    continue;
                }

                Interlocked.Add(ref _currentRecoveryQueueSize, -blockRef.Block.Transactions.Length);
                if (_logger.IsTrace) _logger.Trace($"Recovering addresses for block {blockRef.BlockHash?.ToString() ?? blockRef.Block.ToString(Block.Format.Short)}.");
                _recoveryStep.RecoverData(blockRef.Block);

                try
                {
                    _blockQueue.Add(blockRef);
                }
                catch (InvalidOperationException)
                {
                    if (_logger.IsDebug) _logger.Debug($"Recovery loop stopping.");
                    return;
                }
            }
        }

        private void RunProcessingLoop()
        {
            _stats.Start();
            if (_logger.IsDebug) _logger.Debug($"Starting block processor - {_blockQueue.Count} blocks waiting in the queue.");

            if (IsEmpty)
            {
                ProcessingQueueEmpty?.Invoke(this, EventArgs.Empty);
            }

            foreach (BlockRef blockRef in _blockQueue.GetConsumingEnumerable(_loopCancellationSource.Token))
            {
                if (blockRef.IsInDb || blockRef.Block == null)
                {
                    throw new InvalidOperationException("Processing loop expects only resolved blocks");
                }

                Block block = blockRef.Block;

                if (_logger.IsTrace) _logger.Trace($"Processing block {block.ToString(Block.Format.Short)}).");

                IBlockTracer tracer = NullBlockTracer.Instance;

                Block processedBlock = Process(block, blockRef.ProcessingOptions, tracer);
                if (processedBlock == null)
                {
                    if (_logger.IsTrace) _logger.Trace($"Failed / skipped processing {block.ToString(Block.Format.Full)}");
                }
                else
                {
                    if (_logger.IsTrace) _logger.Trace($"Processed block {block.ToString(Block.Format.Full)}");
                    _stats.UpdateStats(block, _recoveryQueue.Count, _blockQueue.Count);
                }

                if (_logger.IsTrace) _logger.Trace($"Now {_blockQueue.Count} blocks waiting in the queue.");
                if (IsEmpty)
                {
                    ProcessingQueueEmpty?.Invoke(this, EventArgs.Empty);
                }
            }

            if (_logger.IsInfo) _logger.Info("Block processor queue stopped.");
        }

        public event EventHandler ProcessingQueueEmpty;

        public bool IsEmpty => _blockQueue.Count == 0 && _recoveryQueue.Count == 0;

        [Todo("Introduce priority queue and create a SuggestWithPriority that waits for block execution to return a block, then make this private")]
        public Block Process(Block suggestedBlock, ProcessingOptions options, IBlockTracer tracer)
        {
            if (!RunSimpleChecksAheadOfProcessing(suggestedBlock, options))
            {
                return null;
            }

            UInt256 totalDifficulty = suggestedBlock.TotalDifficulty ?? 0;
            if (_logger.IsTrace) _logger.Trace($"Total difficulty of block {suggestedBlock.ToString(Block.Format.Short)} is {totalDifficulty}");


            Block[] processedBlocks = null;
            bool shouldProcess = suggestedBlock.IsGenesis
                                 || totalDifficulty > (_blockTree.Head?.TotalDifficulty ?? 0)
                                 // so above is better and more correct but creates an impression of the node staying behind on stats page
                                 // so we are okay to process slightly more
                                 // and below is less correct but potentially reporting well
                                 // || totalDifficulty >= (_blockTree.Head?.TotalDifficulty ?? 0)
                                 || (options & ProcessingOptions.ForceProcessing) == ProcessingOptions.ForceProcessing;
            
            if (!shouldProcess)
            {
                if (_logger.IsDebug) _logger.Debug($"Skipped processing of {suggestedBlock.ToString(Block.Format.FullHashAndNumber)}, Head = {_blockTree.Head?.Header?.ToString(BlockHeader.Format.Short)}, total diff = {totalDifficulty}, head total diff = {_blockTree.Head?.TotalDifficulty}");
                return null;
            }

            ProcessingBranch processingBranch = PrepareProcessingBranch(suggestedBlock, options);
            PrepareBlocksToProcess(suggestedBlock, options, processingBranch);

            try
            {
                processedBlocks = _blockProcessor.Process(processingBranch.Root, processingBranch.BlocksToProcess, options, tracer);
            }
            catch (InvalidBlockException ex)
            {
                for (int i = 0; i < processingBranch.BlocksToProcess.Count; i++)
                {
                    if (processingBranch.BlocksToProcess[i].Hash == ex.InvalidBlockHash)
                    {
                        _blockTree.DeleteInvalidBlock(processingBranch.BlocksToProcess[i]);
                        if (_logger.IsDebug) _logger.Debug($"Skipped processing of {suggestedBlock.ToString(Block.Format.FullHashAndNumber)} because of {processingBranch.BlocksToProcess[i].ToString(Block.Format.FullHashAndNumber)} is invalid");
                        return null;
                    }
                }
            }

            if ((options & (ProcessingOptions.ReadOnlyChain | ProcessingOptions.DoNotUpdateHead)) == 0)
            {
                _blockTree.UpdateMainChain(processingBranch.Blocks.ToArray(), true);
            }

            Block lastProcessed = null;
            if (processedBlocks != null && processedBlocks.Length > 0)
            {
                lastProcessed = processedBlocks[^1];
                if (_logger.IsTrace) _logger.Trace($"Setting total on last processed to {lastProcessed.ToString(Block.Format.Short)}");
                lastProcessed.Header.TotalDifficulty = suggestedBlock.TotalDifficulty;
            }
            else
            {
                if (_logger.IsDebug) _logger.Debug($"Skipped processing of {suggestedBlock.ToString(Block.Format.FullHashAndNumber)}, last processed is null: {lastProcessed == null}, processedBlocks.Length: {processedBlocks?.Length}");
            }

            if ((options & ProcessingOptions.ReadOnlyChain) == ProcessingOptions.None)
            {
                _stats.UpdateStats(lastProcessed, _recoveryQueue.Count, _blockQueue.Count);
            }

            return lastProcessed;
        }

        private void PrepareBlocksToProcess(Block suggestedBlock, ProcessingOptions options, ProcessingBranch processingBranch)
        {
            List<Block> blocksToProcess = processingBranch.BlocksToProcess;
            if ((options & ProcessingOptions.ForceProcessing) != 0)
            {
                processingBranch.Blocks.Clear();
                blocksToProcess.Add(suggestedBlock);
            }
            else
            {
                foreach (Block block in processingBranch.Blocks)
                {
                    if (block.Hash != null && _blockTree.WasProcessed(block.Number, block.Hash))
                    {
                        if (_logger.IsInfo) _logger.Info($"Rerunning block after reorg: {block.ToString(Block.Format.FullHashAndNumber)}");
                    }

                    blocksToProcess.Add(block);
                }

                blocksToProcess.Reverse();
            }

            if (_logger.IsTrace) _logger.Trace($"Processing {blocksToProcess.Count} blocks from state root {processingBranch.Root}");
            for (int i = 0;
                i < blocksToProcess.Count;
                i++)
            {
                /* this can happen if the block was loaded as an ancestor and did not go through the recovery queue */
                _recoveryStep.RecoverData(blocksToProcess[i]);
            }
        }

        private ProcessingBranch PrepareProcessingBranch(Block suggestedBlock, ProcessingOptions options)
        {
            BlockHeader branchingPoint = null;
            List<Block> blocksToBeAddedToMain = new List<Block>();

            Block toBeProcessed = suggestedBlock;
            do

            {
                blocksToBeAddedToMain.Add(toBeProcessed);
                if (_logger.IsTrace) _logger.Trace($"To be processed (of {suggestedBlock.ToString(Block.Format.Short)}) is {toBeProcessed?.ToString(Block.Format.Short)}");
                if (toBeProcessed.IsGenesis)
                {
                    break;
                }

                branchingPoint = _blockTree.FindParentHeader(toBeProcessed.Header, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                if (branchingPoint == null)
                {
                    break; //failure here
                }

                // for beam sync we do not expect previous blocks to necessarily be there and we
                // do not need them since we can requests state from outside
                if ((options & ProcessingOptions.IgnoreParentNotOnMainChain) != 0)
                {
                    break;
                }

                bool headIsGenesis = _blockTree.Head?.IsGenesis ?? false;
                bool toBeProcessedIsNotBlockOne = toBeProcessed.Number > 1;
                bool isFastSyncTransition = headIsGenesis && toBeProcessedIsNotBlockOne;
                if (!isFastSyncTransition)
                {
                    if (_logger.IsTrace) _logger.Trace($"Finding parent of {toBeProcessed.ToString(Block.Format.Short)}");
                    toBeProcessed = _blockTree.FindParent(toBeProcessed.Header, BlockTreeLookupOptions.None);
                    if (_logger.IsTrace) _logger.Trace($"Found parent {toBeProcessed?.ToString(Block.Format.Short)}");

                    if (toBeProcessed == null)
                    {
                        if (_logger.IsDebug) _logger.Debug($"Treating this as fast sync transition for {suggestedBlock.ToString(Block.Format.Short)}");
                        break;
                    }
                }
                else
                {
                    break;
                }
            } while (!_blockTree.IsMainChain(branchingPoint.Hash));

            if (branchingPoint != null && branchingPoint.Hash != _blockTree.Head?.Hash)
            {
                if (_logger.IsTrace) _logger.Trace($"Head block was: {_blockTree.Head?.Header?.ToString(BlockHeader.Format.Short)}");
                if (_logger.IsTrace) _logger.Trace($"Branching from: {branchingPoint.ToString(BlockHeader.Format.Short)}");
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace(branchingPoint == null ? "Setting as genesis block" : $"Adding on top of {branchingPoint.ToString(BlockHeader.Format.Short)}");
            }

            Keccak stateRoot = branchingPoint?.StateRoot;
            if (_logger.IsTrace) _logger.Trace($"State root lookup: {stateRoot}");
            return new ProcessingBranch(stateRoot, blocksToBeAddedToMain);
        }

        [Todo(Improve.Refactor, "This probably can be made conditional (in DEBUG only)")]
        private bool RunSimpleChecksAheadOfProcessing(Block suggestedBlock, ProcessingOptions options)
        {
            /* a bit hacky way to get the invalid branch out of the processing loop */
            if (suggestedBlock.Number != 0 && !_blockTree.IsKnownBlock(suggestedBlock.Number - 1, suggestedBlock.ParentHash))
            {
                if (_logger.IsDebug) _logger.Debug($"Skipping processing block {suggestedBlock.ToString(Block.Format.FullHashAndNumber)} with unknown parent");
                return false;
            }

            if (suggestedBlock.Header.TotalDifficulty == null)
            {
                if (_logger.IsDebug) _logger.Debug($"Skipping processing block {suggestedBlock.ToString(Block.Format.FullHashAndNumber)} without total difficulty");
                throw new InvalidOperationException("Block without total difficulty calculated was suggested for processing");
            }

            if ((options & ProcessingOptions.NoValidation) == 0 && suggestedBlock.Hash == null)
            {
                if (_logger.IsDebug) _logger.Debug($"Skipping processing block {suggestedBlock.ToString(Block.Format.FullHashAndNumber)} without calculated hash");
                throw new InvalidOperationException("Block hash should be known at this stage if running in a validating mode");
            }

            for (int i = 0;
                i < suggestedBlock.Ommers.Length;
                i++)
            {
                if (suggestedBlock.Ommers[i].Hash == null)
                {
                    if (_logger.IsDebug) _logger.Debug($"Skipping processing block {suggestedBlock.ToString(Block.Format.FullHashAndNumber)} with null ommer hash ar {i}");
                    throw new InvalidOperationException($"Ommer's {i} hash is null when processing block");
                }
            }

            return true;
        }

        public void Dispose()
        {
            _recoveryQueue?.Dispose();
            _blockQueue?.Dispose();
            _loopCancellationSource?.Dispose();
            _recoveryTask?.Dispose();
            _processorTask?.Dispose();
            _blockTree.NewBestSuggestedBlock -= OnNewBestBlock;
        }

        private struct ProcessingBranch
        {
            public ProcessingBranch(Keccak root, List<Block> blocks)
            {
                Root = root;
                Blocks = blocks;
                BlocksToProcess = new List<Block>();
                ProcessedBlocks = new List<Block>();
            }

            public Keccak Root { get; }
            public List<Block> Blocks { get; }
            public List<Block> BlocksToProcess { get; }
            public List<Block> ProcessedBlocks { get; }
        }
    }
}