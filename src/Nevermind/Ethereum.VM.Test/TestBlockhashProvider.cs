using Nevermind.Core;
using Nevermind.Core.Encoding;
using Nevermind.Evm;

namespace Ethereum.VM.Test
{
    public class TestBlockhashProvider : IBlockhashProvider
    {
        public Keccak GetBlockhash(Block block, int depth)
        {
            return Keccak.Compute(depth.ToString());
        }
    }
}