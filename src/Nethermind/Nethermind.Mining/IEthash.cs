using Nethermind.Core;

namespace Nethermind.Mining
{
    public interface IEthash
    {
        bool Validate(BlockHeader header);
    }
}