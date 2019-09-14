/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;

namespace Nethermind.Blockchain
{
    public class BlockchainProcessor : IBlockchainProcessor
    {
        private readonly IBlockProcessor _blockProcessor;
        private readonly IBlockDataRecoveryStep _recoveryStep;
        private readonly bool _storeReceiptsByDefault;
        private readonly bool _storeTracesByDefault;
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

        [Todo(Improve.Refactor, "Store receipts by default should be configurable")]
        public BlockchainProcessor(
            IBlockTree blockTree,
            IBlockProcessor blockProcessor,
            IBlockDataRecoveryStep recoveryStep,
            ILogManager logManager,
            bool storeReceiptsByDefault,
            bool storeTracesByDefault)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _blockProcessor = blockProcessor ?? throw new ArgumentNullException(nameof(blockProcessor));
            _recoveryStep = recoveryStep ?? throw new ArgumentNullException(nameof(recoveryStep));
            _storeReceiptsByDefault = storeReceiptsByDefault;
            _storeTracesByDefault = storeTracesByDefault;

            _blockTree.NewBestSuggestedBlock += OnNewBestBlock;
            _stats = new ProcessingStats(_logger);
        }

        private void OnNewBestBlock(object sender, BlockEventArgs blockEventArgs)
        {
            ProcessingOptions options = ProcessingOptions.None;
            if (_storeReceiptsByDefault)
            {
                options |= ProcessingOptions.StoreReceipts;
            }

            if (_storeTracesByDefault)
            {
                options |= ProcessingOptions.StoreTraces;
            }

            SuggestBlock(blockEventArgs.Block, options);
        }

        public void SuggestBlock(Block block, ProcessingOptions processingOptions)
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

        public async Task StopAsync(bool processRemainingBlocks)
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
                if (!ResolveBlockRef(blockRef))
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

        private bool ResolveBlockRef(BlockRef blockRef)
        {
            if (blockRef.IsInDb)
            {
                Block block = _blockTree.FindBlock(blockRef.BlockHash, BlockTreeLookupOptions.None);
                if (block == null)
                {
                    return false;
                }

                blockRef.Block = block;
                blockRef.BlockHash = null;
                blockRef.IsInDb = false;
            }

            return true;
        }

        private void RunProcessingLoop()
        {
            _stats.Start();
            if (_logger.IsDebug) _logger.Debug($"Starting block processor - {_blockQueue.Count} blocks waiting in the queue.");

            if (_blockQueue.Count == 0)
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
                if ((blockRef.ProcessingOptions & ProcessingOptions.StoreTraces) != 0)
                {
                    tracer = new ParityLikeBlockTracer(ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
                }

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
                if (_blockQueue.Count == 0)
                {
                    ProcessingQueueEmpty?.Invoke(this, EventArgs.Empty);
                }
            }

            if (_logger.IsTrace) _logger.Trace($"Returns from processing loop");
        }

        public event EventHandler ProcessingQueueEmpty;

