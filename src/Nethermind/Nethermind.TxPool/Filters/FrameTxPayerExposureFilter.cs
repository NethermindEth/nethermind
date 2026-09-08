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
/// <remarks>The reservation is taken at admission and released when the transaction leaves the pool. This is the
/// only affordability gate a frame transaction meets: the sender-balance filters skip them, since their fees are
/// the payer's liability. A prefix no payer resolved for reserves nothing, so it is gated against the sender
/// instead of going unpriced.</remarks>
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
        if (!tx.SupportsFrames)
        {
            return AcceptTxResult.Accepted;
        }

        // The upper-bound TXPARAM(0x06), priced with the processor's helper so the admission bound
        // and the payer-solvency gate cannot drift.
        IReleaseSpec spec = specProvider.GetCurrentHeadSpec();
        if (!FrameTxValidation.TryCalculateMaxCost(tx, spec, out UInt256 maxCost))
        {
            // Unincludable rather than malformed: Invalid is the one result that disconnects the relaying peer.
            return AcceptTxResult.Int256Overflow.WithMessage("Frame transaction maximum cost cannot be priced");
        }

        Address? payer = tx.PayerAddress;
        if (payer is null)
        {
            // Nothing to reserve against, so gate the sender: unproven sponsorship must not buy a pool slot
            // no account was priced for.
            UInt256 senderBalance = state.SenderAccount.Balance;
            return maxCost <= senderBalance
                ? AcceptTxResult.Accepted
                : RejectOverExposed(tx, tx.SenderAddress!, UInt256.Zero, maxCost, senderBalance);
        }

        // A simulated third-party payer must be read from state, or the bound gates the wrong account.
        UInt256 balance = payer == tx.SenderAddress
            ? state.SenderAccount.Balance
            : stateProvider.TryGetAccount(payer, out AccountStruct payerAccount) ? payerAccount.Balance : UInt256.Zero;

        // A snapshot; AddCore settles the replacement later. The discount is ignored with no reservation held, so skip the walk.
        UInt256 replaced = exposure.GetReserved(payer).IsZero ? UInt256.Zero : ReplacedPendingReservation(tx, payer);
        if (!exposure.TryReserve(payer, maxCost, balance, out UInt256 reserved, replaced))
        {
            return RejectOverExposed(tx, payer, reserved, maxCost, balance);
        }

        // Held so the release subtracts exactly this, whatever the transaction still carries by then.
        tx.PayerExposure = maxCost;
        return AcceptTxResult.Accepted;
    }

    /// <remarks>Atomic: this filter runs under the pool's head read lock, so payers reject concurrently.</remarks>
    private AcceptTxResult RejectOverExposed(Transaction tx, Address account, in UInt256 reserved, in UInt256 maxCost, in UInt256 balance)
    {
        Interlocked.Increment(ref Metrics.PendingTransactionsFrameTxPayerExposureExceeded);
        if (logger.IsTrace)
            logger.Trace($"Skipped adding frame transaction {tx.Hash}, account {account} reserved exposure {reserved} + {maxCost} exceeds balance {balance}.");
        return AcceptTxResult.FrameTxPayerExposureExceeded;
    }

    /// <summary>The reservation <paramref name="tx"/> would displace, or zero when it joins the pending
    /// set instead.</summary>
    /// <remarks>Matched on the pool's own competing key, so an EIP-8250 same-nonce transaction in another
    /// domain is not discounted, and on the payer, since displacing another payer's tx frees that payer.
    /// Read from what the incumbent recorded at admission, so the discount cannot drift from what its removal
    /// releases.</remarks>
    private UInt256 ReplacedPendingReservation(Transaction tx, Address payer) =>
        PendingReplacement.Find(tx, standardPool, blobPool) is Transaction replaced
        && replaced.PayerAddress == payer
        && replaced.PayerExposure is { } cost
            ? cost
            : UInt256.Zero;
}
