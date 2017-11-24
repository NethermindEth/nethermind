using System;
using System.Numerics;
using Nevermind.Blockchain.Validators;
using Nevermind.Core;
using Nevermind.Core.Encoding;

namespace Nevermind.Blockchain
{
    public class BlockchainProcessor : IBlockchainProcessor
    {
        private readonly IBlockchainStore _blockchainStore;
        private readonly IBlockProcessor _blockProcessor;
        private readonly BlockValidator _blockValidator;
        private readonly ILogger _logger;

        public BlockchainProcessor(
            Block genesisBlock,
            IBlockProcessor blockProcessor,
            IBlockchainStore blockchainStore,
            BlockValidator blockValidator,
            ILogger logger)
        {
            _blockchainStore = blockchainStore;
            _blockValidator = blockValidator;
            _blockProcessor = blockProcessor;
            _logger = logger;

            bool isValid = _blockValidator.IsValid(genesisBlock);
            if (!isValid)
            {
                throw new ArgumentException("Genesis block must be valid", nameof(genesisBlock));
            }

            HeadBlock = genesisBlock;
            _blockchainStore.AddBlock(HeadBlock);
        }

        public Block HeadBlock { get; private set; }
        public BigInteger TotalDifficulty { get; private set; } // TODO: chain selection / sidechains

        private Block Process(Block block)
        {
            // TODO: select by total difficulty
            // TODO: validate block
            // TODO: how to undo changes if forked?

            Block parent = _blockchainStore.FindBlock(block.Header.ParentHash);
            if (parent == null)
            {
                _logger?.Log($"DISCARDING BLOCK {block.Header.Hash} (child of {block.Header.ParentHash}) {block.Header.Number}");
            }

            Block processedBlock = _blockProcessor.ProcessBlock(
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

            // TODO: validate everything else against the derlped block

            HeadBlock = processedBlock;
            _blockchainStore.AddBlock(HeadBlock);
            foreach (BlockHeader ommer in processedBlock.Ommers)
            {
                _blockchainStore.AddOmmer(ommer);    
            }
            
            return processedBlock;
        }

        public Block Process(Rlp blockRlp)
        {
            try
            {
                Block block = Rlp.Decode<Block>(blockRlp);
                if (_blockValidator.IsValid(block))
                {
                    return Process(block);
                }

                throw new InvalidBlockException(blockRlp);
            }
            catch (Exception ex)
            {
                throw new InvalidBlockException(blockRlp);
            }
        }
    }
}