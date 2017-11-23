using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Nevermind.Blockchain.Validators;
using Nevermind.Core;
using Nevermind.Core.Crypto;

namespace Nevermind.Blockchain
{
    public class BlockchainProcessor : IBlockchainProcessor
    {
        private readonly ILogger _logger;
        private readonly IBlockProcessor _blockProcessor;
        private readonly IBlockchainStore _blockchainStore;

        public BlockchainProcessor(
            Block genesisBlock,
            IBlockProcessor blockProcessor,
            IBlockchainStore blockchainStore,
            BlockValidator blockValidator,
            ILogger logger)
        {
            bool isValid = blockValidator.IsValid(genesisBlock);
            if (!isValid)
            {
                throw new ArgumentException("Genesis block must be valid", nameof(genesisBlock));
            }

            _blockchainStore = blockchainStore;
            _blockProcessor = blockProcessor;
            _logger = logger;

            _blockchainStore.AddBlock(genesisBlock);
            HeadBlock = genesisBlock;
        }

        public Block HeadBlock { get; private set; }
        public BigInteger TotalDifficulty { get; private set; } // TODO: chain selection / sidechains

        public void ProcessBlocks(List<Block> blocks)
        {
            // TODO: select by total difficulty
            // TODO: validate block
            // TODO: how to undo changes if forked?

            Dictionary<Keccak, Block> blocksByHash = blocks.ToDictionary(b => b.Header.Hash, b => b);
            foreach (Block block in blocks.OrderBy(b => b.Header.Number))
            {
                blocksByHash.TryGetValue(block.Header.ParentHash, out Block parent);
                if (parent == null)
                {
                    parent = _blockchainStore.FindBlock(block.Header.ParentHash);
                    if (parent == null)
                    {
                        _logger?.Log($"DISCARDING BLOCK {block.Header.Hash} (child of {block.Header.ParentHash}) {block.Header.Number}");
                    }
                }

                HeadBlock = _blockProcessor.ProcessBlock(
                    parent,
                    block.Header.Timestamp,
                    block.Header.Beneficiary,
                    block.Header.GasLimit,
                    block.Header.ExtraData,
                    block.Transactions,
                    block.Header.MixHash,
                    block.Header.Nonce,
                    block.Ommers);
                TotalDifficulty += HeadBlock.Header.Difficulty;
            }
        }
    }
}