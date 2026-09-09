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
/// transaction alone, so nothing the ledger cannot see goes unsummed. A sponsored transaction is bounded against
/// both accounts, the sponsor owing the fee and the sender the wei its own frames move. A free transaction is
/// carved out of the zero-balance leg only, not of the bound: exempting the branch would skip the reservation
/// along with it.</remarks>
internal sealed class FrameTxPayerExposureFilter(
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
        // and the payer-solvency gate cannot drift. The spec pinned for the submission, not the head's:
        // a head that moves mid-pipeline would price this against rules AddCore never validated the pool for.
        IReleaseSpec spec = state.HeadSpec;
        if (!FrameTxValidation.TryCalculateMaxCost(tx, spec, out UInt256 maxCost))
        {
            // Unincludable rather than malformed: Invalid is the one result that disconnects the relaying peer.
            return AcceptTxResult.Int256Overflow.WithMessage("Frame transaction maximum cost cannot be priced");
        }

        Address? payer = tx.PayerAddress;
        UInt256 cost = maxCost;
        UInt256 balance;
        if (payer is null || payer == tx.SenderAddress)
        {
            // TXPARAM(0x06) prices gas alone, so wherever the sender is also what pays, the wei its own frames
            // move is added to the bound. Only the part no earlier frame could have funded: see the helper.
            if (UInt256.AddOverflow(maxCost, LeadingSenderFrameValue(tx), out cost))
            {
                return AcceptTxResult.Int256Overflow.WithMessage("Frame transaction maximum cost cannot be priced");
            }

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
                return RejectUnderfundedSender(tx, pending, cost, sender.Balance, txHandlingOptions);
            }

            balance = sender.Balance - pending;
            if (payer is null)
            {
                // Nothing to reserve against, so the sender bound is the whole gate. The price is still recorded:
                // it is what the bound above sums this transaction at once it is one of the pending ones.
                if (cost > balance) return RejectUnderfundedSender(tx, pending, cost, sender.Balance, txHandlingOptions);

                tx.PayerExposure = cost;
                return AcceptTxResult.Accepted;
            }
        }
        else
        {
            // A simulated third-party payer must be read from state, or the bound gates the wrong account.
            balance = stateProvider.TryGetAccount(payer, out AccountStruct payerAccount) ? payerAccount.Balance : UInt256.Zero;

            // A sponsor's reservation covers the fee alone, leaving the leading frame's wei owed by the sender
            // whoever pays: bounded against the sender here, as the branch above bounds it there.
            UInt256 senderValue = LeadingSenderFrameValue(tx);
            UInt256 senderBalance = state.SenderAccount.Balance;
            if (senderValue > senderBalance)
            {
                return RejectUnderfundedSender(tx, UInt256.Zero, senderValue, senderBalance, txHandlingOptions);
            }
        }

        // AddCore settles the replacement later. The discount is ignored with no reservation held, so skip the walk.
        Hash256? replaced = exposure.GetReserved(payer).IsZero ? null : PendingReplacement.Find(tx, standardPool, blobPool)?.Hash;
        if (!exposure.TryReserve(payer, tx.Hash!, cost, balance, out UInt256 reserved, replaced))
        {
            return payer == tx.SenderAddress
                ? RejectUnderfundedSender(tx, reserved, cost, balance, txHandlingOptions)
                : RejectOverExposed(tx, payer, reserved, cost, balance);
        }

        // Recorded for the restart-time restore; the ledger owns what a removal releases.
        tx.PayerExposure = cost;
        return AcceptTxResult.Accepted;
    }

    /// <summary>The wei <paramref name="tx"/>'s SENDER frames move that its sender must already hold.</summary>
    /// <remarks>Frames run in order, so the first frame able to credit the sender ends what a balance bounds:
    /// summing past it would refuse a transaction whose later frames are funded by its earlier ones. VERIFY and
    /// POST_TX frames are static, and the deploy frame EIP-8141's prologue admits — the only non-static frame a
    /// recognized validation prefix contains — moves no wei either: a non-SENDER frame's value must be zero, and
    /// the mempool trace rules bar the prefix from any value-carrying call, endowed create or SELFDESTRUCT. The
    /// first frame past that prologue is therefore the one this bounds; skipping it would leave the deploy-and-use
    /// layout, a sender's first transaction, outside every balance the pool applies. An underfunded SENDER frame
    /// reverts rather than invalidating the transaction, so the residue is pool quality.</remarks>
    private static UInt256 LeadingSenderFrameValue(Transaction tx)
    {
        TxFrame[] frames = tx.Frames!;
        for (int i = FrameTxValidation.ApprovalSearchStart(frames); i < frames.Length; i++)
        {
            if (frames[i].Mode is TxFrame.ModeVerify or TxFrame.ModePostTx) continue;
            return frames[i].Mode == TxFrame.ModeSender ? frames[i].Value : UInt256.Zero;
        }

        return UInt256.Zero;
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
