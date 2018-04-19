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
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Difficulty;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Store;

namespace Nethermind.Blockchain
{
    public class BlockchainProcessor : IBlockchainProcessor
    {
        private static readonly BigInteger MinGasPriceForMining = 1;
        private readonly IBlockProcessor _blockProcessor;
        private readonly IBlockTree _blockTree;
        private readonly IDifficultyCalculator _difficultyCalculator;
        private readonly ILogger _logger;
        private readonly ISealEngine _sealEngine;

        private readonly BlockingCollection<Block> _blockQueue = new BlockingCollection<Block>(new ConcurrentQueue<Block>());
        private readonly ITransactionStore _transactionStore;

        public BlockchainProcessor(
            IBlockTree blockTree,
            ISealEngine sealEngine,
            ITransactionStore transactionStore,
            IDifficultyCalculator difficultyCalculator,
            IBlockProcessor blockProcessor,
            ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _blockTree = blockTree;
            _blockTree.NewBestSuggestedBlock += OnNewBestBlock;

            _transactionStore = transactionStore;
            _difficultyCalculator = difficultyCalculator;
            _sealEngine = sealEngine;
            _blockProcessor = blockProcessor;
        }

        private void OnNewBestBlock(object sender, BlockEventArgs blockEventArgs)
        {
            _miningCancellation?.Cancel();
            EnqueueForProcessing(blockEventArgs.Block);
        }

        public BigInteger TotalTransactions { get; private set; }

        public Block HeadBlock { get; private set; }
        public BigInteger TotalDifficulty { get; private set; }

        private CancellationTokenSource _blockchainCancellation;
        private CancellationTokenSource _miningCancellation;

        private Task _processorTask;

        public async Task StopAsync(bool processRamainingBlocks)
        {
            if (processRamainingBlocks)
            {
                _blockQueue.CompleteAdding();
            }
            else
            {
                _blockchainCancellation.Cancel();
            }

            await _processorTask;
        }