        [Todo("Introduce priority queue and create a SuggestWithPriority that waits for block execution to return a block, then make this private")]
        public Block Process(Block suggestedBlock, ProcessingOptions options, IBlockTracer blockTracer)
        {
            if (!RunSimpleChecksAheadOfProcessing(suggestedBlock, options))
            {
                return null;
            }

            UInt256 totalDifficulty = suggestedBlock.TotalDifficulty ?? 0;
            if (_logger.IsTrace) _logger.Trace($"Total difficulty of block {suggestedBlock.ToString(Block.Format.Short)} is {totalDifficulty}");

            BlockHeader branchingPoint = null;
            Block[] processedBlocks = null;
            if (_blockTree.Head == null || totalDifficulty > _blockTree.Head.TotalDifficulty || (options & ProcessingOptions.ForceProcessing) != 0)
            {
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

                    bool isFastSyncTransition = _blockTree.Head == _blockTree.Genesis && toBeProcessed.Number > 1; 
                    if (!isFastSyncTransition)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Finding parent of {toBeProcessed.ToString(Block.Format.Short)}");
                        toBeProcessed = _blockTree.FindParent(toBeProcessed.Header, BlockTreeLookupOptions.None);
                        if (_logger.IsTrace) _logger.Trace($"Found parent {toBeProcessed?.ToString(Block.Format.Short)}");

                        if (toBeProcessed == null)
                        {
                            if (_logger.IsTrace) _logger.Trace($"Treating this as fast sync transition for {suggestedBlock.ToString(Block.Format.Short)}");
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
                    if (_logger.IsTrace) _logger.Trace($"Head block was: {_blockTree.Head?.ToString(BlockHeader.Format.Short)}");
                    if (_logger.IsTrace) _logger.Trace($"Branching from: {branchingPoint.ToString(BlockHeader.Format.Short)}");
                }
                else
                {
                    if (_logger.IsTrace) _logger.Trace(branchingPoint == null ? "Setting as genesis block" : $"Adding on top of {branchingPoint.ToString(BlockHeader.Format.Short)}");
                }

                Keccak stateRoot = branchingPoint?.StateRoot;
                if (_logger.IsTrace) _logger.Trace($"State root lookup: {stateRoot}");

                List<Block> blocksToProcess = new List<Block>();
                Block[] blocks;
                if ((options & ProcessingOptions.ForceProcessing) != 0)
                {
                    blocksToBeAddedToMain.Clear();
                    blocks = new Block[1];
                    blocks[0] = suggestedBlock;
                }
                else
                {
                    foreach (Block block in blocksToBeAddedToMain)
                    {
                        if (block.Hash != null && _blockTree.WasProcessed(block.Number, block.Hash))
                        {
                            if (_logger.IsInfo) _logger.Info($"Rerunning block after reorg: {block.ToString(Block.Format.FullHashAndNumber)}");
                        }
                        
                        blocksToProcess.Add(block);
                    }

                    blocks = new Block[blocksToProcess.Count];
                    for (int i = 0; i < blocksToProcess.Count; i++)
                    {
                        blocks[blocks.Length - i - 1] = blocksToProcess[i];
                    }
                }

                if (_logger.IsTrace) _logger.Trace($"Processing {blocks.Length} blocks from state root {stateRoot}");

                for (int i = 0; i < blocks.Length; i++)
                {
                    /* this can happen if the block was loaded as an ancestor and did not go through the recovery queue */
                    _recoveryStep.RecoverData(blocks[i]);
                }

                try
                {
                    processedBlocks = _blockProcessor.Process(stateRoot, blocks, options, blockTracer);
                }
                catch (InvalidBlockException ex)
                {
                    for (int i = 0; i < blocks.Length; i++)
                    {
                        if (blocks[i].Hash == ex.InvalidBlockHash)
                        {
                            _blockTree.DeleteInvalidBlock(blocks[i]);
                            if (_logger.IsDebug) _logger.Debug($"Skipped processing of {suggestedBlock.ToString(Block.Format.FullHashAndNumber)} because of {blocks[i].ToString(Block.Format.FullHashAndNumber)} is invalid");
                            return null;
                        }
                    }
                }

                if ((options & ProcessingOptions.ReadOnlyChain) == 0)
                {
                    _blockTree.UpdateMainChain(blocksToBeAddedToMain.ToArray());
                }
            }
            else
            {
                if (_logger.IsDebug) _logger.Debug($"Skipped processing of {suggestedBlock.ToString(Block.Format.FullHashAndNumber)}, Head = {_blockTree.Head?.ToString(BlockHeader.Format.Short)}, total diff = {totalDifficulty}, head total diff = {_blockTree.Head?.TotalDifficulty}");
            }

            Block lastProcessed = null;
            if (processedBlocks != null && processedBlocks.Length > 0)
            {
                lastProcessed = processedBlocks[processedBlocks.Length - 1];
                if (_logger.IsTrace) _logger.Trace($"Setting total on last processed to {lastProcessed.ToString(Block.Format.Short)}");
                lastProcessed.TotalDifficulty = suggestedBlock.TotalDifficulty;
            }
            else
            {
                if (_logger.IsDebug) _logger.Debug($"Skipped processing of {suggestedBlock.ToString(Block.Format.FullHashAndNumber)}, last processed is null: {lastProcessed == null}, processedBlocks.Length: {processedBlocks?.Length}");
            }

            return lastProcessed;
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

            if ((options & ProcessingOptions.ReadOnlyChain) == 0 && suggestedBlock.Hash == null)
            {
                if (_logger.IsDebug) _logger.Debug($"Skipping processing block {suggestedBlock.ToString(Block.Format.FullHashAndNumber)} without calculated hash");
                throw new InvalidOperationException("Block hash should be known at this stage if the block is not read only");
            }

            for (int i = 0; i < suggestedBlock.Ommers.Length; i++)
            {
                if (suggestedBlock.Ommers[i].Hash == null)
                {
                    if (_logger.IsDebug) _logger.Debug($"Skipping processing block {suggestedBlock.ToString(Block.Format.FullHashAndNumber)} with null ommer hash ar {i}");
                    throw new InvalidOperationException($"Ommer's {i} hash is null when processing block");
                }
            }

            return true;
        }

        private class BlockRef
        {
            public BlockRef(Block block, ProcessingOptions processingOptions = ProcessingOptions.None)
            {
                Block = block;
                ProcessingOptions = processingOptions;
                IsInDb = false;
                BlockHash = null;
            }

            public BlockRef(Keccak blockHash, ProcessingOptions processingOptions = ProcessingOptions.None)
            {
                Block = null;
                IsInDb = true;
                BlockHash = blockHash;
                ProcessingOptions = processingOptions;
            }

            public bool IsInDb { get; set; }
            public Keccak BlockHash { get; set; }
            public Block Block { get; set; }
            public ProcessingOptions ProcessingOptions { get; }
        }
    }
}