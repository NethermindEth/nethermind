using Nethermind.Core;

namespace Nethermind.Optimism;

public interface IOPConfigHelper
{
    bool IsBedrock(BlockHeader header);
    bool IsRegolith(BlockHeader header);
}
