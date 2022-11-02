using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus.Withdrawals;

public interface IWithdrawalApplier
{
    void ApplyWithdrawals(Block block, IReleaseSpec spec);
}
