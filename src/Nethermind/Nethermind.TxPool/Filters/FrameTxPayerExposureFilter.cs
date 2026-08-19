// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool.Collections;
using Nethermind.TxPool.Comparison;

namespace Nethermind.TxPool.Filters;

/// <summary>Rejects a frame transaction whose payer's summed pending maximum cost would exceed its balance (EIP-8141).</summary>
/// <remarks>The reservation is taken at admission and released when the transaction leaves the pool.</remarks>
internal sealed class FrameTxPayerExposureFilter(
    IChainHeadSpecProvider specProvider,
    IReadOnlyStateProvider stateProvider,
    TxDistinctSortedPool standardPool,
    TxDistinctSortedPool blobPool,
    PayerExposureCache exposure,
    ILogger logger) : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        Address? payer = tx.SupportsFrames ? tx.PayerAddress : null;
        if (payer is null)
        {
            return AcceptTxResult.Accepted;
        }

        // The upper-bound TXPARAM(0x06), shared with the processor so the admission bound and the
        // payer-solvency gate cannot drift — this is where the base branch's under-reservation closes.
        IReleaseSpec spec = specProvider.GetCurrentHeadSpec();
        if (!FrameTxValidation.TryCalculateMaxCost(tx, spec, out UInt256 maxCost))
        {
            // Unincludable rather than malformed: Invalid is the one result that disconnects the relaying peer.
            return AcceptTxResult.Int256Overflow.WithMessage("Frame transaction maximum cost cannot be priced");
        }

        // A simulated third-party payer must be read from state, or the bound gates the wrong account.
        UInt256 balance = payer == tx.SenderAddress
            ? state.SenderAccount.Balance
            : stateProvider.TryGetAccount(payer, out AccountStruct payerAccount) ? payerAccount.Balance : UInt256.Zero;

        // A snapshot: AddCore settles the replacement later, under the pool lock. TryReserve ignores the
        // discount when the payer holds no reservation, so skip the bucket walk and its pool lock there.
        UInt256 replaced = exposure.GetReserved(payer).IsZero ? UInt256.Zero : ReplacedPendingReservation(tx, payer);
        if (!exposure.TryReserve(payer, maxCost, balance, out UInt256 reserved, replaced))
        {
            // Atomic: this filter runs under the pool's head read lock, so payers reject concurrently.
            Interlocked.Increment(ref Metrics.PendingTransactionsFrameTxPayerExposureExceeded);
            if (logger.IsTrace)
                logger.Trace($"Skipped adding frame transaction {tx.Hash}, payer {payer} reserved exposure {reserved} + {maxCost} exceeds balance {balance}.");
            return AcceptTxResult.FrameTxPayerExposureExceeded;
        }

        // Held so the release subtracts exactly this, whatever the transaction still carries by then.
        tx.PayerExposure = maxCost;
        return AcceptTxResult.Accepted;
    }

    /// <summary>The reservation <paramref name="tx"/> would displace, or zero when it joins the pending
    /// set instead.</summary>
    /// <remarks>Matched on the pool's own competing key, so an EIP-8250 same-nonce transaction in another
    /// domain is not discounted, and on the payer, since displacing another payer's tx frees that payer.
    /// Read from what the incumbent recorded at admission, so the discount cannot drift from what its removal
    /// releases.</remarks>
    private UInt256 ReplacedPendingReservation(Transaction tx, Address payer)
    {
        ReplacementSearch search = new(tx, payer);
        TxDistinctSortedPool pool = tx.CarriesBlobs ? blobPool : standardPool;
        pool.VisitBucket(tx.SenderAddress!, ref search, static (Transaction pending, ref ReplacementSearch state) =>
        {
            // Buckets are visited in ascending nonce order, so skip below and stop past the replaced nonce.
            if (pending.Nonce < state.Nonce) return true;
            if (pending.Nonce > state.Nonce) return false;

            if (CompetingTransactionEqualityComparer.Instance.Equals(state.Tx, pending)
                && pending.PayerAddress == state.Payer
                && pending.PayerExposure is { } cost)
            {
                state.Reserved = cost;
                return false;
            }

            // Same nonce, another domain: only one entry can compete, so keep looking for it.
            return true;
        });

        return search.Reserved;
    }

    private struct ReplacementSearch(Transaction tx, Address payer)
    {
        public readonly Transaction Tx = tx;
        public readonly ulong Nonce = tx.Nonce;
        public readonly Address Payer = payer;
        public UInt256 Reserved;
    }
}
