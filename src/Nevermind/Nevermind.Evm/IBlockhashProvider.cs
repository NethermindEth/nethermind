using Nevermind.Core;
using Nevermind.Core.Encoding;

namespace Nevermind.Evm
{
    public interface IBlockhashProvider
    {
        Keccak GetBlockhash(BlockHeader block, int depth);
    }
}