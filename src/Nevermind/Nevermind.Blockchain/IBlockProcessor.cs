using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Core.Encoding;

namespace Nevermind.Blockchain
{
    public interface IBlockProcessor
    {
        Block[] Process(Keccak? branchStateRoot, Block[] suggestedBlocks);
    }
}