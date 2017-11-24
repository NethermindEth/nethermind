using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Core.Encoding;
using Nevermind.Evm;

namespace Ethereum.Test.Base
{
    public class TestBlockhashProvider : IBlockhashProvider
    {
        public Keccak? GetBlockhash(BlockHeader header, int depth)
        {
            return Keccak.Compute(depth.ToString());
        }
    }
}