// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
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

        public int Compare(Transaction? x, Transaction? y)
        {
            if (ReferenceEquals(x?.Hash, y?.Hash)) return 0;
            if (ReferenceEquals(null, y?.Hash)) return 1;
            if (ReferenceEquals(null, x?.Hash)) return -1;

            return x.Hash!.CompareTo(y.Hash);
        }

        public bool Equals(Transaction? x, Transaction? y) => Compare(x, y) == 0;

        public int GetHashCode(Transaction obj) => obj.Hash?.GetHashCode() ?? 0;
    }
}
