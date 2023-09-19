// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State;

namespace Nethermind.Optimism;

public class OPL1CostHelper : IL1CostHelper
{
    private readonly IOPConfigHelper _opConfigHelper;

    private readonly StorageCell _l1BaseFeeSlot;
    private readonly StorageCell _overheadSlot;
    private readonly StorageCell _scalarSlot;

    public OPL1CostHelper(IOPConfigHelper opConfigHelper, Address l1BlockAddr)
    {
        _opConfigHelper = opConfigHelper;

        _l1BaseFeeSlot = new StorageCell(l1BlockAddr, new UInt256(1));
        _overheadSlot = new StorageCell(l1BlockAddr, new UInt256(5));
        _scalarSlot = new StorageCell(l1BlockAddr, new UInt256(6));
    }

    public UInt256 ComputeL1Cost(Transaction tx, BlockHeader header, IWorldState worldState)
    {
        if (tx.IsDeposit())
            return UInt256.Zero;

        long dataGas = ComputeDataGas(tx, header);
        if (dataGas == 0)
            return UInt256.Zero;

        // TODO: cache state access
        UInt256 l1BaseFee = new(worldState.Get(_l1BaseFeeSlot), true);
        UInt256 overhead = new(worldState.Get(_overheadSlot), true);
        UInt256 scalar = new(worldState.Get(_scalarSlot), true);

        return ((UInt256)dataGas + overhead) * l1BaseFee * scalar / 1_000_000;
    }

    // TODO: cache this result
    private long ComputeDataGas(Transaction tx, BlockHeader header)
    {
        byte[] encoded = Rlp.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes;

        long zeroCount = encoded.Count(b => b == 0);
        long nonZeroCount = encoded.Length - zeroCount;
        // Add pre-EIP-3529 overhead
        nonZeroCount += _opConfigHelper.IsRegolith(header) ? 0 : 68;

        return zeroCount * GasCostOf.TxDataZero + nonZeroCount * GasCostOf.TxDataNonZeroEip2028;
    }
}
