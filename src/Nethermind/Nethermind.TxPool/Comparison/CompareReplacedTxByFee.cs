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
        private const ulong PartOfFeeRequiredToIncrease = 10;

        public int Compare(Transaction? newTx, Transaction? oldTx)
        {
            if (ReferenceEquals(newTx, oldTx)) return TxComparisonResult.NotDecided;
            if (oldTx is null) return TxComparisonResult.KeepOld;
            if (newTx is null) return TxComparisonResult.TakeNew;

            // always allow replacement of zero fee txs (in legacy txs MaxFeePerGas equals GasPrice)
            if (oldTx.MaxFeePerGas == 0) return TxComparisonResult.TakeNew;

            if (!newTx.Supports1559 && !oldTx.Supports1559)
            {
                UInt256 bumpGasPrice = oldTx.GasPrice / PartOfFeeRequiredToIncrease;
                int gasPriceResult = (oldTx.GasPrice + bumpGasPrice).CompareTo(newTx.GasPrice);
                // return TakeNew if fee bump is exactly by PartOfFeeRequiredToIncrease
                // never return NotDecided - it's allowed or not
                return gasPriceResult != 0 ? gasPriceResult : bumpGasPrice > 0
                    ? TxComparisonResult.TakeNew
                    : TxComparisonResult.KeepOld;
            }

            /* MaxFeePerGas for legacy will be GasPrice and MaxPriorityFeePerGas will be GasPrice too
            so we can compare legacy txs without any problems */
            (UInt256 oldFeeCap, UInt256 newFeeCap) = GetFeeCaps(newTx, oldTx);
            UInt256 bumpMaxFeePerGas = oldFeeCap / PartOfFeeRequiredToIncrease;
            if (oldFeeCap + bumpMaxFeePerGas > newFeeCap) return TxComparisonResult.KeepOld;

            UInt256 bumpMaxPriorityFeePerGas = oldTx.MaxPriorityFeePerGas / PartOfFeeRequiredToIncrease;
            int result = (oldTx.MaxPriorityFeePerGas + bumpMaxPriorityFeePerGas).CompareTo(newTx.MaxPriorityFeePerGas);
            // return TakeNew if fee bump is exactly by PartOfFeeRequiredToIncrease
            // never return NotDecided - it's allowed or not
            return result != 0 ? result : (bumpMaxFeePerGas > 0 && bumpMaxPriorityFeePerGas > 0)
                ? TxComparisonResult.TakeNew
                : TxComparisonResult.KeepOld;
        }

        /// <summary>The fee caps a replacement must bump: EIP-7999 <c>max_fee</c> when both carry one, else <see cref="Transaction.MaxFeePerGas"/>.</summary>
        internal static (UInt256 OldFeeCap, UInt256 NewFeeCap) GetFeeCaps(Transaction newTx, Transaction oldTx) =>
            oldTx.MaxFee is { } oldMaxFee && newTx.MaxFee is { } newMaxFee
                ? (oldMaxFee, newMaxFee)
                : (oldTx.MaxFeePerGas, newTx.MaxFeePerGas);
    }
}
