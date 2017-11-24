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

        public Keccak? GetBlockhash(BlockHeader header, int depth)
        {
            if (depth <= 0 || depth > 255)
            {
                return null;
            }
            
            BlockHeader thatBlock = header;
            while (depth-- > 0)
            {
                thatBlock = _chain.FindBlock(thatBlock.ParentHash)?.Header;
            }

            return thatBlock?.Hash;
        }
    }
}