// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool.Collections;

namespace Nethermind.TxPool.Filters;

/// <summary>Rejects a frame transaction whose payer's summed pending maximum cost would exceed its balance (EIP-8141).</summary>
/// <remarks>The reservation is taken at admission and released when the transaction leaves the pool.</remarks>
internal sealed class FrameTxPayerExposureFilter(
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

        // EIP8141-DEVIATION: GasLimit is the frame-gas sum, so the intrinsic and EIP-7623 floor terms go
        // unreserved — nothing at all, and so no bound, when every frame gas limit is zero.
        if (tx.IsOverflowInTxCostAndValue(out UInt256 maxCost))
        {
            return AcceptTxResult.Int256Overflow;
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

        return AcceptTxResult.Accepted;
    }

    /// <summary>The reservation <paramref name="tx"/> would displace, or zero when it joins the pending
    /// set instead.</summary>
    /// <remarks>Matched on the payer too, since displacing another payer's tx frees that payer.</remarks>
    private UInt256 ReplacedPendingReservation(Transaction tx, Address payer) =>
        PendingReplacement.Find(tx, standardPool, blobPool) is Transaction replaced
        && replaced.PayerAddress == payer
        && !replaced.IsOverflowInTxCostAndValue(out UInt256 cost)
            ? cost
            : UInt256.Zero;
}
