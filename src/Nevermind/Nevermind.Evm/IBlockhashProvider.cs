using Nevermind.Core;
using Nevermind.Core.Crypto;

namespace Nevermind.Evm
{
    public interface IBlockhashProvider
    {
        Keccak GetBlockhash(BlockHeader block, int depth);
    }
}