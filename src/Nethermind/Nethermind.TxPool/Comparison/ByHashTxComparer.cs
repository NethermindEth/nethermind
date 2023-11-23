// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.TxPool.Comparison
{
    /// <summary>
    /// Compares <see cref="Transaction"/>s based on <see cref="Transaction.Hash"/> identity. No two different signed transactions will be same.
    /// </summary>
    public class ByHashTxComparer : IComparer<Transaction>, IEqualityComparer<Transaction>
    {
        public static readonly ByHashTxComparer Instance = new();

        private ByHashTxComparer() { }

        public int Compare(Transaction? newTx, Transaction? oldTx)
        {
            if (ReferenceEquals(newTx?.Hash, oldTx?.Hash)) return TxComparisonResult.NotDecided;
            if (ReferenceEquals(null, oldTx?.Hash)) return TxComparisonResult.KeepOld;
            if (ReferenceEquals(null, newTx?.Hash)) return TxComparisonResult.TakeNew;

            return newTx.Hash!.CompareTo(oldTx.Hash);
        }

        public bool Equals(Transaction? x, Transaction? y) => Compare(x, y) == TxComparisonResult.NotDecided;

        public int GetHashCode(Transaction obj) => obj.Hash?.GetHashCode() ?? 0;
    }
}
