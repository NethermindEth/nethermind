// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.TxPool.Comparison
{
    /// <summary>
    /// Orders first by <see cref="Transaction.Nonce"/> asc and then by inner comparer
    /// </summary>
    public class CompareTxByNonce : IComparer<Transaction?>
    {
        public static readonly CompareTxByNonce Instance = new();

        private CompareTxByNonce() { }

        public int Compare(Transaction? newTx, Transaction? oldTx)
        {
            if (ReferenceEquals(newTx, oldTx)) return TxComparisonResult.NotDecided;
            if (ReferenceEquals(null, oldTx)) return TxComparisonResult.KeepOld;
            if (ReferenceEquals(null, newTx)) return TxComparisonResult.TakeNew;

            // compare by nonce ascending
            return newTx.Nonce.CompareTo(oldTx.Nonce);
        }
    }
}
