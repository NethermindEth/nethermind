// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Consensus.Comparers
{
    public static class GasPriceTxComparerHelper
    {
        public static int Compare(Transaction? x, Transaction? y, in UInt256 baseFee, bool isEip1559Enabled)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (ReferenceEquals(null, y)) return 1;
            if (ReferenceEquals(null, x)) return -1;

            // EIP1559 changed the way we're sorting transactions. The transaction with a higher miner tip should go first
            if (isEip1559Enabled)
            {
                UInt256 xGasPrice = UInt256.Min(x.MaxFeePerGas, x.MaxPriorityFeePerGas + baseFee);
                UInt256 yGasPrice = UInt256.Min(y.MaxFeePerGas, y.MaxPriorityFeePerGas + baseFee);
                if (xGasPrice < yGasPrice) return 1;
                if (xGasPrice > yGasPrice) return -1;

                return y.MaxFeePerGas.CompareTo(x.MaxFeePerGas);
            }

            // the old way of sorting transactions
            return y.GasPrice.CompareTo(x.GasPrice);
        }

    }
}
