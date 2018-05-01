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
using System.Linq;
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
        private readonly IDb _blockDb;
        private readonly IDb _blockInfoDb;
        private readonly ILogger _logger;
        private readonly IDb _receiptsDb;
        private readonly ISpecProvider _specProvider;

        // TODO: validators should be here
        public BlockTree(IDb blockDb, IDb blockInfoDb, IDb receiptsDb, ISpecProvider specProvider, ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _blockDb = blockDb;
            _blockInfoDb = blockInfoDb;
            _receiptsDb = receiptsDb;
            _specProvider = specProvider;
        }

        public event EventHandler<BlockEventArgs> BlockAddedToMain;

        public event EventHandler<BlockEventArgs> NewBestSuggestedBlock;

        public event EventHandler<BlockEventArgs> NewHeadBlock;

        public Block GenesisBlock { get; private set; }
        public Block HeadBlock { get; private set; }
        public Block BestSuggestedBlock { get; private set; }
        public int ChainId => _specProvider.ChainId;

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
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"Block {block.Hash} already known.");
                }

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

            _blockDb.Set(block.Hash, Rlp.Encode(block).Bytes);
            BlockInfo blockInfo = new BlockInfo(block.Hash, block.TotalDifficulty.Value, block.TotalTransactions.Value);
            UpdateLevel(block.Number, blockInfo);

            if (block.TotalDifficulty > (BestSuggestedBlock?.TotalDifficulty ?? 0))
            {
                BestSuggestedBlock = block;
                NewBestSuggestedBlock?.Invoke(this, new BlockEventArgs(block));
            }

            return AddBlockResult.Added;
        }

        public Block FindBlock(Keccak blockHash, bool mainChainOnly)
        {
            (Block block, BlockInfo _, ChainLevelInfo level) = Load(blockHash);
            if (block == null)
            {
                return null;
            }

            if (mainChainOnly)
            {
                // TODO: double hash comparison
                bool isMain = level.HasBlockOnMainChain && level.BlockInfos[0].BlockHash.Equals(blockHash);
                return isMain ? block : null;
            }

            return block;
        }

        // TODO: since finding by hash will be faster it will be worth to refactor this part
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


            ChainLevelInfo level = LoadLevel(blockNumber);
            if (level == null)
            {
                return null;
            }

            return Load(level.BlockInfos[0].BlockHash).Block;
        }

        public bool IsMainChain(Keccak blockHash)
        {
            BigInteger number = LoadNumberOnly(blockHash);
            ChainLevelInfo level = LoadLevel(number);
            return level.HasBlockOnMainChain && level.BlockInfos[0].BlockHash.Equals(blockHash);
        }

        public void MoveToBranch(Keccak blockHash)
        {
            (Block _, BlockInfo _, ChainLevelInfo level) = Load(blockHash);
            level.HasBlockOnMainChain = false;
        }

        public void MarkAsProcessed(Keccak blockHash, TransactionReceipt[] receipts = null)
        {
            BigInteger number = LoadNumberOnly(blockHash);
            (BlockInfo info, ChainLevelInfo level) = LoadInfo(number, blockHash);

            if (info.WasProcessed)
            {
                throw new InvalidOperationException($"Marking already processed block {blockHash} as processed");
            }

            info.WasProcessed = true;
            UpdateLevel(number, level);
            if (receipts != null)
            {
                IReleaseSpec spec = _specProvider.GetSpec(number);
                _receiptsDb.Set(blockHash, Rlp.Encode(receipts.Select(r => Rlp.Encode(r, spec.IsEip658Enabled)).ToArray()).Bytes);
            }
        }

        public bool WasProcessed(Keccak blockHash)
        {
            return Load(blockHash).Info.WasProcessed;
        }

        public void MoveToMain(Keccak blockHash)
        {
            if (!WasProcessed(blockHash))
            {
                throw new InvalidOperationException("Cannot move unprocessed blocks to main");
            }

            (Block block, BlockInfo _, ChainLevelInfo level) = Load(blockHash);

            int index = FindIndex(blockHash, level);
            if (index != 0)
            {
                (level.BlockInfos[index], level.BlockInfos[0]) = (level.BlockInfos[0], level.BlockInfos[index]);
            }

            level.HasBlockOnMainChain = true;
            UpdateLevel(block.Number, level);

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

        private void UpdateLevel(BigInteger number, BlockInfo blockInfo)
        {
            ChainLevelInfo level = LoadLevel(number);
            if (level != null)
            {
                BlockInfo[] blockInfos = new BlockInfo[level.BlockInfos.Length + 1];
                for (int i = 0; i < level.BlockInfos.Length; i++)
                {
                    blockInfos[i] = level.BlockInfos[i];
                }

                blockInfos[blockInfos.Length - 1] = blockInfo;
            }
            else
            {
                level = new ChainLevelInfo(false, new[] {blockInfo});
            }
            
            _blockInfoDb.Set(number, Rlp.Encode(level).Bytes);
        }

        private void UpdateLevel(BigInteger number, ChainLevelInfo level)
        {
            _blockInfoDb.Set(number, Rlp.Encode(level).Bytes);
        }

        private (BlockInfo Info, ChainLevelInfo Level) LoadInfo(BigInteger number, Keccak blockHash)
        {
            ChainLevelInfo level = Rlp.Decode<ChainLevelInfo>(new Rlp(_blockInfoDb.Get(number)));
            for (int i = 0; i < level.BlockInfos.Length; i++)
            {
                if (level.BlockInfos[i].BlockHash.Equals(blockHash))
                {
                    return (level.BlockInfos[i], level);
                }
            }

            return (null, null);
        }

        private ChainLevelInfo LoadLevel(BigInteger number)
        {
            if (!_blockInfoDb.ContainsKey(number))
            {
                return null;
            }
            
            return Rlp.Decode<ChainLevelInfo>(new Rlp(_blockInfoDb.Get(number)));
        }

        // TODO: use headers store?
        private BigInteger LoadNumberOnly(Keccak blockHash)
        {
            Block block = Rlp.Decode<Block>(new Rlp(_blockDb.Get(blockHash)));
            if (block == null)
            {
                throw new InvalidOperationException($"Not able to retrieve block number for an unknown block {blockHash}");
            }

            return block.Number;
        }

        private (Block Block, BlockInfo Info, ChainLevelInfo Level) Load(Keccak blockHash)
        {
            if (!_blockDb.ContainsKey(blockHash))
            {
                return (null, null, null);
            }

            Block block = Rlp.Decode<Block>(new Rlp(_blockDb.Get(blockHash)));
            (BlockInfo blockInfo, ChainLevelInfo level) = LoadInfo(block.Number, block.Hash);

            block.Header.TotalDifficulty = blockInfo.TotalDifficulty;
            block.Header.TotalTransactions = blockInfo.TotalTransactions;
            if (_receiptsDb.ContainsKey(block.Hash))
            {
                TransactionReceipt[] receipts = Rlp.DecodeArray<TransactionReceipt>(new Rlp(_receiptsDb.Get(block.Hash)));
                block.Receipts = receipts;
            }
            else
            {
                block.Receipts = new TransactionReceipt[0];
            }

            return (block, blockInfo, level);
        }

        private int FindIndex(Keccak blockHash, ChainLevelInfo level)
        {
            for (int i = 0; i < level.BlockInfos.Length; i++)
            {
                if (level.BlockInfos[i].BlockHash.Equals(blockHash))
                {
                    return i;
                }
            }

            throw new InvalidOperationException($"Not able to find block {blockHash} index on the chain level");
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