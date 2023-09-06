// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.TxPool.Comparison
{
    /// <summary>
    /// Default ordering by <see cref="Transaction.Timestamp"/> asc
    /// </summary>
    public class CompareTxByTimestamp : IComparer<Transaction?>
    {
        public static readonly CompareTxByTimestamp Instance = new();

        private CompareTxByTimestamp() { }

        public int Compare(Transaction? newTx, Transaction? oldTx)
        {
            if (ReferenceEquals(newTx, oldTx)) return TxComparisonResult.NotDecided;
            if (ReferenceEquals(null, oldTx)) return TxComparisonResult.KeepOld;
            if (ReferenceEquals(null, newTx)) return TxComparisonResult.TakeNew;

            return newTx.Timestamp.CompareTo(oldTx.Timestamp);
        }
    }
}
