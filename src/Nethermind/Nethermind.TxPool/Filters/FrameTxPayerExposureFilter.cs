// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool.Collections;

namespace Nethermind.TxPool.Filters;

/// <summary>Rejects a frame transaction whose payer's summed pending maximum cost would exceed its balance (EIP-8141).</summary>
/// <remarks>The reservation is taken at admission and released when the transaction leaves the pool. This is the
/// only affordability gate a frame transaction meets: the sender-balance filters skip them, since their fees are
/// the payer's liability. Whenever the sender is what pays — its own prefix, or one no payer resolved for — the
/// bound the sibling filters held is taken here instead, over the sender's whole pending bucket rather than this
/// transaction alone, so nothing the ledger cannot see goes unsummed. A free transaction is carved out of the
/// zero-balance leg only, not of the bound: exempting the branch would skip the reservation along with it.</remarks>
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
        UInt256 balance;
        if (payer is null || payer == tx.SenderAddress)
        {
            AccountStruct sender = state.SenderAccount;
            // The sender-balance filters defer to this one, so their cumulative bound is taken here. Only what
            // the payer ledger does not already sum is counted, or a self-paid reservation would count twice.
            TxDistinctSortedPool pool = tx.CarriesBlobs ? blobPool : standardPool;
            if (tx.IsOverflowWhenSummingSenderBucket(pool, sender.Nonce, unreservedOnly: true, spec.IsEip8250Enabled, out UInt256 pending)
                // Reserving nothing, a payer-less transaction is the only one that has to read the other
                // half of that split itself; TryReserve sums it for the rest.
                || (payer is null && UInt256.AddOverflow(pending, SenderReservedAsPayer(tx), out pending)))
            {
                return AcceptTxResult.Int256Overflow.WithMessage("Frame transaction cumulative cost cannot be priced");
            }

            // The zero-balance leg is the BalanceZeroFilter backstop a frame transaction now skips: it has to
            // hold even at zero cost, or a zero-fee prefix buys a pool slot no account could have paid for.
            if (pending > sender.Balance || (sender.Balance.IsZero && !tx.IsFree()))
            {
                return RejectUnderfundedSender(tx, pending, maxCost, sender.Balance, txHandlingOptions);
            }

            balance = sender.Balance - pending;
            if (payer is null)
            {
                // Nothing to reserve against, so the sender bound is the whole gate. The price is still recorded:
                // it is what the bound above sums this transaction at once it is one of the pending ones.
                if (maxCost > balance) return RejectUnderfundedSender(tx, pending, maxCost, sender.Balance, txHandlingOptions);

                tx.PayerExposure = maxCost;
                return AcceptTxResult.Accepted;
            }
        }
        else
        {
            // A simulated third-party payer must be read from state, or the bound gates the wrong account.
            balance = stateProvider.TryGetAccount(payer, out AccountStruct payerAccount) ? payerAccount.Balance : UInt256.Zero;
        }

        // AddCore settles the replacement later. The discount is ignored with no reservation held, so skip the walk.
        Hash256? replaced = exposure.GetReserved(payer).IsZero ? null : PendingReplacement.Find(tx, standardPool, blobPool)?.Hash;
        if (!exposure.TryReserve(payer, tx.Hash!, maxCost, balance, out UInt256 reserved, replaced))
        {
            return payer == tx.SenderAddress
                ? RejectUnderfundedSender(tx, reserved, maxCost, balance, txHandlingOptions)
                : RejectOverExposed(tx, payer, reserved, maxCost, balance);
        }

        // Recorded for the restart-time restore; the ledger owns what a removal releases.
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

    /// <summary>The rejection the sender-balance filters would have returned, so a broke sender is not reported
    /// — to the caller or to the gauge — as a sponsor over-exposure.</summary>
    /// <remarks>The detail is composed for a local submission only, as the sibling filters do: a remote
    /// submitter never reads it, and an unfunded flood walks this path.</remarks>
    private AcceptTxResult RejectUnderfundedSender(Transaction tx, in UInt256 owed, in UInt256 maxCost, in UInt256 balance, TxHandlingOptions handlingOptions)
    {
        if (balance.IsZero) Metrics.PendingTransactionsZeroBalance++;
        else Metrics.PendingTransactionsTooLowBalance++;

        if (logger.IsTrace)
            logger.Trace($"Skipped adding frame transaction {tx.Hash}, sender {tx.SenderAddress} owes {owed} + {maxCost} against {balance} available.");
        return (handlingOptions & TxHandlingOptions.PersistentBroadcast) == 0
            ? AcceptTxResult.InsufficientFunds
            : AcceptTxResult.InsufficientFunds.WithMessage($"Account balance: {balance}, pending cost: {owed}, transaction cost: {maxCost}");
    }

    /// <summary>What the sender of <paramref name="tx"/> already owes as a payer, net of the reservation
    /// <paramref name="tx"/> displaces.</summary>
    /// <remarks>The bucket walk skips every transaction with a resolved payer, and this ledger covers that set —
    /// the sender's own self-paid prefixes among them. It carries no nonce, so it also counts reservations
    /// outside the walk's window and what the sender owes as another account's payer; both over-reject rather
    /// than admit. The ledger nets the displaced reservation off, on the same terms the reserving branch gets
    /// it: only while that reservation is still live, or a replacement would be discounted room already reused.</remarks>
    private UInt256 SenderReservedAsPayer(Transaction tx)
    {
        Address sender = tx.SenderAddress!;
        return exposure.GetReserved(sender).IsZero
            ? UInt256.Zero
            : exposure.GetReservedNetOf(sender, PendingReplacement.Find(tx, standardPool, blobPool)?.Hash);
    }
}
