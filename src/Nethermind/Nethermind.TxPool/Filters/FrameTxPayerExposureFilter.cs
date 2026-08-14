// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool.Collections;

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
            return AcceptTxResult.Invalid.WithMessage("Frame transaction maximum cost cannot be priced");
        }

        // A simulated third-party payer must be read from state, or the bound gates the wrong account.
        UInt256 balance = payer == tx.SenderAddress
            ? state.SenderAccount.Balance
            : stateProvider.TryGetAccount(payer, out AccountStruct payerAccount) ? payerAccount.Balance : UInt256.Zero;

        // A snapshot: AddCore settles the replacement later, under the pool lock. TryReserve ignores the
        // discount when the payer holds no reservation, so skip the bucket walk and its pool lock there.
        UInt256 replaced = exposure.GetReserved(payer).IsZero ? UInt256.Zero : ReplacedPendingReservation(tx, payer, spec);
        if (!exposure.TryReserve(payer, maxCost, balance, out UInt256 reserved, replaced))
        {
            // Atomic: this filter runs under the pool's head read lock, so payers reject concurrently.
            Interlocked.Increment(ref Metrics.PendingTransactionsFrameTxPayerExposureExceeded);
            if (logger.IsTrace)
                logger.Trace($"Skipped adding frame transaction {tx.Hash}, payer {payer} reserved exposure {reserved} + {maxCost} exceeds balance {balance}.");
            return AcceptTxResult.FrameTxPayerExposureExceeded;
        }

        return AcceptTxResult.Accepted;
    }

    /// <summary>
    /// The reservation held by a pending transaction of the same sender and nonce that <paramref name="tx"/>
    /// would displace, or zero when it would join the pending set rather than replace one.
    /// </summary>
    /// <remarks>
    /// Matches on the payer too: displacing a tx paid by someone else frees that payer, not this one.
    /// Prices the displaced tx with the same helper the reservation used, or the discount under-refunds
    /// and the freed exposure leaks.
    /// </remarks>
    private UInt256 ReplacedPendingReservation(Transaction tx, Address payer, IReleaseSpec spec)
    {
        ReplacementSearch search = new(tx.Nonce, payer, spec);
        TxDistinctSortedPool pool = tx.CarriesBlobs ? blobPool : standardPool;
        pool.VisitBucket(tx.SenderAddress!, ref search, static (Transaction pending, ref ReplacementSearch state) =>
        {
            // Buckets are visited in ascending nonce order, so stop once past the replaced nonce.
            if (pending.Nonce < state.Nonce) return true;
            if (pending.Nonce == state.Nonce
                && pending.PayerAddress == state.Payer
                && FrameTxValidation.TryCalculateMaxCost(pending, state.Spec, out UInt256 cost))
            {
                state.Reserved = cost;
            }

            return false;
        });

        return search.Reserved;
    }

    private struct ReplacementSearch(ulong nonce, Address payer, IReleaseSpec spec)
    {
        public readonly ulong Nonce = nonce;
        public readonly Address Payer = payer;
        public readonly IReleaseSpec Spec = spec;
        public UInt256 Reserved;
    }
}
