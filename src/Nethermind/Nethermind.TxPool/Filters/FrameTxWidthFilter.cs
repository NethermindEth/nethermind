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
/// without the incumbent. Spent width is never returned, which is what bounds repeated mass invalidation, so a fee
/// bump beyond the baseline spends width like any admission: its rerun is real work. Only the sender's pending
/// keyed-nonce frame transactions count toward the baseline, since other pending types add no revalidation work.
/// Filters run under the head read lock, so two concurrent admissions from one sender can both see the baseline
/// free; the bound moves by that one transaction, not per head. Inert unless
/// <see cref="ITxPoolConfig.FrameTxWidthEnabled"/>.
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
        int pending = PendingKeyedFrameTxs(sender, PendingReplacement.Find(tx, standardPool, blobPool));
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

    private int PendingKeyedFrameTxs(Address sender, Transaction? replaced)
    {
        PendingCount count = new(replaced);
        standardPool.VisitBucket(sender, ref count, CountKeyedFrameTx);
        blobPool.VisitBucket(sender, ref count, CountKeyedFrameTx);
        return count.Count;
    }

    private static bool CountKeyedFrameTx(Transaction pending, ref PendingCount state)
    {
        if (!ReferenceEquals(pending, state.Replaced) && pending.SupportsFrames && KeyedNonceManager.UsesKeyedNonce(pending))
        {
            state.Count++;
        }

        return state.Count < FrameTxWidthCharge.Eip8141PublicMempoolBaseline;
    }

    private struct PendingCount(Transaction? replaced)
    {
        public readonly Transaction? Replaced = replaced;
        public int Count;
    }
}
