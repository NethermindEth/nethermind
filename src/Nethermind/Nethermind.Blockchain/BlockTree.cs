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
using System.Numerics;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Specs;
using Nethermind.Store;

namespace Nethermind.Blockchain
{
    public class BlockTree : IBlockTree
    {
        private const bool IgnoredValue = false;
        private readonly ConcurrentDictionary<Keccak, bool> _branches = new ConcurrentDictionary<Keccak, bool>(); // TODO: minor, would byte be better than bool?
        private readonly ConcurrentDictionary<BigInteger, Keccak> _canonicalChain = new ConcurrentDictionary<BigInteger, Keccak>();
        private readonly IDb _blockDb; // TODO: separate bodies and headers
        private readonly IDb _blockInfoDb; // TODO: separate bodies and headers
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<Keccak, bool> _mainChain = new ConcurrentDictionary<Keccak, bool>();
        private readonly ConcurrentDictionary<Keccak, bool> _processed = new ConcurrentDictionary<Keccak, bool>();

        // TODO: validators should be here
        public BlockTree(IDb blockDb, IDb blockInfoDb, ISpecProvider specProvider, ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _blockDb = blockDb;
            _blockInfoDb = blockInfoDb;
            ChainId = specProvider.ChainId;
        }

        public event EventHandler<BlockEventArgs> BlockAddedToMain;

        public event EventHandler<BlockEventArgs> NewBestSuggestedBlock;

        public event EventHandler<BlockEventArgs> NewHeadBlock;

        public Block GenesisBlock { get; private set; }
        public Block HeadBlock { get; private set; }
        public Block BestSuggestedBlock { get; private set; }
        public int ChainId { get; }

        public AddBlockResult SuggestBlock(Block block)
        {
            // TODO: review where the ChainId should be set
            foreach (Transaction transaction in block.Transactions)
            {
                transaction.ChainId = ChainId;
            }

            if (block.Number == 0)
            {
                if (BestSuggestedBlock != null)
                {
                    throw new InvalidOperationException("Genesis block should be added only once"); // TODO: make sure it cannot happen
                }
            }
            else if (FindBlock(block.Hash, false) != null)
            {
                return AddBlockResult.AlreadyKnown;
            }
            else if (this.FindParent(block.Header) == null)
            {
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"Could not find parent ({block.Header.ParentHash}) of block {block.Hash}");
                }

                return AddBlockResult.UnknownParent;
            }

            UpdateTotalDifficulty(block);
            UpdateTotalTransactions(block);
            
            _blockDb[block.Hash] = Rlp.Encode(block).Bytes;
            _blockInfoDb[block.Hash] = Rlp.Encode(new BlockInfo(block.TotalDifficulty.Value, block.TotalTransactions.Value)).Bytes;

            bool added = _branches.TryAdd(block.Header.Hash, IgnoredValue);
            if (added)
            {
                if (block.TotalDifficulty > (BestSuggestedBlock?.TotalDifficulty ?? 0))
                {
                    BestSuggestedBlock = block;
                    NewBestSuggestedBlock?.Invoke(this, new BlockEventArgs(block));
                }

                return AddBlockResult.Added;
            }

            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"Block {block.Hash} already known.");
            }

            return AddBlockResult.AlreadyKnown;
        }

        public Block FindBlock(Keccak blockHash, bool mainChainOnly)
        {
            bool isOnMainChain = _mainChain.ContainsKey(blockHash);
            if (!isOnMainChain && mainChainOnly)
            {
                return null;
            }
            
            bool isOnbranch = _branches.ContainsKey(blockHash);
            if (!isOnMainChain && !isOnbranch)
            {
                return null;
            }
            
            return Load(blockHash);
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
                int blockNumber = (int)startBlock.Number + (reverse ? -1 : 1) * (i + i * skip);
                Block ithBlock = FindBlock(blockNumber);
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
            
            _canonicalChain.TryGetValue((int)blockNumber, out Keccak blockHash);

            if (blockHash == null)
            {
                return null;
            }

            return Load(blockHash);
        }

        private Block Load(Keccak blockHash)
        {
            Block block = Rlp.Decode<Block>(new Rlp(_blockDb[blockHash]));
            BlockInfo blockInfo =Rlp.Decode<BlockInfo>(new Rlp(_blockInfoDb[blockHash]));
            block.Header.TotalDifficulty = blockInfo.TotalDifficulty;
            block.Header.TotalTransactions = blockInfo.TotalTransactions;
            return block;
        }

        public bool IsMainChain(Keccak blockHash)
        {
            return _mainChain.ContainsKey(blockHash);
        }

        public void MoveToBranch(Keccak blockHash)
        {
            Block block = FindBlock(blockHash, true);
            _canonicalChain[(int)block.Number] = null;

            _branches.AddOrUpdate(blockHash, IgnoredValue, (h, b) => throw new InvalidOperationException("Block being moved to branch is already there"));

            if (!_mainChain.TryRemove(blockHash, out bool _))
            {
                throw new InvalidOperationException("Not able to move block from the main chain to branch"); // should never happen as we only invoke it in the BlockchainProcessor
            }
        }

        public void MarkAsProcessed(Keccak blockHash)
        {
            Block block = FindBlock(blockHash, false);
            if (block == null)
            {
                throw new InvalidOperationException("Block being marked as processed is unknown.");
            }

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

            Block block = FindBlock(blockHash, false);
            _canonicalChain[(int)block.Number] = blockHash;

            _mainChain.AddOrUpdate(blockHash, IgnoredValue, (h, b) => throw new InvalidOperationException("Block being moved to main is already there"));

            if (!_branches.TryRemove(blockHash, out bool _))
            {
                throw new InvalidOperationException("Not able to move block from branch to the main chain"); // should never happen as we only invoke it in the BlockchainProcessor
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
                if (parent == null)
                {
                    throw new InvalidOperationException($"An orphaned block on the chain {block.Hash} ({block.Number})");
                }

                if (parent.TotalDifficulty == null)
                {
                    throw new InvalidOperationException($"Parent's {nameof(parent.TotalDifficulty)} unknown when calculating for {block.Hash} ({block.Number})");
                }

                block.Header.TotalDifficulty = parent.TotalDifficulty + block.Difficulty;
            }

            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"CALCULATED TOTAL DIFFICULTY FOR {block.Hash} IS {block.TotalDifficulty}");
            }
        }

        // TODO: move it, merge it with diff calc (copy pasted for now)
        private void UpdateTotalTransactions(Block block)
        {
            if (block.Number == 0)
            {
                block.Header.TotalTransactions = block.Transactions.Length;
            }
            else
            {
                Block parent = this.FindParent(block.Header);
                if (parent == null)
                {
                    throw new InvalidOperationException($"An orphaned block on the chain {block.Hash} ({block.Number})");
                }

                if (parent.TotalTransactions == null)
                {
                    throw new InvalidOperationException($"Parent's {nameof(parent.TotalTransactions)} unknown when calculating for {block.Hash} ({block.Number})");
                }

                block.Header.TotalTransactions = parent.TotalTransactions + block.Transactions.Length;
            }
        }
    }
}