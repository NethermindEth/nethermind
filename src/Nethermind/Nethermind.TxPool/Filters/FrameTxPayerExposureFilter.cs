// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
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
/// The reservation is taken atomically at admission and released when the transaction leaves the pool,
/// so concurrent submissions for one payer cannot each pass a stale check.
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

        // EIP8141-DEVIATION: TXPARAM(0x06) is defined precisely, but Transaction.GasLimit is the frame-gas
        // sum only, so the intrinsic and EIP-7623 floor terms go unreserved; for a calldata-heavy prefix
        // with small frame gas limits the reserved amount can be a small fraction of the true max cost.
        if (tx.IsOverflowInTxCostAndValue(out UInt256 maxCost))
        {
            return AcceptTxResult.Int256Overflow;
        }

        // Keyed on the payer actually being the sender, never assumed: a simulated third-party payer must
        // still be read from state, or the bound would be enforced against the wrong account.
        UInt256 balance = payer == tx.SenderAddress
            ? state.SenderAccount.Balance
            : stateProvider.TryGetAccount(payer, out AccountStruct payerAccount) ? payerAccount.Balance : UInt256.Zero;

        if (!exposure.TryReserve(payer, maxCost, balance, out UInt256 reserved))
        {
            // Atomic like the gauge: the two are only diagnosable together, and this filter runs under
            // the pool's head read lock, so rejections for different payers are concurrent.
            Interlocked.Increment(ref Metrics.PendingTransactionsFrameTxPayerExposureExceeded);
            if (logger.IsTrace)
                logger.Trace($"Skipped adding frame transaction {tx.Hash}, payer {payer} reserved exposure {reserved} + {maxCost} exceeds balance {balance}.");
            return AcceptTxResult.FrameTxPayerExposureExceeded;
        }

        return AcceptTxResult.Accepted;
    }
}
