// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.TxPool.Collections;

namespace Nethermind.TxPool.Filters;

/// <summary>
/// Ignores blob transactions if sender already have pending transactions of other types; ignore other types if has already pending blobs
/// </summary>
public class TxTypeTxFilter : IIncomingTxFilter
{
    private readonly TxDistinctSortedPool _txs;
    private readonly TxDistinctSortedPool _blobTxs;

    public TxTypeTxFilter(TxDistinctSortedPool txs, TxDistinctSortedPool blobTxs)
    {
        _txs = txs;
        _blobTxs = blobTxs;
    }

    public AcceptTxResult Accept(Transaction tx, TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        if (tx.SupportsBlobs)
        {
            return _txs.TryGetBucket(tx.SenderAddress!, out _) ? AcceptTxResult.PendingTxsOfOtherType : AcceptTxResult.Accepted;
        }
        else
        {
            return _blobTxs.TryGetBucket(tx.SenderAddress!, out _) ? AcceptTxResult.PendingTxsOfOtherType : AcceptTxResult.Accepted;
        }
    }
}
