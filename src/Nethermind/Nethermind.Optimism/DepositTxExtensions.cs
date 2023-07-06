using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Optimism;

public static class DepositTxExtensions
{

    public static bool IsDeposit(this Transaction tx)
    {
        return tx.Type == TxType.DepositTx;
    }
}
