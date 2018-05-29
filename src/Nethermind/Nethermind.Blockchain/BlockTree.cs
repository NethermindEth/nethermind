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
using System.Threading.Tasks;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Store;

namespace Nethermind.Blockchain
{
    public class BlockTree : IBlockTree
    {
        // TODO: automatically wrap DBs with caches
        private readonly LruCache<Keccak, Block> _blockCache = new LruCache<Keccak, Block>(64);
        private readonly LruCache<BigInteger, ChainLevelInfo> _blockInfoCache = new LruCache<BigInteger, ChainLevelInfo>(64);
        private readonly LruCache<Keccak, TransactionReceipt[]> _receiptsCache = new LruCache<Keccak, TransactionReceipt[]>(64);

        private const int MaxQueueSize = 1_000_000;

        public const int DbLoadBatchSize = 1000;

        private BigInteger _currentDbLoadBatchEnd;

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

        public async Task LoadBlocksFromDb(BigInteger? startBlockNumber = null, int batchSize = DbLoadBatchSize, int maxBlocksToLoad = int.MaxValue)
        {
            if (startBlockNumber == null)
            {
                startBlockNumber = Head?.Number ?? -1;
            }
            else
            {
                Head = startBlockNumber == 0 ? null : FindBlock(startBlockNumber.Value - 1)?.Header;
            }

            if (_logger.IsInfoEnabled)
            {
                _logger.Info($"Loading blocks from DB (starting from {startBlockNumber}).");
            }

            BigInteger blocksToLoad = BigInteger.Min(FindNumberOfBlocksToLoadFromDb(), maxBlocksToLoad);
            if (_logger.IsInfoEnabled)
            {
                _logger.Info($"Found {blocksToLoad} blocks to load starting from current head block {Head?.ToString(BlockHeader.Format.Short)}.");
            }

            for (int i = 0; i < blocksToLoad; i++)
            {
                BigInteger blockNumber = startBlockNumber.Value + i + 1;
                Block block = FindBlock(blockNumber);
                BestSuggested = block.Header;
                NewBestSuggestedBlock?.Invoke(this, new BlockEventArgs(block));

                if (i % batchSize == batchSize - 1)
                {
                    if (_logger.IsInfoEnabled)
                    {
                        _logger.Info($"Loaded {i + 1} blocks, waiting for processor.");
                    }

                    _dbBatchProcessed = new TaskCompletionSource<object>();
                    _currentDbLoadBatchEnd = blockNumber;
                    await _dbBatchProcessed.Task;
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

            _blockDb.Set(block.Hash, Rlp.Encode(block).Bytes);
            _blockCache.Set(block.Hash, block);

            // TODO: when reviewing the entire data chain need to look at the transactional storing of level and block
            SetTotalDifficulty(block);
            SetTotalTransactions(block);
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

        private Keccak GetBlockHashOnMain(BigInteger blockNumber)
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

            if (level.HasBlockOnMainChain)
            {
                return level.BlockInfos[0].BlockHash;
            }
            else
            {
                if (level.BlockInfos.Length > 0)
                {
                    throw new InvalidOperationException("Unexpected request by number for a block that is not on the main chain");
                }
            }

            return null;
        }

        public Block FindBlock(BigInteger blockNumber)
        {
            Keccak hash = GetBlockHashOnMain(blockNumber);
            return Load(hash).Block;
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
                _receiptsCache.Set(blockHash, receipts);
                _receiptsDb.Set(blockHash, Rlp.Encode(receipts.Select(r => Rlp.Encode(r, spec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None)).ToArray()).Bytes);
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

        private TaskCompletionSource<object> _dbBatchProcessed;

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

                UpdateHeadBlock(block);
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
                Block block = _blockDecoder.Decode(data.AsRlpContext(), RlpBehaviors.AllowExtraData);
                Head = BestSuggested = block.Header;
            }
        }

        public bool IsKnownBlock(Keccak blockHash)
        {
            if (_blockCache.Get(blockHash) != null)
            {
                return true;
            }

            byte[] data = _blockDb.Get(blockHash);
            return data != null;
        }

        private void UpdateHeadBlock(Block block)
        {
            Head = block.Header;
            _blockDb.Set(Keccak.Zero, Rlp.Encode(block).Bytes);
            NewHeadBlock?.Invoke(this, new BlockEventArgs(block));
            if (_dbBatchProcessed != null)
            {
                if (block.Number == _currentDbLoadBatchEnd)
                {
                    TaskCompletionSource<object> completionSoruce = _dbBatchProcessed;
                    _dbBatchProcessed = null;
                    completionSoruce.SetResult(null);
                }
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
                level.BlockInfos = blockInfos;
            }
            else
            {
                level = new ChainLevelInfo(false, new[] { blockInfo });
            }

            UpdateLevel(number, level);
        }

        private void UpdateLevel(BigInteger number, ChainLevelInfo level)
        {
            _blockInfoCache.Set(number, level);
            _blockInfoDb.Set(number, Rlp.Encode(level).Bytes);
        }

        private (BlockInfo Info, ChainLevelInfo Level) LoadInfo(BigInteger number, Keccak blockHash)
        {
            ChainLevelInfo chainLevelInfo = LoadLevel(number);
            if (chainLevelInfo == null)
            {
                return (null, null);
            }

            int? index = FindIndex(blockHash, chainLevelInfo);
            return index.HasValue ? (chainLevelInfo.BlockInfos[index.Value], chainLevelInfo) : (null, chainLevelInfo);
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
            ChainLevelInfo chainLevelInfo = _blockInfoCache.Get(number);
            if (chainLevelInfo == null)
            {
                byte[] levelBytes = _blockInfoDb.Get(number);
                if (levelBytes == null)
                {
                    return null;
                }

                chainLevelInfo = Rlp.Decode<ChainLevelInfo>(new Rlp(levelBytes));
            }

            return chainLevelInfo;
        }

        // TODO: use headers store or some simplified RLP decoder for number only or hash to number store
        private BigInteger LoadNumberOnly(Keccak blockHash)
        {
            Block block = _blockCache.Get(blockHash);
            if (block != null)
            {
                return block.Number;
            }

            block = _blockDecoder.Decode(_blockDb.Get(blockHash).AsRlpContext(), RlpBehaviors.AllowExtraData);
            if (block == null)
            {
                throw new InvalidOperationException($"Not able to retrieve block number for an unknown block {blockHash}");
            }

            return block.Number;
        }

        public BlockHeader FindHeader(Keccak blockHash)
        {
            Block block = _blockCache.Get(blockHash);
            if (block == null)
            {
                byte[] data = _blockDb.Get(blockHash);
                if (data == null)
                {
                    return null;
                }

                block = _blockDecoder.Decode(data.AsRlpContext(), RlpBehaviors.AllowExtraData);
            }

            BlockHeader header = block.Header;
            BlockInfo blockInfo = LoadInfo(header.Number, header.Hash).Info;
            header.TotalTransactions = blockInfo.TotalTransactions;
            header.TotalDifficulty = blockInfo.TotalDifficulty;

            return header;
        }

        public BlockHeader FindHeader(BigInteger number)
        {
            Keccak hash = GetBlockHashOnMain(number);
            if (hash == null)
            {
                return null;
            }

            return FindHeader(hash);
        }

        private (Block Block, BlockInfo Info, ChainLevelInfo Level) Load(Keccak blockHash)
        {
            Block block = _blockCache.Get(blockHash);
            if (block == null)
            {
                byte[] data = _blockDb.Get(blockHash);
                if (data == null)
                {
                    return (null, null, null);
                }

                block = _blockDecoder.Decode(data.AsRlpContext(), RlpBehaviors.AllowExtraData);
                _blockCache.Set(blockHash, block);
            }

            (BlockInfo blockInfo, ChainLevelInfo level) = LoadInfo(block.Number, block.Hash);
            if (level == null || blockInfo == null)
            {
                // TODO: this is here because storing block data is not transactional
                SetTotalDifficulty(block);
                SetTotalTransactions(block);
                blockInfo = new BlockInfo(block.Hash, block.TotalDifficulty.Value, block.TotalTransactions.Value);
                UpdateLevel(block.Number, blockInfo);
                (blockInfo, level) = LoadInfo(block.Number, block.Hash);
            }
            else
            {
                block.Header.TotalDifficulty = blockInfo.TotalDifficulty;
                block.Header.TotalTransactions = blockInfo.TotalTransactions;
            }

            TransactionReceipt[] receipts = _receiptsCache.Get(block.Hash);
            if (receipts == null)
            {
                byte[] receiptsData = _receiptsDb.Get(block.Hash);
                receipts = receiptsData == null ? null : Rlp.DecodeArray<TransactionReceipt>(receiptsData.AsRlpContext());
            }

            block.Receipts = receipts ?? new TransactionReceipt[0];
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