// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool.Collections;

namespace Nethermind.TxPool.Filters;

/// <summary>
/// Charges MATCHA width for every pending EIP-8250 keyed-nonce frame transaction a sender admits beyond the
/// single EIP-8141 baseline transaction.
/// </summary>
/// <remarks>
/// Sender-keyed only: the sender pays whether or not a paymaster sponsors the transaction, and the charge scales
/// with the transaction's admission gas. A replacement displaces a pending entry rather than adding one, so it is judged against the count
/// without the incumbent. Spent width is never returned, which is what bounds repeated mass invalidation.
/// Inert unless <see cref="ITxPoolConfig.FrameTxWidthEnabled"/>.
/// </remarks>
internal sealed class FrameTxWidthFilter(
    ITxPoolConfig txPoolConfig,
    TxDistinctSortedPool standardPool,
    TxDistinctSortedPool blobPool,
    SenderWidthCache senderWidth,
    ILogger logger) : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        if (!txPoolConfig.FrameTxWidthEnabled || !tx.SupportsFrames || !KeyedNonceManager.UsesKeyedNonce(tx))
        {
            return AcceptTxResult.Accepted;
        }

        Address sender = tx.SenderAddress!;
        int pending = standardPool.GetBucketCount(sender) + blobPool.GetBucketCount(sender);
        if (PendingReplacement.Find(tx, standardPool, blobPool) is not null) pending--;
        if (pending < FrameTxWidthCharge.Eip8141PublicMempoolBaseline)
        {
            return AcceptTxResult.Accepted;
        }

        UInt256 cost = FrameTxWidthCharge.For(tx, txPoolConfig.FrameTxWidthSafetyFactorPermille);
        if (!senderWidth.TrySpend(sender, cost))
        {
            Interlocked.Increment(ref Metrics.PendingTransactionsFrameTxWidthUnmet);
            if (logger.IsTrace)
                logger.Trace($"Skipped adding keyed-nonce frame transaction {tx.Hash}, sender {sender} holds {senderWidth.GetWidth(sender)} width against a cost of {cost} with {pending} pending.");
            return AcceptTxResult.WidthUnmet;
        }

        return AcceptTxResult.Accepted;
    }
}
