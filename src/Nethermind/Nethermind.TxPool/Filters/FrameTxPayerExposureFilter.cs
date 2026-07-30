// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters;

/// <summary>
/// Enforces EIP-8141 per-payer mempool exposure: rejects a frame transaction when its resolved
/// payer's summed pending maximum cost would exceed the payer's balance.
/// </summary>
/// <remarks>
/// Runs after <see cref="FrameTxPayerFilter"/> has recorded <see cref="Transaction.PayerAddress"/>.
/// Only natively-resolved payers are gated; unresolved frame txs (payer <c>null</c>) and non-frame
/// txs pass through. The reservation is taken here atomically at admission and released when the
/// transaction leaves the pool (or fails to insert), so concurrent submissions for one payer cannot
/// each pass a stale check (ethereum/EIPs#12007, "a node MUST NOT hold pending frame transactions
/// whose summed maximum costs exceed the payer's balance").
/// https://eips.ethereum.org/EIPS/eip-8141
/// </remarks>
internal sealed class FrameTxPayerExposureFilter(
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

        // Max cost approximates TXPARAM(0x06). For a frame tx this is gas-only (MaxFeePerGas·GasLimit):
        // the top-level Value is always zero (frame value lives on TxFrame.Value) and blob fields are
        // rejected at validation while frame-blob support is off. The signature-verification add-on
        // lands with the deferred MAX_VERIFY_GAS slice.
        if (tx.IsOverflowInTxCostAndValue(out UInt256 maxCost))
        {
            return AcceptTxResult.Int256Overflow;
        }

        UInt256 balance = stateProvider.TryGetAccount(payer, out AccountStruct payerAccount) ? payerAccount.Balance : UInt256.Zero;

        // Reserve atomically so N concurrent submissions for the same payer cannot each observe a
        // pre-reservation total and all pass. Released on the pool Removed event, or on the
        // non-insert path in TxPool.AddCore.
        if (!exposure.TryReserve(payer, maxCost, balance, out UInt256 reserved))
        {
            if (logger.IsTrace)
                logger.Trace($"Skipped adding frame transaction {tx.Hash}, payer {payer} reserved exposure {reserved} + {maxCost} exceeds balance {balance}.");
            return AcceptTxResult.PayerExposureExceeded;
        }

        return AcceptTxResult.Accepted;
    }
}
