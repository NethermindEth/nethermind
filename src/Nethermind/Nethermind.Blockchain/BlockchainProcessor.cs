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

        private readonly BlockingCollection<Block> _suggestedBlocks = new BlockingCollection<Block>(new ConcurrentQueue<Block>());
        private readonly ITransactionStore _transactionStore;

        public BlockchainProcessor(
            IBlockTree blockTree,
            ISealEngine sealEngine,
            ITransactionStore transactionStore,
            IDifficultyCalculator difficultyCalculator,
            IBlockProcessor blockProcessor,
            ILogger logger)
        {
            _blockTree = blockTree;
            _blockTree.NewBestBlockSuggested += OnNewBestBlock;
            
            _transactionStore = transactionStore;
            _difficultyCalculator = difficultyCalculator;
            _sealEngine = sealEngine;
            _blockProcessor = blockProcessor;
            _logger = logger;
        }

        private void OnNewBestBlock(object sender, BlockEventArgs blockEventArgs)
        {
            SuggestBlock(blockEventArgs.Block);
        }

        public BigInteger TotalTransactions { get; private set; }

        public Block HeadBlock { get; private set; }
        public BigInteger TotalDifficulty { get; private set; }

        private CancellationTokenSource _cancellationSource;
        private Task _processorTask;

        public async Task StopAsync(bool processRamainingBlocks)
        {
            if (processRamainingBlocks)
            {
                _suggestedBlocks.CompleteAdding();
            }
            else
            {
                _cancellationSource.Cancel();
            }

            await _processorTask;
        }

        public void Start()
        {
            _cancellationSource = new CancellationTokenSource();
            _processorTask = Task.Factory.StartNew(() =>
                {
                    _logger?.Info($"Starting block processor - {_suggestedBlocks.Count} blocks waiting in the queue.");
                    if (_suggestedBlocks.Count == 0 && _sealEngine.IsMining)
                    {
                        _logger?.Info("Nothing in the queue so I mine my own.");
                        BuildAndSeal();
                        _logger?.Info("Will go and wait for another block now...");
                    }

                    foreach (Block block in _suggestedBlocks.GetConsumingEnumerable(_cancellationSource.Token))
                    {
                        _logger?.Info($"Processing a suggested block {block.Hash} ({block.Number}).");
                        Process(block);
                        _logger?.Info($"Now {_suggestedBlocks.Count} blocks waiting in the queue.");
                        if (_suggestedBlocks.Count == 0 && _sealEngine.IsMining)
                        {
                            _logger?.Info("Nothing in the queue so I mine my own.");
                            BuildAndSeal();
                            _logger?.Info("Will go and wait for another block now...");
                        }
                    }
                },
                _cancellationSource.Token,
                TaskCreationOptions.None,
                TaskScheduler.Default).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger?.Error($"{nameof(BlockchainProcessor)} encountered an exception {t.Exception}.");
                }
                else if (t.IsCanceled)
                {
                    _logger?.Error($"{nameof(BlockchainProcessor)} stopped.");
                }
                else if (t.IsCompleted)
                {
                    _logger?.Error($"{nameof(BlockchainProcessor)} complete.");
                }
            });
        }

        private void SuggestBlock(Block block)
        {
            _logger?.Info($"Enqueuing a new block {block.Hash} ({block.Number}) for processing.");
            _suggestedBlocks.Add(block);
            _logger?.Info($"A new block {block.Number} ({block.Hash}) suggested for processing.");
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
            _logger?.Debug($"Setting total difficulty to {parentHeader.TotalDifficulty} + {difficulty}.");

            var transactions = _transactionStore.GetAllPending().OrderBy(t => t?.Nonce); // by nonce in case there are two transactions for the same account, TODO: test it
            
            List<Transaction> selected = new List<Transaction>();
            BigInteger gasRemaining = header.GasLimit;
            _logger?.Debug($"Collecting pending transactions at min gas price {MinGasPriceForMining} and block gas limit {gasRemaining}.");
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
                    _logger?.Debug($"Rejecting transaction - gas price ({transaction.GasPrice}) too low (min gas price: {MinGasPriceForMining}.");
                    continue;
                }

                if (transaction.GasLimit > gasRemaining)
                {
                    _logger?.Debug($"Rejecting transaction - gas limit ({transaction.GasPrice}) more than remaining gas ({gasRemaining}).");
                    break;
                }

                selected.Add(transaction);
                gasRemaining -= transaction.GasLimit;
            }

            _logger?.Debug($"Collected {selected.Count} out of {total} pending transactions.");

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
            
            _logger?.Info("-------------------------------------------------------------------------------------");
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
            _logger?.Info($"TOTAL DIFFICULTY OF BLOCK {suggestedBlock.Header.Hash} ({suggestedBlock.Header.Number}) IS {totalDifficulty}");
            _logger?.Info($"TOTAL TRANSACTIONS OF BLOCK {suggestedBlock.Header.Hash} ({suggestedBlock.Header.Number}) IS {totalTransactions}");

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
                    _logger?.Info($"HEAD BLOCK WAS: {HeadBlock?.Hash} ({HeadBlock?.Number})");
                    _logger?.Info($"BRANCHING FROM: {branchingPoint.Hash} ({branchingPoint.Number})");
                }
                else
                {
                    _logger?.Info(branchingPoint == null ? "SETTING AS GENESIS BLOCK" : $"ADDING ON TOP OF {branchingPoint.Hash} ({branchingPoint.Number})");
                }

                Keccak stateRoot = branchingPoint?.Header.StateRoot;
                _logger?.Info($"STATE ROOT LOOKUP: {stateRoot}");
                List<Block> unprocessedBlocksToBeAddedToMain = new List<Block>();

                foreach (Block block in blocksToBeAddedToMain)
                {
                    if (_blockTree.WasProcessed(block.Hash))
                    {
                        stateRoot = block.Header.StateRoot;
                        _logger?.Info($"STATE ROOT LOOKUP: {stateRoot}");
                        break;
                    }

                    unprocessedBlocksToBeAddedToMain.Add(block);
                }

                Block[] blocks = new Block[unprocessedBlocksToBeAddedToMain.Count];
                for (int i = 0; i < unprocessedBlocksToBeAddedToMain.Count; i++)
                {
                    blocks[blocks.Length - i - 1] = unprocessedBlocksToBeAddedToMain[i];
                }

                _logger?.Info($"PROCESSING {blocks.Length} BLOCKS FROM STATE ROOT {stateRoot}");
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
                        _logger?.Info($"MARKING {processedBlock.Hash} ({processedBlock.Number}) AS PROCESSED");
                        _blockTree.MarkAsProcessed(processedBlock.Hash);
                    }
                    
                    HeadBlock = processedBlocks[processedBlocks.Length - 1];
                    HeadBlock.Header.TotalDifficulty = suggestedBlock.TotalDifficulty; // TODO: cleanup total difficulty
                    _logger?.Info($"SETTING HEAD BLOCK TO {HeadBlock.Hash} ({HeadBlock.Number})");

                    foreach (Block block in blocksToBeRemovedFromMain)
                    {
                        _logger?.Info($"MOVING {block.Header.Hash} ({block.Header.Number}) TO BRANCH");
                        _blockTree.MoveToBranch(block.Hash);
                        _logger?.Info($"BLOCK {block.Header.Hash} ({block.Header.Number}) MOVED TO BRANCH");
                    }

                    foreach (Block block in blocksToBeAddedToMain)
                    {
                        _logger?.Info($"MOVING {block.Header.Hash} ({block.Header.Number}) TO MAIN");
                        _blockTree.MoveToMain(block.Hash);
                        _logger?.Info($"BLOCK {block.Header.Hash} ({block.Header.Number}) ADDED TO MAIN CHAIN");
                    }

                    _logger?.Info($"UPDATING TOTAL DIFFICULTY OF THE MAIN CHAIN TO {totalDifficulty}");
                    TotalDifficulty = totalDifficulty;
                    _logger?.Info($"UPDATING TOTAL TRANSACTIONS OF THE MAIN CHAIN TO {totalTransactions}");
                    TotalTransactions = totalTransactions;

                    HeadBlockChanged?.Invoke(this, new BlockEventArgs(HeadBlock));
                }
                else
                {
                    Block blockToBeMined = processedBlocks[processedBlocks.Length - 1];
                    _sealEngine.MineAsync(blockToBeMined, _cancellationSource.Token).ContinueWith(t =>
                    {
                        Block minedBlock = t.Result;
                        minedBlock.Header.RecomputeHash();
                        _blockTree.AddBlock(minedBlock);
                        SuggestBlock(minedBlock);
                    });
                }
            }
        }
    }
}