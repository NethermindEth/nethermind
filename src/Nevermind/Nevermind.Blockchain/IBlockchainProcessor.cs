using System.Collections.Generic;
using System.Numerics;
using Nevermind.Core;

namespace Nevermind.Blockchain
{
    public interface IBlockchainProcessor
    {
        Block HeadBlock { get; }
        BigInteger TotalDifficulty { get; }
        void ProcessBlocks(List<Block> blocks);        
    }
}