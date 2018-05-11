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
        private const int MaxQueueSize = 100_000;
        private readonly IDb _blockDb;

        private readonly BlockDecoder _blockDecoder = new BlockDecoder();
        private readonly IDb _blockInfoDb;
        private readonly ILogger _logger;
        private readonly IDb _receiptsDb;
        private readonly ISpecProvider _specProvider;

        // TODO: validators should be here
        public BlockTree(
            IDb blockDb,
            IDb blockInfoDb,
            IDb receiptsDb,
            ISpecProvider specProvider,
            ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _blockDb = blockDb;
            _blockInfoDb = blockInfoDb;
            _receiptsDb = receiptsDb;
            _specProvider = specProvider;

            ChainLevelInfo genesisLevel = LoadLevel(0);
            if (genesisLevel != null)
            {
                if (genesisLevel.BlockInfos.Length != 1)
                {
                    // TODO: corrupted state exception?
                    throw new InvalidOperationException($"Genesis level in DB has {genesisLevel.BlockInfos.Length} blocks");
                }

                Block genesisBlock = Load(genesisLevel.BlockInfos[0].BlockHash).Block;
                Genesis = genesisBlock.Header;
                LoadHeadBlock();
            }
        }

        public void LoadBlocksFromDb(BigInteger? startBlockNumber = null)
        {
            if (startBlockNumber == null)
            {
                startBlockNumber = Head.Number;
            }
            else
            {
                Head = startBlockNumber == 0 ? null : FindBlock(startBlockNumber.Value - 1)?.Header;
            }

            if (_logger.IsInfoEnabled)
            {
                _logger.Info($"Loading blocks from DB (starting from {startBlockNumber}).");
            }

            BigInteger blocksToLoad = FindNumberOfBlocksToLoadFromDb();
            if (_logger.IsInfoEnabled)
            {
                _logger.Info($"Found {blocksToLoad} blocks to load starting from current head block {Head?.ToString(BlockHeader.Format.Short)}.");
            }

            for (int i = 0; i < blocksToLoad; i++)
            {
                Block block = FindBlock(startBlockNumber.Value + i + 1);
                BestSuggested = block.Header;
                NewBestSuggestedBlock?.Invoke(this, new BlockEventArgs(block));

                if (i % 10000 == 9999 && _logger.IsInfoEnabled)
                {
                    _logger.Info($"Loaded {i + 1} blocks");
                }
            }

            if (_logger.IsInfoEnabled)
            {
                _logger.Info("Finished loading blocks from DB. Current best suggested block");
            }
        }

        public event EventHandler<BlockEventArgs> BlockAddedToMain;

        public event EventHandler<BlockEventArgs> NewBestSuggestedBlock;

        public event EventHandler<BlockEventArgs> NewHeadBlock;

        public BlockHeader Genesis { get; private set; }
        public BlockHeader Head { get; private set; }
        public BlockHeader BestSuggested { get; private set; }
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
                if (BestSuggested != null)
                {
                    throw new InvalidOperationException("Genesis block should be added only once"); // TODO: make sure it cannot happen
                }
            }
            else if (IsKnownBlock(block.Hash))
            {
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"Block {block.Hash} already known.");
                }

                return AddBlockResult.AlreadyKnown;
            }
            else if (!IsKnownBlock(block.Header.ParentHash))
            {
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"Could not find parent ({block.Header.ParentHash}) of block {block.Hash}");
                }

                return AddBlockResult.UnknownParent;
            }

            SetTotalDifficulty(block);
            SetTotalTransactions(block);

            _blockDb.Set(block.Hash, Rlp.Encode(block).Bytes);

            BlockInfo blockInfo = new BlockInfo(block.Hash, block.TotalDifficulty.Value, block.TotalTransactions.Value);
            UpdateLevel(block.Number, blockInfo);

            if (block.TotalDifficulty > (BestSuggested?.TotalDifficulty ?? 0))
            {
                BestSuggested = block.Header;
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
            BigInteger number = LoadNumberOnly(blockHash);
            ChainLevelInfo level = LoadLevel(number);
            level.HasBlockOnMainChain = false;
            UpdateLevel(number, level);
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
            BigInteger number = LoadNumberOnly(blockHash);
            ChainLevelInfo levelInfo = LoadLevel(number);
            int? index = FindIndex(blockHash, levelInfo);
            if (index == null)
            {
                throw new InvalidOperationException($"Not able to find block {blockHash} index on the chain level");
            }

            return levelInfo.BlockInfos[index.Value].WasProcessed;
        }

        public void MoveToMain(Block block)
        {
            ChainLevelInfo level = LoadLevel(block.Number);
            MoveToMain(level, block);
        }

        public void MoveToMain(Keccak blockHash) // TODO: still needed?
        {
            (Block block, BlockInfo _, ChainLevelInfo level) = Load(blockHash);
            MoveToMain(level, block);
        }

        private void MoveToMain(ChainLevelInfo level, Block block)
        {
            int? index = FindIndex(block.Hash, level);
            if (index.Value != 0)
            {
                (level.BlockInfos[index.Value], level.BlockInfos[0]) = (level.BlockInfos[0], level.BlockInfos[index.Value]);
            }

            BlockInfo info = level.BlockInfos[index.Value];
            if (!info.WasProcessed)
            {
                throw new InvalidOperationException("Cannot move unprocessed blocks to main");
            }

            // TODO: in testing chains we have a chain full of processed blocks that we process again
            //if (level.HasBlockOnMainChain)
            //{
            //    throw new InvalidOperationException("When moving to main encountered a block in main on the same level");
            //}

            level.HasBlockOnMainChain = true;
            UpdateLevel(block.Number, level);

            BlockAddedToMain?.Invoke(this, new BlockEventArgs(block));

            if (block.TotalDifficulty > (Head?.TotalDifficulty ?? 0))
            {
                if (block.Number == 0)
                {
                    Genesis = block.Header;
                }

                Head = block.Header;
                SaveHeadBlock(block);
                NewHeadBlock?.Invoke(this, new BlockEventArgs(block));
            }
        }

        // TODO: this is all temp while we test full chain sync
        private BigInteger FindNumberOfBlocksToLoadFromDb()
        {
            BigInteger headNumber = Head?.Number ?? -1;
            BigInteger left = headNumber;
            BigInteger right = headNumber + MaxQueueSize;

            while (left != right)
            {
                BigInteger index = left + (right - left) / 2;
                ChainLevelInfo level = LoadLevel(index);
                if (level == null)
                {
                    right = index;
                }
                else
                {
                    left = index + 1;
                }
            }

            return left - headNumber - 1;
        }

        private void LoadHeadBlock()
        {
            byte[] data = _blockDb.Get(Keccak.Zero);
            if (data != null)
            {
                Block block = _blockDecoder.Decode(new Rlp(data), true);
                Head = BestSuggested = block.Header;
            }
        }

        public bool IsKnownBlock(Keccak blockHash)
        {
            byte[] data = _blockDb.Get(blockHash);
            return data != null;
        }

        private void SaveHeadBlock(Block block)
        {
            _blockDb.Set(Keccak.Zero, Rlp.Encode(block).Bytes);
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
                level.BlockInfos = blockInfos;
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
            int? index = FindIndex(blockHash, level);
            return index.HasValue ? (level.BlockInfos[index.Value], level) : (null, level);
        }

        private int? FindIndex(Keccak blockHash, ChainLevelInfo level)
        {
            for (int i = 0; i < level.BlockInfos.Length; i++)
            {
                if (level.BlockInfos[i].BlockHash.Equals(blockHash))
                {
                    return i;
                }
            }

            return null;
        }

        private ChainLevelInfo LoadLevel(BigInteger number)
        {
            byte[] data = _blockInfoDb.Get(number);
            return data == null ? null : Rlp.Decode<ChainLevelInfo>(new Rlp(data));
        }

        // TODO: use headers store or some simplified RLP decoder for number only or hash to number store
        private BigInteger LoadNumberOnly(Keccak blockHash)
        {
            Block block = _blockDecoder.Decode(new Rlp(_blockDb.Get(blockHash)), true);
            if (block == null)
            {
                throw new InvalidOperationException($"Not able to retrieve block number for an unknown block {blockHash}");
            }

            return block.Number;
        }

        public BlockHeader FindHeader(Keccak blockHash)
        {
            byte[] data = _blockDb.Get(blockHash);
            if (data == null)
            {
                return null;
            }

            BlockHeader header = _blockDecoder.Decode(new Rlp(data), true).Header;
            BlockInfo blockInfo = LoadInfo(header.Number, header.Hash).Info;
            header.TotalTransactions = blockInfo.TotalTransactions;
            header.TotalDifficulty = blockInfo.TotalDifficulty;

            return header;
        }

        private (Block Block, BlockInfo Info, ChainLevelInfo Level) Load(Keccak blockHash)
        {
            byte[] data = _blockDb.Get(blockHash);
            if (data == null)
            {
                return (null, null, null);
            }

            Block block = _blockDecoder.Decode(new Rlp(data));
            (BlockInfo blockInfo, ChainLevelInfo level) = LoadInfo(block.Number, block.Hash);

            if (blockInfo == null)
            {
                throw new InvalidOperationException($"{nameof(blockInfo)} is null when {nameof(block)} is not null");
            }

            block.Header.TotalDifficulty = blockInfo.TotalDifficulty;
            block.Header.TotalTransactions = blockInfo.TotalTransactions;

            byte[] receiptsData = _receiptsDb.Get(block.Hash);
            if (receiptsData != null)
            {
                TransactionReceipt[] receipts = Rlp.DecodeArray<TransactionReceipt>(new Rlp(receiptsData));
                block.Receipts = receipts;
            }
            else
            {
                block.Receipts = new TransactionReceipt[0];
            }

            return (block, blockInfo, level);
        }

        private void SetTotalDifficulty(Block block)
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

        private void SetTotalTransactions(Block block)
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