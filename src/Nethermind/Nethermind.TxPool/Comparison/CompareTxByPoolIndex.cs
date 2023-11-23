// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.TxPool.Comparison
{
    /// <summary>
    /// Default ordering by <see cref="Transaction.PoolIndex"/> asc
    /// </summary>
    public class CompareTxByPoolIndex : IComparer<Transaction?>
    {
        public static readonly CompareTxByPoolIndex Instance = new();

        private CompareTxByPoolIndex() { }

        public int Compare(Transaction? newTx, Transaction? oldTx)
        {
            if (ReferenceEquals(newTx, oldTx)) return TxComparisonResult.NotDecided;
            if (ReferenceEquals(null, oldTx)) return TxComparisonResult.KeepOld;
            if (ReferenceEquals(null, newTx)) return TxComparisonResult.TakeNew;

            return newTx.PoolIndex.CompareTo(oldTx.PoolIndex);
        }
    }
}
