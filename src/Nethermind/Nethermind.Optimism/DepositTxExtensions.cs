using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Optimism;

public static class DepositTxExtensions
{
    private static readonly Address _l1BlockAddr = new("0x4200000000000000000000000000000000000015");
    private static readonly StorageCell _l1BaseFeeSlot = new(_l1BlockAddr, new UInt256(1));
    private static readonly StorageCell _overheadSlot = new(_l1BlockAddr, new UInt256(5));
    private static readonly StorageCell _scalarSlot = new(_l1BlockAddr, new UInt256(6));

    public static bool IsDeposit(this Transaction tx)
    {
        return tx.Type == TxType.DepositTx;
    }

    public static UInt256 L1Cost(this Transaction tx, IWorldState worldState, long blockNumber, long blockTime, long dataGas, bool isDepositTx)
    {
        if (isDepositTx || dataGas == 0)
            return UInt256.Zero;

        UInt256 l1BaseFee = new(worldState.Get(_l1BaseFeeSlot), true);
        UInt256 overhead = new(worldState.Get(_overheadSlot), true);
        UInt256 scalar = new(worldState.Get(_scalarSlot), true);

        return ((UInt256)dataGas + overhead) * l1BaseFee * scalar / 1_000_000;
    }
}
