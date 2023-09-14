// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Optimism;

public class OPL1CostHelper : IL1CostHelper
{
    private static readonly Address _l1BlockAddr = new("0x4200000000000000000000000000000000000015");
    private static readonly StorageCell _l1BaseFeeSlot = new(_l1BlockAddr, new UInt256(1));
    private static readonly StorageCell _overheadSlot = new(_l1BlockAddr, new UInt256(5));
    private static readonly StorageCell _scalarSlot = new(_l1BlockAddr, new UInt256(6));

    public UInt256 ComputeL1Cost(Transaction tx, IWorldState worldState, long number, ulong timestamp, bool isDeposit)
    {
        ulong dataGas = ComputeDataGas();

        if (isDeposit || dataGas == 0)
            return UInt256.Zero;

        UInt256 l1BaseFee = new(worldState.Get(_l1BaseFeeSlot), true);
        UInt256 overhead = new(worldState.Get(_overheadSlot), true);
        UInt256 scalar = new(worldState.Get(_scalarSlot), true);

        return ((UInt256)dataGas + overhead) * l1BaseFee * scalar / 1_000_000;
    }

    private static ulong ComputeDataGas()
    {
        return 0;
    }
}
