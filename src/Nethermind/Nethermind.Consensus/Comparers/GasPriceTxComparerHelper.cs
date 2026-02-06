// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.TxPool.Comparison;

namespace Nethermind.Consensus.Comparers
{
    public static class GasPriceTxComparerHelper
    {
        public static int Compare(Transaction? x, Transaction? y, in UInt256 baseFee, bool isEip1559Enabled)
        {
            if (ReferenceEquals(x, y)) return TxComparisonResult.Equal;
            if (y is null) return TxComparisonResult.YFirst;
            if (x is null) return TxComparisonResult.XFirst;

            // EIP1559 changed the way we're sorting transactions. The transaction with a higher miner tip should go first
            if (isEip1559Enabled)
            {
                UInt256 xGasPrice = UInt256.Min(x.MaxFeePerGas, x.MaxPriorityFeePerGas + baseFee);
                UInt256 yGasPrice = UInt256.Min(y.MaxFeePerGas, y.MaxPriorityFeePerGas + baseFee);
                if (xGasPrice < yGasPrice) return TxComparisonResult.YFirst;
                if (xGasPrice > yGasPrice) return TxComparisonResult.XFirst;

                return y.MaxFeePerGas.CompareTo(x.MaxFeePerGas);
            }

            // the old way of sorting transactions
            return y.GasPrice.CompareTo(x.GasPrice);
        }

    }
}
