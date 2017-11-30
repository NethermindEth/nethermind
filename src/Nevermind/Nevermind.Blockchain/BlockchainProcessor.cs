using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Nevermind.Blockchain.Validators;
using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Core.Encoding;

namespace Nevermind.Blockchain
{
    public class BlockchainProcessor : IBlockchainProcessor
    {
        private readonly IBlockStore _blockStore;
        private readonly IBlockProcessor _blockProcessor;
        private readonly ILogger _logger;

        public BlockchainProcessor(
            Rlp genesisBlockRlp,
            IBlockProcessor blockProcessor,
            IBlockStore blockStore,
            ILogger logger)
        {
            _blockStore = blockStore;
            _blockProcessor = blockProcessor;
            _logger = logger;

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

            // TODO: can I have orphans here?
            Block parent = _blockStore.FindParent(block.Header);
            Debug.Assert(parent != null, "testing transactions count of an orphaned block");

            return block.Transactions.Count + GetTotalTransactions(parent);
        }

        private BigInteger GetTotalDifficulty(BlockHeader blockHeader)
        {
            // TODO: vulnerability if genesis block is propagated with high initial difficulty?
            if (blockHeader.Number == 0)
            {
                return blockHeader.Difficulty;
            }

            // TODO: can I have orphans here?
            Block parent = _blockStore.FindParent(blockHeader);
            Debug.Assert(parent != null, "testing difficulty of an orphaned block");

            return blockHeader.Difficulty + GetTotalDifficulty(parent.Header);
        }

        public void Process(Rlp blockRlp)
        {
            try
            {
                _logger?.Log("-------------------------------------------------------------------------------------");
                Block suggestedBlock = Rlp.Decode<Block>(blockRlp);
                BigInteger totalDifficulty = GetTotalDifficulty(suggestedBlock.Header);
                BigInteger totalTransactions = GetTotalTransactions(suggestedBlock);
                _logger?.Log($"TOTAL DIFFICULTY OF BLOCK {suggestedBlock.Header.Hash} ({suggestedBlock.Header.Number}) IS {totalDifficulty}");
                _logger?.Log($"TOTAL TRANSACTIONS OF BLOCK {suggestedBlock.Header.Hash} ({suggestedBlock.Header.Number}) IS {totalTransactions}");

                if (totalDifficulty > TotalDifficulty)
//                    if (totalDifficulty > TotalDifficulty || totalDifficulty == TotalDifficulty && totalTransactions > TotalTransactions)
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
                    Keccak? stateRoot = branchingPoint?.Header.StateRoot;
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
                    Block[] processedBlocks = _blockProcessor.Process(stateRoot, blocks);

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
                else
                {
                    // lower difficulty branch
                    _blockStore.AddBlock(suggestedBlock, false);
                }
            }
            catch (InvalidBlockException ex)
            {
                throw;
            }
        }
    }
}