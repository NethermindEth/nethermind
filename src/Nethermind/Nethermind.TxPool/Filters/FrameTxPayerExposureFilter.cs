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
/// txs pass through. Reservation is accounted from the pool insert/remove events into
/// <see cref="PayerExposureCache"/> (ethereum/EIPs#12007, "a node MUST NOT hold pending frame
/// transactions whose summed maximum costs exceed the payer's balance").
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

        // Max cost approximates TXPARAM(0x06); the signature-verification add-on lands with the
        // deferred MAX_VERIFY_GAS slice (see MEMPOOL-RULES-DESIGN.md).
        if (tx.IsOverflowInTxCostAndValue(out UInt256 maxCost))
        {
            return AcceptTxResult.Int256Overflow;
        }

        UInt256 balance = stateProvider.TryGetAccount(payer, out AccountStruct payerAccount) ? payerAccount.Balance : UInt256.Zero;
        UInt256 reserved = exposure.GetReserved(payer);

        if (UInt256.AddOverflow(reserved, maxCost, out UInt256 required) || required > balance)
        {
            if (logger.IsTrace)
                logger.Trace($"Skipped adding frame transaction {tx.Hash}, payer {payer} exposure {reserved} + {maxCost} exceeds balance {balance}.");
            return AcceptTxResult.PayerExposureExceeded;
        }

        return AcceptTxResult.Accepted;
    }
}
