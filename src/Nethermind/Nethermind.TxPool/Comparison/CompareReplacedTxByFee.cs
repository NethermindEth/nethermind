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

        public int Compare(Transaction? newTx, Transaction? oldTx)
        {
            if (ReferenceEquals(newTx, oldTx)) return TxComparisonResult.NotDecided;
            if (oldTx is null) return TxComparisonResult.KeepOld;
            if (newTx is null) return TxComparisonResult.TakeNew;

            // always allow replacement of zero fee txs (in legacy txs MaxFeePerGas equals GasPrice)
            if (oldTx.MaxFeePerGas.IsZero) return TxComparisonResult.TakeNew;

            if (!newTx.Supports1559 && !oldTx.Supports1559)
            {
                oldTx.GasPrice.Divide(PartOfFeeRequiredToIncrease, out UInt256 bumpGasPrice);
                int gasPriceResult = (oldTx.GasPrice + bumpGasPrice).CompareTo(newTx.GasPrice);
                // return TakeNew if fee bump is exactly by PartOfFeeRequiredToIncrease
                // never return NotDecided - it's allowed or not
                return gasPriceResult != 0 ? gasPriceResult : bumpGasPrice > 0
                    ? TxComparisonResult.TakeNew
                    : TxComparisonResult.KeepOld;
            }

            /* MaxFeePerGas for legacy will be GasPrice and MaxPriorityFeePerGas will be GasPrice too
            so we can compare legacy txs without any problems */
            oldTx.MaxFeePerGas.Divide(PartOfFeeRequiredToIncrease, out UInt256 bumpMaxFeePerGas);
            if (oldTx.MaxFeePerGas + bumpMaxFeePerGas > newTx.MaxFeePerGas) return TxComparisonResult.KeepOld;

            oldTx.MaxPriorityFeePerGas.Divide(PartOfFeeRequiredToIncrease, out UInt256 bumpMaxPriorityFeePerGas);
            int result = (oldTx.MaxPriorityFeePerGas + bumpMaxPriorityFeePerGas).CompareTo(newTx.MaxPriorityFeePerGas);
            // return TakeNew if fee bump is exactly by PartOfFeeRequiredToIncrease
            // never return NotDecided - it's allowed or not
            return result != 0 ? result : (bumpMaxFeePerGas > 0 && bumpMaxPriorityFeePerGas > 0)
                ? TxComparisonResult.TakeNew
                : TxComparisonResult.KeepOld;
        }

    }
}
