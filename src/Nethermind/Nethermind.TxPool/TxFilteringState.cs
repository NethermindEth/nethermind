using Nethermind.Core;

namespace Nethermind.TxPool;

public struct TxFilteringState
{
    public Account? SenderAccount { get; set; }
}
