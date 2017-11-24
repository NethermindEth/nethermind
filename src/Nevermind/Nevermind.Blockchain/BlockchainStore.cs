using System.Collections.Generic;
using Nevermind.Core;
using Nevermind.Core.Crypto;

namespace Nevermind.Blockchain
{
    public class BlockchainStore : IBlockchainStore
    {
        private readonly Dictionary<Keccak, Block> _chain = new Dictionary<Keccak, Block>();
        private readonly Dictionary<Keccak, BlockHeader> _ommers = new Dictionary<Keccak, BlockHeader>();

        public void AddBlock(Block block)
        {
            _chain.Add(block.Header.Hash, block);
        }

        public Block FindBlock(Keccak blockHash)
        {
            _chain.TryGetValue(blockHash, out Block block);
            return block;
        }

        public void AddOmmer(BlockHeader blockHeader)
        {
            _ommers.Add(blockHeader.Hash, blockHeader);
        }

        public BlockHeader FindOmmer(Keccak blockHash)
        {
            _ommers.TryGetValue(blockHash, out BlockHeader header);
            return header;
        }
    }
}