// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool.Collections;

namespace Nethermind.TxPool.Filters;

/// <summary>
/// Enforces the EIP-8141 public-mempool cap on how many pending frame transactions may pay through one
/// non-canonical paymaster.
/// </summary>
/// <remarks>Counting is the reservation: the slot is taken before the cap is judged, so two concurrent
/// submissions cannot both read it free. The pool releases it again whenever the transaction does not
/// end up pending.
/// With <see cref="ITxPoolConfig.FrameTxWidthEnabled"/> the cap becomes the paymaster's free MATCHA baseline: a
/// transaction beyond it is admitted when the paymaster holds the width to pay for it. The baseline is the
/// transaction admitted while the paymaster sponsored none, and a replacement of it stays the baseline; once it
/// leaves, no other pending transaction takes its place until the paymaster sponsors none again. This filter runs
/// before the prefix simulation, so it only refuses a paymaster whose width cannot cover the charge and leaves the
/// spend to <see cref="FrameTxWidthFilter"/>, which runs after it.</remarks>
internal sealed class FrameTxPaymasterFilter(
    IReadOnlyStateProvider stateProvider,
    TxDistinctSortedPool standardPool,
    TxDistinctSortedPool blobPool,
    PendingPaymasterCache paymasters,
    ITxPoolConfig txPoolConfig,
    SenderWidthCache paymasterWidth,
    ConcurrentDictionary<AddressAsKey, ValueHash256> paymasterBaselines,
    ILogger logger) : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        if (PendingPaymasterCache.KeyFor(tx) is not Address paymaster)
        {
            return AcceptTxResult.Accepted;
        }

        // Taken before the verdict, so a concurrent submission naming this paymaster cannot also see the
        // slot free; the pool releases it on every path that leaves the transaction unpooled.
        int held = paymasters.Reserve(paymaster);
        state.PaymasterReserved = true;

        if (txPoolConfig.FrameTxWidthEnabled)
        {
            return AcceptByWidth(tx, ref state, paymaster, held);
        }

        // Both remaining tests are deferred until the count could bite: below the cap neither the target's
        // code nor a replacement it displaces can change the verdict, so neither is paid for.
        if (held > Eip8141Constants.MaxPendingTxsUsingNonCanonicalPaymaster
            && IsNonCanonicalPaymaster(paymaster, stateProvider)
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

    private AcceptTxResult AcceptByWidth(Transaction tx, ref TxFilteringState state, Address paymaster, int held)
    {
        if (held <= Eip8141Constants.MaxPendingTxsUsingNonCanonicalPaymaster
            || (PendingReplacement.Find(tx, standardPool, blobPool) is Transaction replaced
                && paymasterBaselines.TryGetValue(paymaster, out ValueHash256 baseline)
                && baseline == replaced.Hash!.ValueHash256))
        {
            state.TakesPaymasterBaseline = true;
            return AcceptTxResult.Accepted;
        }

        if (!IsNonCanonicalPaymaster(paymaster, stateProvider))
        {
            return AcceptTxResult.Accepted;
        }

        UInt256 charge = FrameTxWidthCharge.For(tx, state.HeadSpec, txPoolConfig.FrameTxWidthSafetyFactorPermille);
        if (paymasterWidth.GetWidth(paymaster) < charge)
        {
            Interlocked.Increment(ref Metrics.PendingTransactionsFrameTxPaymasterWidthUnmet);
            if (logger.IsTrace)
                logger.Trace($"Skipped adding frame transaction {tx.Hash}, paymaster {paymaster} holds {paymasterWidth.GetWidth(paymaster)} width against a cost of {charge} with {held - 1} pending.");
            return AcceptTxResult.PaymasterWidthUnmet;
        }

        state.BeyondPaymasterBaseline = true;
        return AcceptTxResult.Accepted;
    }

    // EIP8141-GAP: no canonical paymaster runtime is pinned yet, so every code-carrying pay target is capped.
    internal static bool IsNonCanonicalPaymaster(Address paymaster, IReadOnlyStateProvider stateProvider) =>
        stateProvider.TryGetAccount(paymaster, out AccountStruct account) && account.HasCode;

    /// <remarks>Matched on the paymaster too: displacing a tx sponsored elsewhere frees that sponsor's
    /// slot while still taking one here.</remarks>
    private bool ReplacesPendingTxOfSamePaymaster(Transaction tx, Address paymaster) =>
        PendingReplacement.Find(tx, standardPool, blobPool) is Transaction replaced
        && paymaster == PendingPaymasterCache.KeyFor(replaced);
}
