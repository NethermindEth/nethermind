using Nethermind.Core;

namespace Nethermind.Optimism;

public interface IOPConfigHelper
{
    Address L1FeeReceiver { get; }

    bool IsBedrock(BlockHeader header);
    bool IsRegolith(BlockHeader header);
}
