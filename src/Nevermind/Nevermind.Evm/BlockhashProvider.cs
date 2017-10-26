using Nevermind.Core;
using Nevermind.Core.Encoding;

namespace Nevermind.Evm
{
    public class BlockhashProvider : IBlockhashProvider
    {
        public Keccak GetBlockhash(Block block, int depth)
        {
            throw new System.NotImplementedException();
        }
    }
}