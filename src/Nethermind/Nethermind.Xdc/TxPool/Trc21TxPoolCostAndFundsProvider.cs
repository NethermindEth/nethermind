// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.TxPool;
using Nethermind.Xdc.Contracts;
using Nethermind.Xdc.Spec;

namespace Nethermind.Xdc.TxPool;

internal sealed class Trc21TxPoolCostAndFundsProvider(
    IBlockTree blockTree,
    ISpecProvider specProvider,
    ITrc21StateReader trc21StateReader) : ITxPoolCostAndFundsProvider
{
    private readonly ITxPoolCostAndFundsProvider _defaultProvider = DefaultTxPoolCostAndFundsProvider.Instance;

    public UInt256 GetAdditionalFunds(Transaction tx)
    {
        (XdcBlockHeader currentHead, _, IXdcReleaseSpec spec) = XdcTxPoolHelper.GetSpecAndHeader(blockTree, specProvider);
        if (!IsTrc21Transaction(tx, currentHead, spec))
            return UInt256.Zero;


        IReadOnlyDictionary<Address, UInt256> feeCapacities = trc21StateReader.GetFeeCapacities(currentHead);
        return feeCapacities.TryGetValue(tx.To!, out UInt256 capacity)
            ? capacity
            : UInt256.Zero;
    }

    public bool TryGetTransactionCost(Transaction tx, out UInt256 txCost)
    {
        var (currentHead, number, spec) = XdcTxPoolHelper.GetSpecAndHeader(blockTree, specProvider);
        if (!IsTrc21Transaction(tx, currentHead, spec))
        {
            return _defaultProvider.TryGetTransactionCost(tx, out txCost);
        }

        UInt256 gasPrice = number >= spec.BlockNumberGas50x
            ? XdcConstants.Trc21GasPrice50x
            : XdcConstants.Trc21GasPrice;

        bool overflow = UInt256.MultiplyOverflow(gasPrice, (UInt256)tx.GasLimit, out UInt256 gasCost);
        overflow |= UInt256.AddOverflow(gasCost, tx.ValueRef, out txCost);
        return !overflow;
    }

    private bool IsTrc21Transaction(Transaction tx, XdcBlockHeader currentHead, IXdcReleaseSpec spec)
    {
        if (tx.To is null || tx.SenderAddress is null || !spec.IsTipTrc21FeeEnabled)
            return false;

        IReadOnlyDictionary<Address, UInt256> feeCapacities = trc21StateReader.GetFeeCapacities(currentHead);
        return feeCapacities.ContainsKey(tx.To);
    }
}
