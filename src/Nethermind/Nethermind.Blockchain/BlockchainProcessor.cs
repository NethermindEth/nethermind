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

using System.Collections.Generic;
using System.Numerics;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain
{
    public class BlockchainProcessor : IBlockchainProcessor
    {
        private readonly IBlockStore _blockStore;
        private readonly IBlockProcessor _blockProcessor;
        private readonly ILogger _logger;

        public BlockchainProcessor(
            IBlockProcessor blockProcessor,
            IBlockStore blockStore,
            ILogger logger)
        {
            _blockStore = blockStore;
            _blockProcessor = blockProcessor;
            _logger = logger;
        }

        public void Initialize(Block genesisBlockRlp)
        {
            Process(genesisBlockRlp);
        }

        public Block HeadBlock { get; private set; }
        public BigInteger TotalDifficulty { get; private set; }
        public BigInteger TotalTransactions { get; private set; }

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

        public Block Try(Block suggestedBlock)
        {
            return Process(suggestedBlock, true);
        }

        public Block Process(Block suggestedBlock, bool tryOnly)
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
                Block[] processedBlocks = _blockProcessor.Process(stateRoot, blocks, tryOnly);

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

                if (!tryOnly)
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
                }

                return processedBlocks[processedBlocks.Length - 1];
            }

            if (!tryOnly)
            {
                // lower difficulty branch
                _blockStore.AddBlock(suggestedBlock, false);
            }

            return null;
        }

        public void Process(Block suggestedBlock)
        {
            Process(suggestedBlock, false);
        }
    }
}