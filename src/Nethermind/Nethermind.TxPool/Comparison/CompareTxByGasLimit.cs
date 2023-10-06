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

        public int Compare(Transaction? x, Transaction? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (ReferenceEquals(null, y)) return 1;
            if (ReferenceEquals(null, x)) return -1;

            // then by gas limit ascending
            return x.GasLimit.CompareTo(y.GasLimit);
        }
    }
}
