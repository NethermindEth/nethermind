// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.TxPool.Collections;
using Nethermind.TxPool.Comparison;

namespace Nethermind.TxPool.Filters;

/// <summary>
/// Enforces the EIP-8141 public-mempool cap on how many pending frame transactions may pay through one
/// non-canonical paymaster.
/// </summary>
/// <remarks>Reads the pending count rather than reserving, so a later filter's rejection needs no release,
/// at the cost of concurrent submissions naming one paymaster briefly exceeding the cap.</remarks>
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
        // (EIP-8141 decrements on "eviction, replacement, inclusion, or reorg removal"). Below the cap the
        // discount cannot change the verdict, so skip the bucket walk and its pool lock there.
        int held = paymasters.GetPendingCount(paymaster);
        int pending = held < Eip8141Constants.MaxPendingTxsUsingNonCanonicalPaymaster
            || !ReplacesPendingTxOfSamePaymaster(tx, paymaster)
            ? held
            : held - 1;
        if (pending >= Eip8141Constants.MaxPendingTxsUsingNonCanonicalPaymaster)
        {
            // Atomic: this filter runs under the pool's head read lock, so paymasters reject concurrently.
            Interlocked.Increment(ref Metrics.PendingTransactionsFrameTxPaymasterLimitReached);
            if (logger.IsTrace)
                logger.Trace($"Skipped adding frame transaction {tx.Hash}, non-canonical paymaster {paymaster} already sponsors {held} pending transactions.");
            return AcceptTxResult.NonCanonicalPaymasterLimitReached;
        }

        return AcceptTxResult.Accepted;
    }

    // EIP8141-GAP: no canonical paymaster runtime is pinned yet, so every code-carrying pay target is capped.
    private bool IsNonCanonicalPaymaster(Address paymaster) =>
        stateProvider.TryGetAccount(paymaster, out AccountStruct account) && account.HasCode;

    /// <summary>
    /// Whether the pending transaction <paramref name="tx"/> would displace pays through
    /// <paramref name="paymaster"/>, so admitting it does not grow that paymaster's pending count.
    /// </summary>
    /// <remarks>
    /// Tested with the pool's own competing key, so the EIP-8250 nonce-key domain is part of the match: a
    /// same-nonce transaction in another domain joins the pending set and must not be discounted.
    /// Matches on the paymaster too: replacing a tx sponsored elsewhere frees that sponsor's slot
    /// while still taking one here.
    /// </remarks>
    private bool ReplacesPendingTxOfSamePaymaster(Transaction tx, Address paymaster)
    {
        ReplacementSearch search = new(tx, paymaster);
        TxDistinctSortedPool pool = tx.CarriesBlobs ? blobPool : standardPool;
        pool.VisitBucket(tx.SenderAddress!, ref search, static (Transaction pending, ref ReplacementSearch state) =>
        {
            // Buckets are visited in ascending nonce order, so skip below and stop past the replaced nonce.
            if (pending.Nonce < state.Nonce) return true;
            if (pending.Nonce > state.Nonce) return false;

            if (CompetingTransactionEqualityComparer.Instance.Equals(state.Tx, pending))
            {
                state.Found = state.Paymaster == FrameTxValidation.GetPrefixPaymaster(pending);
                return false;
            }

            // Same nonce, another domain: only one entry can compete, so keep looking for it.
            return true;
        });

        return search.Found;
    }

    private struct ReplacementSearch(Transaction tx, Address paymaster)
    {
        public readonly Transaction Tx = tx;
        public readonly ulong Nonce = tx.Nonce;
        public readonly Address Paymaster = paymaster;
        public bool Found;
    }
}
