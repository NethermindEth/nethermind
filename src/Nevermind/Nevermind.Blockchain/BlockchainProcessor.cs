using System;
using System.Numerics;
using Nevermind.Core;
using Nevermind.Core.Encoding;

namespace Nevermind.Blockchain
{
    public class BlockchainProcessor : IBlockchainProcessor
    {
        private readonly IBlockchainStore _blockchainStore;
        private readonly IBlockProcessor _blockProcessor;
        private readonly ILogger _logger;

        public BlockchainProcessor(
            Rlp genesisBlockRlp,
            IBlockProcessor blockProcessor,
            IBlockchainStore blockchainStore,
            ILogger logger)
        {
            _blockchainStore = blockchainStore;
            _blockProcessor = blockProcessor;
            _logger = logger;

            Process(genesisBlockRlp);
        }

        public Block HeadBlock { get; private set; }
        public BigInteger TotalDifficulty { get; private set; } // TODO: chain selection / sidechains

        public Block Process(Rlp blockRlp)
        {
            try
            {
                Block processedBlock = _blockProcessor.Process(blockRlp);
                AddToChain(processedBlock);
                return processedBlock;
            }
            catch (Exception)
            {
                throw new InvalidBlockException(blockRlp);
            }
        }

        private void AddToChain(Block processedBlock)
        {
            HeadBlock = processedBlock;
            _blockchainStore.AddBlock(HeadBlock);
            TotalDifficulty += HeadBlock.Header.Difficulty;
            foreach (BlockHeader ommer in processedBlock.Ommers)
            {
                _blockchainStore.AddOmmer(ommer);
            }
        }
    }
}