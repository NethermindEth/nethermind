using Nevermind.Core;
using Nevermind.Core.Encoding;

namespace Nevermind.Evm
{
    public class BlockhashProvider : IBlockhashProvider
    {
        public Keccak GetBlockhash(BlockHeader block, int depth)
        {
            throw new System.NotImplementedException();
        }
    }
}