        public void Start()
        {
            _blockchainCancellation = new CancellationTokenSource();
            _processorTask = Task.Factory.StartNew(() =>
                {
                    if (_logger.IsInfoEnabled)
                    {
                        _logger.Info($"Starting block processor - {_blockQueue.Count} blocks waiting in the queue.");
                    }

                    if (_blockQueue.Count == 0 && _sealEngine.IsMining)
                    {
                        if (_logger.IsDebugEnabled)
                        {
                            _logger.Debug("Nothing in the queue so I mine my own.");
                        }

                        BuildAndSeal();

                        if (_logger.IsDebugEnabled)
                        {
                            _logger.Debug("Will go and wait for another block now...");
                        }
                    }

                    foreach (Block block in _blockQueue.GetConsumingEnumerable(_blockchainCancellation.Token))
                    {
                        if (_logger.IsInfoEnabled)
                        {
                            _logger.Info($"Block processing {block.Hash} ({block.Number}).");
                        }

                        Process(block);

                        if (_logger.IsDebugEnabled) // TODO: different levels depending on the queue size?
                        {
                            _logger.Debug($"Now {_blockQueue.Count} blocks waiting in the queue.");
                        }

                        if (_blockQueue.Count == 0 && _sealEngine.IsMining)
                        {
                            if (_logger.IsDebugEnabled)
                            {
                                _logger.Debug("Nothing in the queue so I mine my own.");
                            }

                            BuildAndSeal();

                            if (_logger.IsDebugEnabled)
                            {
                                _logger.Debug("Will go and wait for another block now...");
                            }
                        }
                    }
                },
                _blockchainCancellation.Token,
                TaskCreationOptions.None,
                TaskScheduler.Default).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    if (_logger.IsErrorEnabled)
                    {
                        _logger.Error($"{nameof(BlockchainProcessor)} encountered an exception {t.Exception}.");
                    }
                }
                else if (t.IsCanceled)
                {
                    if (_logger.IsInfoEnabled)
                    {
                        _logger.Info($"{nameof(BlockchainProcessor)} stopped.");
                    }
                }
                else if (t.IsCompleted)
                {
                    if (_logger.IsInfoEnabled)
                    {
                        _logger.Info($"{nameof(BlockchainProcessor)} complete.");
                    }
                }
            });
        }

        private void EnqueueForProcessing(Block block)
        {
            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"Enqueuing a new block {block.Hash} ({block.Number}) for processing.");
            }

            _blockQueue.Add(block);

            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"A new block {block.Number} ({block.Hash}) enqueued for processing.");
            }
        }

        public event EventHandler<BlockEventArgs> HeadBlockChanged;

        private BigInteger GetTotalTransactions(Block block)
        {
            // TODO: vulnerability if genesis block is propagated with high initial difficulty?
            if (block.Header.Number == 0)
            {
                return block.Transactions.Length;
            }

            Block parent = _blockTree.FindParent(block.Header);
            if (parent == null)
            {
                return 0;
            }

            //Debug.Assert(parent != null, "testing transactions count of an orphaned block");  // ChainAtoChainB_BlockHash
            return block.Transactions.Length + GetTotalTransactions(parent);
        }

        // TODO: there will be a need for cancellation of current mining whenever a new block arrives
        private void BuildAndSeal()
        {
            if (HeadBlock == null)
            {
                return;
            }

            BigInteger timestamp = Timestamp.UnixUtcUntilNowSecs;
            BlockHeader parentHeader = HeadBlock.Header;
            BigInteger difficulty = _difficultyCalculator.Calculate(parentHeader.Difficulty, parentHeader.Timestamp, Timestamp.UnixUtcUntilNowSecs, parentHeader.Number + 1, HeadBlock.Ommers.Length > 0);
            BlockHeader header = new BlockHeader(
                parentHeader.Hash,
                Keccak.OfAnEmptySequenceRlp,
                Address.Zero,
                difficulty,
                parentHeader.Number + 1,
                parentHeader.GasLimit,
                timestamp,
                Encoding.UTF8.GetBytes("Nethermind"));

            header.TotalDifficulty = parentHeader.TotalDifficulty + difficulty;
            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"Setting total difficulty to {parentHeader.TotalDifficulty} + {difficulty}.");
            }

            var transactions = _transactionStore.GetAllPending().OrderBy(t => t?.Nonce); // by nonce in case there are two transactions for the same account, TODO: test it

            List<Transaction> selected = new List<Transaction>();
            BigInteger gasRemaining = header.GasLimit;

            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"Collecting pending transactions at min gas price {MinGasPriceForMining} and block gas limit {gasRemaining}.");
            }

            int total = 0;
            foreach (Transaction transaction in transactions)
            {
                total++;
                Debug.Assert(transaction != null, "transaction is null :/");
                if (transaction == null)
                {
                    continue;
                }

                if (transaction.GasPrice < MinGasPriceForMining)
                {
                    if (_logger.IsDebugEnabled)
                    {
                        _logger.Debug($"Rejecting transaction - gas price ({transaction.GasPrice}) too low (min gas price: {MinGasPriceForMining}.");
                    }

                    continue;
                }

                if (transaction.GasLimit > gasRemaining)
                {
                    if (_logger.IsInfoEnabled)
                    {
                        _logger.Debug($"Rejecting transaction - gas limit ({transaction.GasPrice}) more than remaining gas ({gasRemaining}).");
                    }

                    break;
                }

                selected.Add(transaction);
                gasRemaining -= transaction.GasLimit;
            }

            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"Collected {selected.Count} out of {total} pending transactions.");
            }

            header.TransactionsRoot = GetTransactionsRoot(selected);

            Block block = new Block(header, selected, new BlockHeader[0]);
            Process(block, true);
        }

        private Keccak GetTransactionsRoot(List<Transaction> transactions)
        {
            PatriciaTree tranTree = new PatriciaTree();
            for (int i = 0; i < transactions.Count; i++)
            {
                Rlp transactionRlp = Rlp.Encode(transactions[i]);
                tranTree.Set(Rlp.Encode(i).Bytes, transactionRlp);
            }

            return tranTree.RootHash;
        }

        public void Process(Block suggestedBlock)
        {
            Process(suggestedBlock, false);
        }

        private void Process(Block suggestedBlock, bool forMining)
        {
            Debug.Assert(suggestedBlock.Number == 0 || _blockTree.FindParent(suggestedBlock) != null, "Got an orphaned block for porcessing.");
            Debug.Assert(suggestedBlock.Header.TotalDifficulty != null, "block without total difficulty calculated was suggested for processing");

            if (!forMining)
            {
                Debug.Assert(suggestedBlock.Hash != null, "block hash should be known at this stage if the block is not mining");
            }

            foreach (BlockHeader ommerHeader in suggestedBlock.Ommers)
            {
                Debug.Assert(ommerHeader.Hash != null, "ommer's hash should be known at this stage");
            }

            BigInteger totalDifficulty = suggestedBlock.TotalDifficulty ?? 0;
            BigInteger totalTransactions = GetTotalTransactions(suggestedBlock);
            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"TOTAL DIFFICULTY OF BLOCK {suggestedBlock.Header.Hash} ({suggestedBlock.Header.Number}) IS {totalDifficulty}");
                _logger.Debug($"TOTAL TRANSACTIONS OF BLOCK {suggestedBlock.Header.Hash} ({suggestedBlock.Header.Number}) IS {totalTransactions}");
            }

            if (totalDifficulty > TotalDifficulty)
            {
                List<Block> blocksToBeAddedToMain = new List<Block>();
                Block toBeProcessed = suggestedBlock;
                do
                {
                    blocksToBeAddedToMain.Add(toBeProcessed);
                    toBeProcessed = _blockTree.FindParent(toBeProcessed);
                    if (toBeProcessed == null)
                    {
                        break;
                    }
                } while (!_blockTree.IsMainChain(toBeProcessed.Hash));

                Block branchingPoint = toBeProcessed;
                if (branchingPoint != null && branchingPoint.Hash != HeadBlock?.Hash)
                {
                    if (_logger.IsDebugEnabled)
                    {
                        _logger.Debug($"HEAD BLOCK WAS: {HeadBlock?.Hash} ({HeadBlock?.Number})");
                        _logger.Debug($"BRANCHING FROM: {branchingPoint.Hash} ({branchingPoint.Number})");
                    }
                }
                else
                {
                    if (_logger.IsDebugEnabled)
                    {
                        _logger.Debug(branchingPoint == null ? "SETTING AS GENESIS BLOCK" : $"ADDING ON TOP OF {branchingPoint.Hash} ({branchingPoint.Number})");
                    }
                }

                Keccak stateRoot = branchingPoint?.Header.StateRoot;
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"STATE ROOT LOOKUP: {stateRoot}");
                }

                List<Block> unprocessedBlocksToBeAddedToMain = new List<Block>();

                foreach (Block block in blocksToBeAddedToMain)
                {
                    if (!forMining && _blockTree.WasProcessed(block.Hash))
                    {
                        stateRoot = block.Header.StateRoot;
                        if (_logger.IsDebugEnabled)
                        {
                            _logger.Debug($"STATE ROOT LOOKUP: {stateRoot}");
                        }

                        break;
                    }

                    unprocessedBlocksToBeAddedToMain.Add(block);
                }

                Block[] blocks = new Block[unprocessedBlocksToBeAddedToMain.Count];
                for (int i = 0; i < unprocessedBlocksToBeAddedToMain.Count; i++)
                {
                    blocks[blocks.Length - i - 1] = unprocessedBlocksToBeAddedToMain[i];
                }

                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"PROCESSING {blocks.Length} BLOCKS FROM STATE ROOT {stateRoot}");
                }

                Block[] processedBlocks = _blockProcessor.Process(stateRoot, blocks, forMining);

                List<Block> blocksToBeRemovedFromMain = new List<Block>();
                if (HeadBlock?.Hash != branchingPoint?.Hash && HeadBlock != null)
                {
                    blocksToBeRemovedFromMain.Add(HeadBlock);
                    Block teBeRemovedFromMain = _blockTree.FindParent(HeadBlock);
                    while (teBeRemovedFromMain != null && teBeRemovedFromMain.Hash != branchingPoint?.Hash)
                    {
                        blocksToBeRemovedFromMain.Add(teBeRemovedFromMain);
                        teBeRemovedFromMain = _blockTree.FindParent(teBeRemovedFromMain);
                    }
                }

                if (!forMining)
                {
                    foreach (Block processedBlock in processedBlocks)
                    {
                        if (_logger.IsDebugEnabled)
                        {
                            _logger.Debug($"MARKING {processedBlock.Hash} ({processedBlock.Number}) AS PROCESSED");
                        }

                        _blockTree.MarkAsProcessed(processedBlock.Hash);
                    }

                    HeadBlock = processedBlocks[processedBlocks.Length - 1];
                    HeadBlock.Header.TotalDifficulty = suggestedBlock.TotalDifficulty; // TODO: cleanup total difficulty
                    if (_logger.IsDebugEnabled)
                    {
                        _logger.Debug($"SETTING HEAD BLOCK TO {HeadBlock.Hash} ({HeadBlock.Number})");
                    }

                    foreach (Block block in blocksToBeRemovedFromMain)
                    {
                        if (_logger.IsDebugEnabled)
                        {
                            _logger.Debug($"MOVING {block.Header.Hash} ({block.Header.Number}) TO BRANCH");
                        }

                        _blockTree.MoveToBranch(block.Hash);
                        foreach (Transaction transaction in block.Transactions)
                        {
                            _transactionStore.AddPending(transaction);
                        }

                        if (_logger.IsDebugEnabled)
                        {
                            _logger.Debug($"BLOCK {block.Header.Hash} ({block.Header.Number}) MOVED TO BRANCH");
                        }
                    }

                    foreach (Block block in blocksToBeAddedToMain)
                    {
                        if (_logger.IsDebugEnabled)
                        {
                            _logger.Debug($"MOVING {block.Header.Hash} ({block.Header.Number}) TO MAIN");
                        }

                        _blockTree.MoveToMain(block.Hash);
                        foreach (Transaction transaction in block.Transactions)
                        {
                            _transactionStore.RemovePending(transaction);
                        }

                        if (_logger.IsDebugEnabled)
                        {
                            _logger.Debug($"BLOCK {block.Header.Hash} ({block.Header.Number}) ADDED TO MAIN CHAIN");
                        }
                    }

                    if (_logger.IsDebugEnabled)
                    {
                        _logger.Debug($"UPDATING TOTAL DIFFICULTY OF THE MAIN CHAIN TO {totalDifficulty}");
                    }

                    TotalDifficulty = totalDifficulty;

                    if (_logger.IsDebugEnabled)
                    {
                        _logger.Debug($"UPDATING TOTAL TRANSACTIONS OF THE MAIN CHAIN TO {totalTransactions}");
                    }

                    TotalTransactions = totalTransactions;

                    HeadBlockChanged?.Invoke(this, new BlockEventArgs(HeadBlock));
                }
                else
                {
                    Block blockToBeMined = processedBlocks[processedBlocks.Length - 1];
                    _miningCancellation = new CancellationTokenSource();
                    CancellationTokenSource anyCancellation =
                        CancellationTokenSource.CreateLinkedTokenSource(_miningCancellation.Token, _blockchainCancellation.Token);
                    _sealEngine.MineAsync(blockToBeMined, anyCancellation.Token).ContinueWith(t =>
                    {
                        anyCancellation.Dispose();
                        
                        if (_logger.IsInfoEnabled)
                        {
                            _logger.Info($"Mined a block {t.Result.Hash} ({t.Result.Number})");
                        }

                        Block minedBlock = t.Result;
                        minedBlock.Header.RecomputeHash();
                        _blockTree.SuggestBlock(minedBlock);
                    }, _miningCancellation.Token);
                }
            }
        }
    }
}