// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.TxPool.Collections;

namespace Nethermind.TxPool.Filters;

/// <summary>
/// Ignores blob transactions if sender already have pending transactions of other types; ignore other types if has already pending blobs
/// </summary>
public class TxTypeTxFilter(TxDistinctSortedPool txs, TxDistinctSortedPool blobTxs) : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        TxDistinctSortedPool otherTxTypePool = tx.SupportsBlobs ? txs : blobTxs;
        if (otherTxTypePool.ContainsBucket(tx.SenderAddress!)) // as unknownSenderFilter will run before this one
        {
            Metrics.PendingTransactionsConflictingTxType++;
            return AcceptTxResult.PendingTxsOfConflictingType;
        }
        return AcceptTxResult.Accepted;
    }
}
