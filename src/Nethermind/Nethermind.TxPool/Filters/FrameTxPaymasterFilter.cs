// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.TxPool.Collections;

namespace Nethermind.TxPool.Filters;

/// <summary>
/// Enforces the EIP-8141 public-mempool cap on how many pending frame transactions may pay through one
/// non-canonical paymaster.
/// </summary>
/// <remarks>
/// EIP-8141 "Non-canonical paymaster" bounds the set one sponsor's balance or code change can invalidate
/// to <see cref="Eip8141Constants.MaxPendingTxsUsingNonCanonicalPaymaster"/>. Only a code-carrying
/// <c>pay</c> target is a paymaster; a default-code sponsor is bounded by
/// <see cref="FrameTxPayerExposureFilter"/> alone. The check reads the pending count rather than
/// reserving, so nothing needs releasing when a later filter rejects, at the cost of concurrent
/// submissions naming one paymaster briefly exceeding the cap.
/// </remarks>
internal sealed class FrameTxPaymasterFilter(
    IReadOnlyStateProvider stateProvider,
    TxDistinctSortedPool standardPool,
    TxDistinctSortedPool blobPool,
    PendingPaymasterCache paymasters,
    ILogger logger) : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        if (!tx.SupportsFrames)
        {
            return AcceptTxResult.Accepted;
        }

        Address? paymaster = FrameTxValidation.GetPrefixPaymaster(tx);
        if (paymaster is null || !IsNonCanonicalPaymaster(paymaster))
        {
            return AcceptTxResult.Accepted;
        }

        // A replacement takes over the slot of the tx it displaces, so the pending set does not grow
        // (EIP-8141 decrements on "eviction, replacement, inclusion, or reorg removal").
        int pending = paymasters.GetPendingCount(paymaster) - (ReplacesPendingTxOfSamePaymaster(tx, paymaster) ? 1 : 0);
        if (pending >= Eip8141Constants.MaxPendingTxsUsingNonCanonicalPaymaster)
        {
            Metrics.PendingTransactionsFrameTxPaymasterLimitReached++;
            if (logger.IsTrace)
                logger.Trace($"Skipped adding frame transaction {tx.Hash}, non-canonical paymaster {paymaster} already sponsors {pending} pending transactions.");
            return AcceptTxResult.NonCanonicalPaymasterLimitReached;
        }

        return AcceptTxResult.Accepted;
    }

    // No canonical paymaster runtime is pinned in production yet, so every code-carrying pay target is
    // capped. Exempting a canonical instance additionally requires its balance reservation (EIP8141-GAP).
    private bool IsNonCanonicalPaymaster(Address paymaster) =>
        stateProvider.TryGetAccount(paymaster, out AccountStruct account) && account.HasCode;

    /// <summary>
    /// Whether a pending transaction from the same sender holds the same nonce and pays through
    /// <paramref name="paymaster"/>, so <paramref name="tx"/> would replace it rather than join it.
    /// </summary>
    /// <remarks>
    /// Matching on the paymaster too: replacing a tx sponsored elsewhere frees that sponsor's slot while
    /// still taking one here. A blob-carrying frame tx lives in the blob pool, so the displaced tx is
    /// looked for in whichever pool holds transactions of this shape.
    /// </remarks>
    private bool ReplacesPendingTxOfSamePaymaster(Transaction tx, Address paymaster)
    {
        ReplacementSearch search = new(tx.Nonce, paymaster);
        TxDistinctSortedPool pool = tx.CarriesBlobs ? blobPool : standardPool;
        pool.VisitBucket(tx.SenderAddress!, ref search, static (Transaction pending, ref ReplacementSearch state) =>
        {
            // Buckets are visited in ascending nonce order, so stop once past the replaced nonce.
            if (pending.Nonce < state.Nonce) return true;
            if (pending.Nonce == state.Nonce)
                state.Found = state.Paymaster == FrameTxValidation.GetPrefixPaymaster(pending);
            return false;
        });

        return search.Found;
    }

    private struct ReplacementSearch(ulong nonce, Address paymaster)
    {
        public readonly ulong Nonce = nonce;
        public readonly Address Paymaster = paymaster;
        public bool Found;
    }
}
