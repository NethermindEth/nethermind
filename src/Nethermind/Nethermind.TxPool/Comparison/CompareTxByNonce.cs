// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
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

        public int Compare(Transaction? x, Transaction? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (ReferenceEquals(null, y)) return 1;
            if (ReferenceEquals(null, x)) return -1;

            // compare by nonce ascending
            return x.Nonce.CompareTo(y.Nonce);
        }
    }
}
