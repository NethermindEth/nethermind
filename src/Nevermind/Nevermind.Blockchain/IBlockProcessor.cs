using Nevermind.Core;
using Nevermind.Core.Encoding;

namespace Nevermind.Blockchain
{
    public interface IBlockProcessor
    {
        Block Process(Rlp rlp);
    }
}