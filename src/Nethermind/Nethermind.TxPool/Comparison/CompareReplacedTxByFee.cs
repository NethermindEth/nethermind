// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.TxPool.Comparison
{
    /// <summary>
    /// Compare fee of newcomer transaction with fee of transaction intended to be replaced increased by given percent
    /// </summary>
    public class CompareReplacedTxByFee : IComparer<Transaction?>
    {
        public static readonly CompareReplacedTxByFee Instance = new();

        private CompareReplacedTxByFee() { }

        // To replace old transaction, new transaction needs to have fee higher by at least 10% (1/10) of current fee.
        // It is required to avoid acceptance and propagation of transaction with almost the same fee as replaced one.
        private const int PartOfFeeRequiredToIncrease = 10;

        public int Compare(Transaction? x, Transaction? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (ReferenceEquals(null, y)) return 1;
            if (ReferenceEquals(null, x)) return -1;

            // always allow replacement of zero fee txs (in legacy txs MaxFeePerGas equals GasPrice)
            if (y.MaxFeePerGas.IsZero) return -1;

            if (!x.Supports1559 && !y.Supports1559)
            {
                y.GasPrice.Divide(PartOfFeeRequiredToIncrease, out UInt256 bumpGasPrice);
                return (y.GasPrice + bumpGasPrice).CompareTo(x.GasPrice);
            }

            /* MaxFeePerGas for legacy will be GasPrice and MaxPriorityFeePerGas will be GasPrice too
            so we can compare legacy txs without any problems */
            y.MaxFeePerGas.Divide(PartOfFeeRequiredToIncrease, out UInt256 bumpMaxFeePerGas);
            if (y.MaxFeePerGas + bumpMaxFeePerGas > x.MaxFeePerGas) return 1;

            y.MaxPriorityFeePerGas.Divide(PartOfFeeRequiredToIncrease, out UInt256 bumpMaxPriorityFeePerGas);
            return (y.MaxPriorityFeePerGas + bumpMaxPriorityFeePerGas).CompareTo(x.MaxPriorityFeePerGas);
        }

    }
}
