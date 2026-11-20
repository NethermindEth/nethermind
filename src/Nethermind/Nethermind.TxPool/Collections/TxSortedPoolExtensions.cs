// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.TxPool.Comparison;

namespace Nethermind.TxPool.Collections
{
    public static class TxSortedPoolExtensions
    {
        public static IComparer<MempoolTransaction> GetPoolUniqueTxComparer(this IComparer<MempoolTransaction> comparer)
            => comparer
                .ThenBy(ByHashTxComparer.Instance.ForMempool()); // in order to sort properly and not lose transactions we need to differentiate on their identity which provided comparer might not be doing

        public static IComparer<MempoolTransaction> GetPoolUniqueTxComparerByNonce(this IComparer<MempoolTransaction> comparer)
            => CompareTxByNonce.Instance.ForMempool() // we need to ensure transactions are ordered by nonce, which might not be done in supplied comparer
                .ThenBy(GetPoolUniqueTxComparer(comparer));

        public static IComparer<MempoolTransaction> GetReplacementComparer(this IComparer<MempoolTransaction> comparer)
            => CompareReplacedTxByFee.Instance.ForMempool().ThenBy(comparer);

        public static IComparer<MempoolTransaction> GetBlobReplacementComparer(this IComparer<MempoolTransaction> comparer)
            => CompareReplacedBlobTx.Instance.ForMempool().ThenBy(comparer);

        public static Address? MapTxToGroup(this Transaction value) => value.SenderAddress;
    }
}
