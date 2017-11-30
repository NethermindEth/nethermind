using System.Numerics;
using Nevermind.Blockchain.Validators;
using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Evm;

namespace Nevermind.Blockchain
{
    public class BlockhashProvider : IBlockhashProvider
    {
        private readonly IBlockStore _chain;

        public BlockhashProvider(IBlockStore chain)
        {
            _chain = chain;
        }

        public Keccak? GetBlockhash(Keccak blockHash, BigInteger number)
        {
            Block block = _chain.FindBlock(blockHash, false);
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

                block = _chain.FindParent(block.Header);
            }

            return null;
        }
    }
}