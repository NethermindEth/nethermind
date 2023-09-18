// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.TxPool.Collections;

namespace Nethermind.TxPool.Filters;

public class MaxPendingTxsPerSenderFilter : IIncomingTxFilter
{
    private readonly ITxPoolConfig _txPoolConfig;
    private readonly TxDistinctSortedPool _txs;
    private readonly TxDistinctSortedPool _blobTxs;

    public MaxPendingTxsPerSenderFilter(ITxPoolConfig txPoolConfig, TxDistinctSortedPool txs, TxDistinctSortedPool blobTxs)
    {
        _txPoolConfig = txPoolConfig;
        _txs = txs;
        _blobTxs = blobTxs;
    }

    public AcceptTxResult Accept(Transaction tx, TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        int relevantMaxPendingTxsPerSender = (tx.SupportsBlobs
            ? _txPoolConfig.MaxPendingBlobTxsPerSender
            : _txPoolConfig.MaxPendingTxsPerSender);

        if (relevantMaxPendingTxsPerSender == 0)
        {
            return AcceptTxResult.Accepted;
        }

        TxDistinctSortedPool relevantTxPool = (tx.SupportsBlobs ? _blobTxs : _txs);

        if (relevantTxPool.GetBucketCount(tx.SenderAddress!) > relevantMaxPendingTxsPerSender)
        {
            Metrics.PendingTransactionsNonceTooFarInFuture++;
            return AcceptTxResult.NonceTooFarInFuture;
        }

        return AcceptTxResult.Accepted;
    }
}
