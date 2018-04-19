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
using System.Diagnostics;
using System.Numerics;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

namespace Nethermind.Blockchain
{
    // TODO: work in progress
    public class BlockTree : IBlockTree
    {
        private readonly ConcurrentDictionary<Keccak, Block> _branches = new ConcurrentDictionary<Keccak, Block>();
        private readonly ConcurrentDictionary<int, Block> _canonicalChain = new ConcurrentDictionary<int, Block>();
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<Keccak, Block> _mainChain = new ConcurrentDictionary<Keccak, Block>();
        private readonly ConcurrentDictionary<Keccak, bool> _processed = new ConcurrentDictionary<Keccak, bool>();

        // TODO: validators should be here
        public BlockTree(ISpecProvider specProvider, ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            ChainId = specProvider.ChainId;
        }

        public event EventHandler<BlockEventArgs> BlockAddedToMain;

        public event EventHandler<BlockEventArgs> NewBestSuggestedBlock;

        public event EventHandler<BlockEventArgs> NewHeadBlock;

        private readonly object _syncObject = new object();

        public Block GenesisBlock { get; private set; }
        public Block HeadBlock { get; private set; }
        public Block BestSuggestedBlock { get; private set; }
        public int ChainId { get; }

        public AddBlockResult AddBlock(Block block)
        {
            if (block.Number == 0)
            {
                if (BestSuggestedBlock != null)
                {
                    throw new InvalidOperationException("Genesis block should be added only once"); // TODO: make sure it cannot happen
                }
            }
            else if(FindBlock(block.Hash, false) != null)
            {
                return AddBlockResult.AlreadyKnown;
            }
            else if (this.FindParent(block.Header) == null)
            {
                return AddBlockResult.UnknownParent;
            }

            _branches.AddOrUpdate(block.Header.Hash, block, (h, b) =>
            {
                //https://stackoverflow.com/questions/17926519/concurrent-dictionary-addorupdate-vs-index-add?utm_medium=organic&utm_source=google_rich_qa&utm_campaign=google_rich_qa
                Debug.Assert(block == b, "Assuming it would not happen, if never fired use indexer instead");
                return b;
            });

            UpdateTotalDifficulty(block);

            if (block.TotalDifficulty > (BestSuggestedBlock?.TotalDifficulty ?? 0))
            {
                BestSuggestedBlock = block;
                NewBestSuggestedBlock?.Invoke(this, new BlockEventArgs(block));
            }

            return AddBlockResult.Added;
        }

        public Block FindBlock(Keccak blockHash, bool mainChainOnly)
        {
            _mainChain.TryGetValue(blockHash, out Block block);
            if (block == null && !mainChainOnly)
            {
                _branches.TryGetValue(blockHash, out block);
            }

            return block;
        }

        public Block[] FindBlocks(Keccak blockHash, int numberOfBlocks, int skip, bool reverse)
        {
            Block[] result = new Block[numberOfBlocks];
            Block startBlock = FindBlock(blockHash, true);
            if (startBlock == null)
            {
                return result;
            }

            for (int i = 0; i < numberOfBlocks; i++)
            {
                _canonicalChain.TryGetValue((int)startBlock.Number + (reverse ? -1 : 1) * (skip + i), out Block ithBlock);
                result[i] = ithBlock;
            }

            return result;
        }

        public Block FindBlock(BigInteger blockNumber)
        {
            if (blockNumber.Sign < 0)
            {
                throw new ArgumentException($"{nameof(blockNumber)} must be greater or equal zero and is {blockNumber}", nameof(blockNumber));
            }

            _canonicalChain.TryGetValue((int)blockNumber, out Block block);
            return block;
        }

        public bool IsMainChain(Keccak blockHash)
        {
            return _mainChain.ContainsKey(blockHash);
        }

        public void MoveToBranch(Keccak blockHash)
        {
            Block block = _mainChain[blockHash];
            _canonicalChain[(int)block.Number] = null;

            _branches.AddOrUpdate(blockHash, block, (h, b) =>
            {
                Debug.Assert(block == b, "Assuming it would not happen, if never fired use indexer instead");
                return b;
            });

            if (!_mainChain.TryRemove(blockHash, out Block _))
            {
                throw new InvalidOperationException($"this should not happen as we should only be removing in {nameof(BlockchainProcessor)}");
            }
        }

        public void MarkAsProcessed(Keccak blockHash)
        {
            Block block = FindBlock(blockHash, false);
            Debug.Assert(block != null, "Expected block to be known when marking as processed");
            if (block == null)
            {
                throw new InvalidOperationException($"Unknown {nameof(Block)} cannot {nameof(MarkAsProcessed)}");
            }

            _processed[blockHash] = true;
        }

        public bool WasProcessed(Keccak blockHash)
        {
            return _processed.ContainsKey(blockHash);
        }

        public void MoveToMain(Keccak blockHash)
        {
            if (!WasProcessed(blockHash))
            {
                throw new InvalidOperationException("Cannot move unprocessed blocks to main");
            }

            Block block = _branches[blockHash];
            _canonicalChain[(int)block.Number] = block;

            _mainChain.AddOrUpdate(blockHash, block, (h, b) =>
            {
                Debug.Assert(block == b, "updating with a different block?");
                return b;
            });

            if (!_branches.TryRemove(blockHash, out Block _))
            {
                throw new InvalidOperationException($"this should not happen as we should only be removing in {nameof(BlockchainProcessor)}");
            }

            BlockAddedToMain?.Invoke(this, new BlockEventArgs(block));

            if (block.TotalDifficulty > (HeadBlock?.TotalDifficulty ?? 0))
            {
                if (block.Number == 0)
                {
                    GenesisBlock = block;
                }
                
                HeadBlock = block;
                NewHeadBlock?.Invoke(this, new BlockEventArgs(block));
            }
        }

        private void UpdateTotalDifficulty(Block block)
        {
            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"CALCULATING TOTAL DIFFICULTY FOR {block.Hash}");
            }

            if (block.Number == 0)
            {
                block.Header.TotalDifficulty = block.Difficulty;
            }
            else
            {
                Block parent = this.FindParent(block.Header);
                Debug.Assert(parent != null, "assuming this is never called if there has been no parent in the first place");
                if (parent == null)
                {
                    throw new InvalidOperationException($"An orphaned block on the chain {block.Hash} ({block.Number})");
                }

                Debug.Assert(parent.TotalDifficulty != null, "assuming this parent's difficulty is known");
                block.Header.TotalDifficulty = parent.TotalDifficulty + block.Difficulty;
            }

            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"CALCULATED TOTAL DIFFICULTY FOR {block.Hash} IS {block.TotalDifficulty}");
            }
        }
    }
}