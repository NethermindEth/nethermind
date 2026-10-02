// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Threading;
using Nethermind.Core.Crypto;
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
/// with the transaction's admission gas. The baseline is the transaction admitted while the sender had none pending, and a
/// replacement of it stays the baseline; replacing any other pending transaction spends width. Once the baseline leaves, no
/// other pending transaction takes its place until the sender's pending set empties. Spent width is never returned, which is what bounds repeated mass invalidation, so a fee
/// bump beyond the baseline spends width like any admission: its rerun is real work. A replacement the pool would refuse is
/// refused here, before any width is spent. Otherwise width is spent when this filter accepts, so a transaction that a
/// later pool check rejects, such as a fee too low to compete, still spends it. Only the sender's pending
/// keyed-nonce frame transactions count toward the baseline, since other pending types add no revalidation work.
/// Runs inside the sender's admission gate, held through insertion, so concurrent admissions from one sender see
/// each other and only one takes the free baseline. Inert unless
/// <see cref="ITxPoolConfig.FrameTxWidthEnabled"/>.
/// </remarks>
internal sealed class FrameTxWidthFilter(
    ITxPoolConfig txPoolConfig,
    TxDistinctSortedPool standardPool,
    TxDistinctSortedPool blobPool,
    SenderWidthCache senderWidth,
    ConcurrentDictionary<AddressAsKey, ValueHash256> senderBaselines,
    ILogger logger) : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        if (!txPoolConfig.FrameTxWidthEnabled || !tx.SupportsFrames || !KeyedNonceManager.UsesKeyedNonce(tx))
        {
            return AcceptTxResult.Accepted;
        }

        Address sender = tx.SenderAddress!;
        int pending = PendingKeyedFrameTxs(sender);
        Transaction? replaced = PendingReplacement.Find(tx, standardPool, blobPool);
        if (replaced is null
                ? pending < FrameTxWidthCharge.Eip8141PublicMempoolBaseline
                : senderBaselines.TryGetValue(sender, out ValueHash256 baseline) && baseline == replaced.Hash!.ValueHash256)
        {
            state.TakesSenderBaseline = true;
            return AcceptTxResult.Accepted;
        }

        if (replaced is not null && !(tx.CarriesBlobs ? blobPool : standardPool).CanReplace(tx, replaced))
        {
            return AcceptTxResult.ReplacementNotAllowed;
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

    private int PendingKeyedFrameTxs(Address sender)
    {
        int count = 0;
        standardPool.VisitBucket(sender, ref count, CountKeyedFrameTx);
        blobPool.VisitBucket(sender, ref count, CountKeyedFrameTx);
        return count;
    }

    private static bool CountKeyedFrameTx(Transaction pending, ref int count)
    {
        if (pending.SupportsFrames && KeyedNonceManager.UsesKeyedNonce(pending))
        {
            count++;
        }

        return count < FrameTxWidthCharge.Eip8141PublicMempoolBaseline;
    }
}
