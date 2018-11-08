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
using Nethermind.Core.Logging;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;

namespace Nethermind.Blockchain
{
    public class BlockchainProcessor : IBlockchainProcessor
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
        private const int SoftMaxRecoveryQueueSizeInTx = 10000; // adjust based on tx or gas
        private const int MaxProcessingQueueSize = 2000; // adjust based on tx or gas

        [Todo(Improve.Refactor, "Store receipts by default should be configurable")]
        public BlockchainProcessor(
            IBlockTree blockTree,
            IBlockProcessor blockProcessor,
            IBlockDataRecoveryStep recoveryStep,
            ILogManager logManager,
            bool storeReceiptsByDefault)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _blockProcessor = blockProcessor ?? throw new ArgumentNullException(nameof(blockProcessor));
            _recoveryStep = recoveryStep ?? throw new ArgumentNullException(nameof(recoveryStep));
            _storeReceiptsByDefault = storeReceiptsByDefault;

            _blockTree.NewBestSuggestedBlock += OnNewBestBlock;
            _stats = new ProcessingStats(_logger);
        }

        private void OnNewBestBlock(object sender, BlockEventArgs blockEventArgs)
        {
            SuggestBlock(blockEventArgs.Block, _storeReceiptsByDefault ? ProcessingOptions.StoreReceipts : ProcessingOptions.None);
        }

        public void SuggestBlock(UInt256 blockNumber, ProcessingOptions processingOptions)
        {
            if ((processingOptions & ProcessingOptions.ReadOnlyChain) == 0 ||
                (processingOptions & ProcessingOptions.ForceProcessing) == 0)
            {
                throw new InvalidOperationException("Probably not what you meant as when processing old blocks you should not modify the chain and you need to enforce processing");
            }

            Block block = _blockTree.FindBlock(blockNumber);
            SuggestBlock(block, processingOptions);
        }

        public void SuggestBlock(Keccak blockHash, ProcessingOptions processingOptions)
        {
            Block block = _blockTree.FindBlock(blockHash, false);
            SuggestBlock(block, processingOptions);
        }

        public void SuggestBlock(Block block, ProcessingOptions processingOptions)
        {
            if (_logger.IsTrace) _logger.Trace($"Enqueuing a new block {block.ToString(Block.Format.Short)} for processing.");

            _currentRecoveryQueueSize += block.Transactions.Length;
            BlockRef blockRef = _currentRecoveryQueueSize >= SoftMaxRecoveryQueueSizeInTx ? new BlockRef(block.Hash, processingOptions) : new BlockRef(block, processingOptions);
            if (!_recoveryQueue.IsAddingCompleted)
            {
                _recoveryQueue.Add(blockRef);
                if (_logger.IsTrace) _logger.Trace($"A new block {block.ToString(Block.Format.Short)} enqueued for processing.");
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
                ResolveBlockRef(blockRef);
                _currentRecoveryQueueSize -= blockRef.Block.Transactions.Length;
                if (_logger.IsTrace) _logger.Trace($"Recovering addresses for block {blockRef.BlockHash ?? blockRef.Block.Hash}.");
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

        private void ResolveBlockRef(BlockRef blockRef)
        {
            if (blockRef.IsInDb)
            {
                Block block = _blockTree.FindBlock(blockRef.BlockHash, false);
                if (block == null)
                {
                    throw new InvalidOperationException($"Cannot resolve block reference for {blockRef.BlockHash}");
                }

                blockRef.Block = block;
                blockRef.BlockHash = null;
                blockRef.IsInDb = false;
            }
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
                Process(block, blockRef.ProcessingOptions, NullTraceListener.Instance);
                if (_logger.IsTrace) _logger.Trace($"Processed block {block.ToString(Block.Format.Full)}");

                _stats.UpdateStats(block, _recoveryQueue.Count, _blockQueue.Count);

                if (_logger.IsTrace) _logger.Trace($"Now {_blockQueue.Count} blocks waiting in the queue.");
                if (_blockQueue.Count == 0)
                {
                    ProcessingQueueEmpty?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public event EventHandler ProcessingQueueEmpty;

        [Todo("Introduce priority queue and create a SuggestWithPriority that waits for block execution to return a block, then make this private")]
        public Block Process(Block suggestedBlock, ProcessingOptions options, ITraceListener traceListener)
        {
            RunSimpleChecksAheadOfProcessing(suggestedBlock, options);

            UInt256 totalDifficulty = suggestedBlock.TotalDifficulty ?? 0;
            if (_logger.IsTrace) _logger.Trace($"Total difficulty of block {suggestedBlock.ToString(Block.Format.Short)} is {totalDifficulty}");
            UInt256 totalTransactions = suggestedBlock.TotalTransactions ?? 0;
            if (_logger.IsTrace) _logger.Trace($"Total transactions of block {suggestedBlock.ToString(Block.Format.Short)} is {totalTransactions}");

            Block[] processedBlocks = null;
            if (totalDifficulty > (_blockTree.Head?.TotalDifficulty ?? 0) || (options & ProcessingOptions.ForceProcessing) != 0)
            {
                List<Block> blocksToBeAddedToMain = new List<Block>();
                Block toBeProcessed = suggestedBlock;
                do
                {
                    blocksToBeAddedToMain.Add(toBeProcessed);
                    toBeProcessed = toBeProcessed.Number == 0 ? null : _blockTree.FindParent(toBeProcessed);
                    // TODO: need to remove the hardcoded head block store at keccak zero as it would be referenced by the genesis... 
                    if (toBeProcessed == null)
                    {
                        break;
                    }
                } while (!_blockTree.IsMainChain(toBeProcessed.Hash));

                BlockHeader branchingPoint = toBeProcessed?.Header;
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

                List<Block> unprocessedBlocksToBeAddedToMain = new List<Block>();
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
                        if (block.Hash != null && _blockTree.WasProcessed(block.Hash))
                        {
                            stateRoot = block.Header.StateRoot;
                            if (_logger.IsTrace) _logger.Trace($"State root lookup: {stateRoot}");
                            break;
                        }

                        unprocessedBlocksToBeAddedToMain.Add(block);
                    }

                    blocks = new Block[unprocessedBlocksToBeAddedToMain.Count];
                    for (int i = 0; i < unprocessedBlocksToBeAddedToMain.Count; i++)
                    {
                        blocks[blocks.Length - i - 1] = unprocessedBlocksToBeAddedToMain[i];
                    }
                }

                if (_logger.IsTrace) _logger.Trace($"Processing {blocks.Length} blocks from state root {stateRoot}");

                for (int i = 0; i < blocks.Length; i++)
                {
                    /* this can happen if the block was loaded as an ancestor and did not go through the recovery queue */
                    _recoveryStep.RecoverData(blocks[i]);
                }

                processedBlocks = _blockProcessor.Process(stateRoot, blocks, options, traceListener);
                if ((options & ProcessingOptions.ReadOnlyChain) == 0)
                {
                    // TODO: lots of unnecessary loading and decoding here, review after adding support for loading headers only
                    List<BlockHeader> blocksToBeRemovedFromMain = new List<BlockHeader>();
                    if (_blockTree.Head?.Hash != branchingPoint?.Hash && _blockTree.Head != null)
                    {
                        blocksToBeRemovedFromMain.Add(_blockTree.Head);
                        BlockHeader teBeRemovedFromMain = _blockTree.FindHeader(_blockTree.Head.ParentHash);
                        while (teBeRemovedFromMain != null && teBeRemovedFromMain.Hash != branchingPoint?.Hash)
                        {
                            blocksToBeRemovedFromMain.Add(teBeRemovedFromMain);
                            teBeRemovedFromMain = _blockTree.FindHeader(teBeRemovedFromMain.ParentHash);
                        }
                    }

                    for (int i = 0; i < processedBlocks.Length; i++)
                    {
                        _blockTree.MarkAsProcessed(processedBlocks[i].Hash);
                        if (i == processedBlocks.Length - 1)
                        {
                            if (_logger.IsTrace) _logger.Trace($"Setting total on last processed to {processedBlocks[i].ToString(Block.Format.Short)}");
                            processedBlocks[i].Header.TotalDifficulty = suggestedBlock.TotalDifficulty;
                        }
                    }

                    foreach (BlockHeader blockHeader in blocksToBeRemovedFromMain)
                    {
                        _blockTree.MoveToBranch(blockHeader.Hash);
                    }

                    foreach (Block block in blocksToBeAddedToMain)
                    {
                        _blockTree.MoveToMain(block);
                    }
                }
            }

            return (processedBlocks?.Length ?? 0) > 0 ? processedBlocks[processedBlocks.Length - 1] : null;
        }

        private void RunSimpleChecksAheadOfProcessing(Block suggestedBlock, ProcessingOptions options)
        {
            if (suggestedBlock.Number != 0 && _blockTree.FindParent(suggestedBlock) == null)
            {
                throw new InvalidOperationException("Got an orphaned block for porcessing.");
            }

            if (suggestedBlock.Header.TotalDifficulty == null)
            {
                throw new InvalidOperationException("Block without total difficulty calculated was suggested for processing");
            }

            if ((options & ProcessingOptions.ReadOnlyChain) == 0 && suggestedBlock.Hash == null)
            {
                throw new InvalidOperationException("Block hash should be known at this stage if the block is not read only");
            }

            for (int i = 0; i < suggestedBlock.Ommers.Length; i++)
            {
                if (suggestedBlock.Ommers[i].Hash == null)
                {
                    throw new InvalidOperationException($"Ommer's {i} hash is null when processing block");
                }
            }
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