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
/// single EIP-8141 baseline transaction, and for every pending frame transaction a non-canonical paymaster
/// sponsors beyond its own.
/// </summary>
/// <remarks>
/// The sender pays whether or not a paymaster sponsors the transaction, and the charge scales
/// with the transaction's admission gas. A transaction <see cref="FrameTxPaymasterFilter"/> marked as beyond its
/// paymaster's baseline spends the same charge from the paymaster's width, keyed nonce or not. The sender is
/// charged first, so a sender without width cannot spend a paymaster's. The baseline is the transaction admitted while the sender had none pending, and a
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
    IChainHeadInfoProvider headInfo,
    TxDistinctSortedPool standardPool,
    TxDistinctSortedPool blobPool,
    SenderWidthCache senderWidth,
    ConcurrentDictionary<AddressAsKey, ValueHash256> senderBaselines,
    SenderWidthCache paymasterWidth,
    ILogger logger) : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        if (!txPoolConfig.FrameTxWidthEnabled || !tx.SupportsFrames)
        {
            return AcceptTxResult.Accepted;
        }

        bool beyondPaymasterBaseline = state.BeyondPaymasterBaseline;
        bool keyed = KeyedNonceManager.UsesKeyedNonce(tx);
        if (!keyed && !beyondPaymasterBaseline)
        {
            return AcceptTxResult.Accepted;
        }

        Address sender = tx.SenderAddress!;
        int pending = keyed ? PendingKeyedFrameTxs(sender) : 0;
        Transaction? replaced = PendingReplacement.Find(tx, standardPool, blobPool);
        bool beyondSenderBaseline = keyed;
        if (keyed && (replaced is null
                ? pending < FrameTxWidthCharge.Eip8141PublicMempoolBaseline
                : senderBaselines.TryGetValue(sender, out ValueHash256 baseline) && baseline == replaced.Hash!.ValueHash256))
        {
            state.TakesSenderBaseline = true;
            beyondSenderBaseline = false;
        }

        if (!beyondSenderBaseline && !beyondPaymasterBaseline)
        {
            return AcceptTxResult.Accepted;
        }

        if (replaced is not null && !(tx.CarriesBlobs ? blobPool : standardPool).CanReplace(tx, replaced))
        {
            return AcceptTxResult.ReplacementNotAllowed;
        }

        UInt256 nextBaseFee = headInfo.NextBaseFee;
        if (tx.MaxFeePerGas < nextBaseFee)
        {
            Metrics.PendingTransactionsTooLowFee++;
            if (logger.IsTrace)
                logger.Trace($"Skipped adding frame transaction {tx.Hash}, max fee per gas {tx.MaxFeePerGas} is below the next base fee {nextBaseFee}.");
            return AcceptTxResult.FeeTooLow.WithMessage($"MaxFeePerGas needs to be at least the next block's base fee ({nextBaseFee}) beyond the baseline, is {tx.MaxFeePerGas}.");
        }

        UInt256 cost = FrameTxWidthCharge.For(tx, state.HeadSpec, txPoolConfig.FrameTxWidthSafetyFactorPermille);
        Address? paymaster = beyondPaymasterBaseline ? PendingPaymasterCache.KeyFor(tx) : null;

        if (paymaster is not null && paymasterWidth.GetWidth(paymaster) < cost)
        {
            return PaymasterWidthUnmet(tx, paymaster, cost);
        }

        if (beyondSenderBaseline && !senderWidth.TrySpend(sender, cost))
        {
            Interlocked.Increment(ref Metrics.PendingTransactionsFrameTxWidthUnmet);
            if (logger.IsTrace)
                logger.Trace($"Skipped adding keyed-nonce frame transaction {tx.Hash}, sender {sender} holds {senderWidth.GetWidth(sender)} width against a cost of {cost} with {pending} pending.");
            return AcceptTxResult.WidthUnmet;
        }

        if (paymaster is not null && !paymasterWidth.TrySpend(paymaster, cost))
        {
            return PaymasterWidthUnmet(tx, paymaster, cost);
        }

        return AcceptTxResult.Accepted;
    }

    private AcceptTxResult PaymasterWidthUnmet(Transaction tx, Address paymaster, in UInt256 cost)
    {
        Interlocked.Increment(ref Metrics.PendingTransactionsFrameTxPaymasterWidthUnmet);
        if (logger.IsTrace)
            logger.Trace($"Skipped adding frame transaction {tx.Hash}, paymaster {paymaster} holds {paymasterWidth.GetWidth(paymaster)} width against a cost of {cost}.");
        return AcceptTxResult.PaymasterWidthUnmet;
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
