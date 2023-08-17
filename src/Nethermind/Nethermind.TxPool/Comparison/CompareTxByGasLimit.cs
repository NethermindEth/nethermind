// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.TxPool.Comparison
{
    /// <summary>
    /// Default ordering by <see cref="Transaction.GasLimit"/> asc
    /// </summary>
    public class CompareTxByGasLimit : IComparer<Transaction?>
    {
        public static readonly CompareTxByGasLimit Instance = new();

        private CompareTxByGasLimit() { }

        public int Compare(Transaction? newTx, Transaction? oldTx)
        {
            if (ReferenceEquals(newTx, oldTx)) return TxComparisonResult.NotDecided;
            if (ReferenceEquals(null, oldTx)) return TxComparisonResult.KeepOld;
            if (ReferenceEquals(null, newTx)) return TxComparisonResult.TakeNew;

            // then by gas limit ascending
            return newTx.GasLimit.CompareTo(oldTx.GasLimit);
        }
    }
}
