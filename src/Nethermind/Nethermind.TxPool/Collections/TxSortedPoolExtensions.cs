// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.TxPool.Comparison;

namespace Nethermind.TxPool.Collections
{
    public static class TxSortedPoolExtensions
    {
        public static IComparer<Transaction> GetPoolUniqueTxComparer(this IComparer<Transaction> comparer)
            => comparer
                .ThenBy(ByHashTxComparer.Instance); // in order to sort properly and not lose transactions we need to differentiate on their identity which provided comparer might not be doing

        public static IComparer<Transaction> GetPoolUniqueTxComparerByNonce(this IComparer<Transaction> comparer)
            => CompareTxByNonce.Instance // we need to ensure transactions are ordered by nonce, which might not be done in supplied comparer
                .ThenBy(GetPoolUniqueTxComparer(comparer));

        public static IComparer<Transaction> GetReplacementComparer(this IComparer<Transaction> comparer)
            => CompareReplacedTxByFee.Instance.ThenBy(comparer);

        public static Address? MapTxToGroup(this Transaction value) => value.SenderAddress;
    }
}
