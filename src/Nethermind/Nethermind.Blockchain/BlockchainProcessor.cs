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
        private readonly IBlockStore _blockStore;
        private readonly IDifficultyCalculator _difficultyCalculator;
        private readonly ILogger _logger;
        private readonly ISealEngine _sealEngine;

        private readonly BlockingCollection<Block> _suggestedBlocks = new BlockingCollection<Block>(new ConcurrentQueue<Block>());
        private readonly ITransactionStore _transactionStore;

        public BlockchainProcessor(
            IBlockProcessor blockProcessor,
            IBlockStore blockStore,
            ITransactionStore transactionStore,
            IDifficultyCalculator difficultyCalculator,
            ISealEngine sealEngine,
            ILogger logger)
        {
            _blockStore = blockStore;
            _transactionStore = transactionStore;
            _difficultyCalculator = difficultyCalculator;
            _sealEngine = sealEngine;
            _blockProcessor = blockProcessor;
            _logger = logger;
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

        public void Start(Block genesisBlock)
        {
            _cancellationSource = new CancellationTokenSource();
            _logger?.Log($"Processing genesis block ({genesisBlock.Hash}).");
            Process(genesisBlock);
            _processorTask = Task.Factory.StartNew(() =>
                {
                    _logger?.Log($"Starting block processor - {_suggestedBlocks.Count} blocks waiting in the queue.");
                    if (_suggestedBlocks.Count == 0 && _sealEngine.IsMining)
                    {
                        _logger?.Log("Nothing in the queue so I mine my own.");
                        BuildAndSeal();
                        _logger?.Log("Will go and wait for another block now...");
                    }

                    foreach (Block block in _suggestedBlocks.GetConsumingEnumerable(_cancellationSource.Token))
                    {
                        _logger?.Log($"Processing a suggested block ({block.Hash}).");
                        Process(block);
                        _logger?.Log($"Now {_suggestedBlocks.Count} blocks waiting in the queue.");
                        if (_suggestedBlocks.Count == 0 && _sealEngine.IsMining)
                        {
                            _logger?.Log("Nothing in the queue so I mine my own.");
                            BuildAndSeal();
                            _logger?.Log("Will go and wait for another block now...");
                        }
                    }
                },
                _cancellationSource.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger?.Error($"Blockchain processor encountered an exception {t.Exception}.");
                }
                else if(t.IsCanceled)
                {
                    _logger?.Error("Blockchain processor stopped.");
                }
                else if(t.IsCompleted)
                {
                    _logger?.Error("Blockchain processor complete.");
                }
            });
        }

        public void SuggestBlock(Block block)
        {
            _logger?.Log($"Enqueuing a new block ({block.Hash}) for processing.");
            _logger?.Debug($"Number {block.Number}");
            _logger?.Debug($"Hash {block.Hash}");
            _suggestedBlocks.Add(block);
            _logger?.Log($"A new block ({block.Hash}) suggested for processing.");
        }

        public event EventHandler<BlockEventArgs> HeadBlockChanged;

        private BigInteger GetTotalTransactions(Block block)
        {
            // TODO: vulnerability if genesis block is propagated with high initial difficulty?
            if (block.Header.Number == 0)
            {
                return block.Transactions.Count;
            }

            Block parent = _blockStore.FindParent(block.Header);
            if (parent == null)
            {
                return 0;
            }

            //Debug.Assert(parent != null, "testing transactions count of an orphaned block");  // ChainAtoChainB_BlockHash
            return block.Transactions.Count + GetTotalTransactions(parent);
        }

        private BigInteger GetTotalDifficulty(BlockHeader blockHeader)
        {
            // TODO: vulnerability if genesis block is propagated with high initial difficulty?
            if (blockHeader.Number == 0)
            {
                return blockHeader.Difficulty;
            }

            Block parent = _blockStore.FindParent(blockHeader);
            if (parent == null)
            {
                return 0;
            }

            //Debug.Assert(parent != null, "testing difficulty of an orphaned block"); // ChainAtoChainB_BlockHash
            return blockHeader.Difficulty + GetTotalDifficulty(parent.Header);
        }

        private void BuildAndSeal()
        {
            BigInteger timestamp = Timestamp.UnixUtcUntilNowSecs;
            BlockHeader parentHeader = HeadBlock.Header;
            BlockHeader header = new BlockHeader(
                parentHeader.Hash,
                Keccak.OfAnEmptySequenceRlp,
                Address.Zero,
                _difficultyCalculator.Calculate(parentHeader.Difficulty, parentHeader.Timestamp, Timestamp.UnixUtcUntilNowSecs, parentHeader.Number + 1, HeadBlock.Ommers.Length > 0),
                parentHeader.Number + 1,
                parentHeader.GasLimit,
                timestamp,
                Encoding.UTF8.GetBytes("Nethermind"));

            Transaction[] transactions = _transactionStore.GetAllPending();
            for (int i = 0; i < transactions.Length; i++)
            {
                Debug.Assert(transactions[i] != null, "transaction is null :/");
            }
            
            List<Transaction> selected = new List<Transaction>();
            BigInteger gasRemaining = header.GasLimit;
            _logger?.Debug($"Collecting pending transactions at min gas price {MinGasPriceForMining} and block gas limit {gasRemaining}.");
            foreach (Transaction transaction in transactions)
            {
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
            
            _logger?.Debug($"Collected {selected.Count} out of {transactions.Length} pending transactions.");

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
            _logger?.Log("-------------------------------------------------------------------------------------");
            suggestedBlock.Header.RecomputeHash();
            foreach (BlockHeader ommerHeader in suggestedBlock.Ommers)
            {
                ommerHeader.RecomputeHash();
            }

            BigInteger totalDifficulty = GetTotalDifficulty(suggestedBlock.Header);
            BigInteger totalTransactions = GetTotalTransactions(suggestedBlock);
            _logger?.Log($"TOTAL DIFFICULTY OF BLOCK {suggestedBlock.Header.Hash} ({suggestedBlock.Header.Number}) IS {totalDifficulty}");
            _logger?.Log($"TOTAL TRANSACTIONS OF BLOCK {suggestedBlock.Header.Hash} ({suggestedBlock.Header.Number}) IS {totalTransactions}");

            if (totalDifficulty > TotalDifficulty)
            {
                List<Block> blocksToBeAddedToMain = new List<Block>();
                Block toBeProcessed = suggestedBlock;
                do
                {
                    blocksToBeAddedToMain.Add(toBeProcessed);
                    toBeProcessed = _blockStore.FindParent(toBeProcessed);
                    if (toBeProcessed == null)
                    {
                        break;
                    }
                } while (!_blockStore.IsMainChain(toBeProcessed.Hash));

                Block branchingPoint = toBeProcessed;
                Keccak stateRoot = branchingPoint?.Header.StateRoot;
                _logger?.Log($"STATE ROOT LOOKUP: {stateRoot}");
                List<Block> unprocessedBlocksToBeAddedToMain = new List<Block>();

                foreach (Block block in blocksToBeAddedToMain)
                {
                    if (_blockStore.WasProcessed(block.Hash))
                    {
                        stateRoot = block.Header.StateRoot;
                        _logger?.Log($"STATE ROOT LOOKUP: {stateRoot}");
                        break;
                    }

                    unprocessedBlocksToBeAddedToMain.Add(block);
                }

                Block[] blocks = new Block[unprocessedBlocksToBeAddedToMain.Count];
                for (int i = 0; i < unprocessedBlocksToBeAddedToMain.Count; i++)
                {
                    blocks[blocks.Length - i - 1] = unprocessedBlocksToBeAddedToMain[i];
                }

                _logger?.Log($"PROCESSING {blocks.Length} BLOCKS FROM STATE ROOT {stateRoot}");
                Block[] processedBlocks = _blockProcessor.Process(stateRoot, blocks, forMining);

                List<Block> blocksToBeRemovedFromMain = new List<Block>();
                if (HeadBlock != branchingPoint && HeadBlock != null)
                {
                    blocksToBeRemovedFromMain.Add(HeadBlock);
                    Block teBeRemovedFromMain = _blockStore.FindParent(HeadBlock);
                    while (teBeRemovedFromMain != null && teBeRemovedFromMain.Hash != branchingPoint?.Hash)
                    {
                        blocksToBeRemovedFromMain.Add(teBeRemovedFromMain);
                        teBeRemovedFromMain = _blockStore.FindParent(teBeRemovedFromMain);
                    }
                }

                if (!forMining)
                {
                    HeadBlock = processedBlocks[processedBlocks.Length - 1];
                    _blockStore.AddBlock(HeadBlock, false);

                    foreach (Block block in blocksToBeRemovedFromMain)
                    {
                        _blockStore.MoveToBranch(block.Hash);
                        _logger?.Log($"BLOCK {block.Header.Hash} ({block.Header.Number}) MOVED TO BRANCH");
                    }

                    foreach (Block block in processedBlocks)
                    {
                        _blockStore.MoveToMain(block.Hash);
                        _logger?.Log($"BLOCK {block.Header.Hash} ({block.Header.Number}) ADDED TO MAIN CHAIN");
                    }

                    _logger?.Log($"UPDATING TOTAL DIFFICULTY OF THE MAIN CHAIN TO {totalDifficulty}");
                    TotalDifficulty = totalDifficulty;
                    _logger?.Log($"UPDATING TOTAL TRANSACTIONS OF THE MAIN CHAIN TO {totalTransactions}");
                    TotalTransactions = totalTransactions;
                    
                    HeadBlockChanged?.Invoke(this, new BlockEventArgs(HeadBlock));
                }
                else
                {
                    Block blockToBeMined = processedBlocks[processedBlocks.Length - 1];
                    _sealEngine.MineAsync(blockToBeMined).ContinueWith(t =>
                    {
                        Block minedBlock = t.Result;
                        minedBlock.Header.RecomputeHash();
                        SuggestBlock(minedBlock);
                    });
                }
            }

            if (!forMining)
            {
                // lower difficulty branch
                _blockStore.AddBlock(suggestedBlock, false);
            }
        }
    }
}