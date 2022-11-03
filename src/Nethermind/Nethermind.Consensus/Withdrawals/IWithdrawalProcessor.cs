using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus.Withdrawals;

public interface IWithdrawalProcessor
{
    void ProcessWithdrawals(Block block, IReleaseSpec spec);
}
