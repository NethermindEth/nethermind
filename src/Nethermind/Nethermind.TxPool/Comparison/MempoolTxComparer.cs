// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.TxPool.Comparison;

public static class MempoolTxComparer
{
    /// <summary>Orders <see cref="MempoolTransaction"/>s by their <see cref="MempoolTransaction.Transaction"/> alone.</summary>
    public static IComparer<MempoolTransaction> ForMempool(this IComparer<Transaction> comparer) => new TransactionComparer(comparer);

    private sealed class TransactionComparer(IComparer<Transaction> comparer) : IComparer<MempoolTransaction>
    {
        public int Compare(MempoolTransaction? x, MempoolTransaction? y) => comparer.Compare(x?.Transaction, y?.Transaction);
    }
}
