// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.TxPool.Collections;

namespace Nethermind.TxPool.Filters;

/// <summary>
/// Enforces the EIP-8141 public-mempool cap on how many pending frame transactions may pay through one
/// non-canonical paymaster.
/// </summary>
/// <remarks>Counting is the reservation: the slot is taken before the cap is judged, so two concurrent
/// submissions cannot both read it free. The pool releases it again whenever the transaction does not
/// end up pending.</remarks>
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
        if (paymaster is null)
        {
            return AcceptTxResult.Accepted;
        }

        // Taken before the verdict, so a concurrent submission naming this paymaster cannot also see the
        // slot free; the pool releases it on every path that leaves the transaction unpooled.
        int held = paymasters.Reserve(paymaster);
        state.PaymasterReserved = true;

        // Both remaining tests are deferred until the count could bite: below the cap neither the target's
        // code nor a replacement it displaces can change the verdict, so neither is paid for.
        if (held > Eip8141Constants.MaxPendingTxsUsingNonCanonicalPaymaster
            && IsNonCanonicalPaymaster(paymaster)
            && !ReplacesPendingTxOfSamePaymaster(tx, paymaster))
        {
            paymasters.Decrement(paymaster);
            state.PaymasterReserved = false;
            Interlocked.Increment(ref Metrics.PendingTransactionsFrameTxPaymasterLimitReached);
            if (logger.IsTrace)
                logger.Trace($"Skipped adding frame transaction {tx.Hash}, non-canonical paymaster {paymaster} already sponsors {held - 1} pending transactions.");
            return AcceptTxResult.NonCanonicalPaymasterLimitReached;
        }

        return AcceptTxResult.Accepted;
    }

    // EIP8141-GAP: no canonical paymaster runtime is pinned yet, so every code-carrying pay target is capped.
    private bool IsNonCanonicalPaymaster(Address paymaster) =>
        stateProvider.TryGetAccount(paymaster, out AccountStruct account) && account.HasCode;

    /// <remarks>Matched on the paymaster too: displacing a tx sponsored elsewhere frees that sponsor's
    /// slot while still taking one here.</remarks>
    private bool ReplacesPendingTxOfSamePaymaster(Transaction tx, Address paymaster) =>
        PendingReplacement.Find(tx, standardPool, blobPool) is Transaction replaced
        && paymaster == FrameTxValidation.GetPrefixPaymaster(replaced);
}
