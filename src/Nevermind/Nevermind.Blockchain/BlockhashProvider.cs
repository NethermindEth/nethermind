using System.Numerics;
using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Evm;

namespace Nevermind.Blockchain
{
    public class BlockhashProvider : IBlockhashProvider
    {
        private readonly IBlockchainStore _chain;

        public BlockhashProvider(IBlockchainStore chain)
        {
            _chain = chain;
        }

        public Keccak? GetBlockhash(Keccak blockHash, BigInteger number)
        {            
            Block block = _chain.FindBlock(blockHash);
            if (number > block.Header.Number)
            {
                return null;
            }
            
            for (int i = 0; i < 256; i++)
            {
                if (number == block.Header.Number)
                {
                    return block.Header.Hash;
                }
                
                block = _chain.FindBlock(block.Header.ParentHash);
            }

            return null;
        }
    }
}