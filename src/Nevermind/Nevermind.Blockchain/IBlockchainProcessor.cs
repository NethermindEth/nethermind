using System.Collections.Generic;
using Nevermind.Core;
using Nevermind.Core.Crypto;

namespace Nevermind.Blockchain
{
    public interface IBlockchainProcessor
    {
        Block HeadBlock { get; }
        void ProcessBlocks(List<Block> blocks);
        Block GetBlock(Keccak hash);
    }
}