using Nevermind.Core;
using Nevermind.Core.Encoding;

namespace Nevermind.Evm
{
    public interface IBlockhashProvider
    {
        Keccak GetBlockhash(Block block, int depth);
    }
}