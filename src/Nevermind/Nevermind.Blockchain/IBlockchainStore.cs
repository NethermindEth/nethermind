using Nevermind.Core;
using Nevermind.Core.Crypto;

namespace Nevermind.Blockchain
{
    public interface IBlockchainStore
    {
        void AddBlock(Block block);
        Block FindBlock(Keccak blockHash);
    }
}