// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters;

/// <summary>
/// Enforces EIP-8141 per-payer mempool exposure: rejects a frame transaction when its resolved
/// payer's summed pending maximum cost would exceed the payer's balance.
/// </summary>
/// <remarks>
/// The reservation is taken atomically at admission and released when the transaction leaves the pool,
/// so concurrent submissions for one payer cannot each pass a stale check.
/// https://eips.ethereum.org/EIPS/eip-8141
/// </remarks>
internal sealed class FrameTxPayerExposureFilter(
    IChainHeadSpecProvider specProvider,
    IReadOnlyStateProvider stateProvider,
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
        if (!FrameTxValidation.TryCalculateMaxCost(tx, specProvider.GetCurrentHeadSpec(), out UInt256 maxCost))
        {
            return AcceptTxResult.Invalid.WithMessage("Frame transaction maximum cost cannot be priced");
        }

        // Keyed on the payer actually being the sender, never assumed: a simulated third-party payer must
        // still be read from state, or the bound would be enforced against the wrong account.
        UInt256 balance = payer == tx.SenderAddress
            ? state.SenderAccount.Balance
            : stateProvider.TryGetAccount(payer, out AccountStruct payerAccount) ? payerAccount.Balance : UInt256.Zero;

        if (!exposure.TryReserve(payer, maxCost, balance, out UInt256 reserved))
        {
            Metrics.PendingTransactionsFrameTxPayerExposureExceeded++;
            if (logger.IsTrace)
                logger.Trace($"Skipped adding frame transaction {tx.Hash}, payer {payer} reserved exposure {reserved} + {maxCost} exceeds balance {balance}.");
            return AcceptTxResult.FrameTxPayerExposureExceeded;
        }

        return AcceptTxResult.Accepted;
    }
}
