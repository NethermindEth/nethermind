using System.Collections.Generic;
using Nevermind.Core;
using Nevermind.Core.Crypto;

namespace Nevermind.Blockchain
{
    public class BlockchainStore : IBlockchainStore
    {
        private readonly Dictionary<Keccak, Block> _chain = new Dictionary<Keccak, Block>();

        public void AddBlock(Block block)
        {
            _chain.Add(block.Header.Hash, block);
        }

        public Block FindBlock(Keccak blockHash)
        {
            _chain.TryGetValue(blockHash, out Block block);
            return block;
        }
    }
}