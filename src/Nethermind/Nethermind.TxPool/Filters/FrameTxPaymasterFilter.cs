// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters;

/// <summary>
/// Enforces the EIP-8141 public-mempool cap on how many pending frame transactions may pay through one
/// non-canonical paymaster.
/// </summary>
/// <remarks>
/// A paymaster sponsors many senders, so without a cap one sponsor's balance or code change could
/// invalidate an unbounded set of pending transactions. EIP-8141 "Non-canonical paymaster" bounds that
/// set to <see cref="Eip8141Constants.MaxPendingTxsUsingNonCanonicalPaymaster"/> per sponsor
/// (ethereum/EIPs#12007). Only a <c>pay</c> frame target that carries code is a paymaster: a default-code
/// sponsor is governed by the per-payer exposure rule alone (<see cref="FrameTxPayerExposureFilter"/>).
/// The check is a read of the pending count rather than a reservation, so it holds no state a later
/// rejecting filter would have to release; concurrent submissions naming one paymaster may briefly
/// exceed the cap, as concurrent delegations may for <see cref="DelegatedAccountFilter"/>.
/// </remarks>
internal sealed class FrameTxPaymasterFilter(
    IReadOnlyStateProvider stateProvider,
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
        if (paymaster is null || !IsNonCanonicalPaymaster(paymaster))
        {
            return AcceptTxResult.Accepted;
        }

        int pending = paymasters.GetPendingCount(paymaster);
        if (pending >= Eip8141Constants.MaxPendingTxsUsingNonCanonicalPaymaster)
        {
            Metrics.PendingTransactionsFrameTxPaymasterLimitReached++;
            if (logger.IsTrace)
                logger.Trace($"Skipped adding frame transaction {tx.Hash}, non-canonical paymaster {paymaster} already sponsors {pending} pending transactions.");
            return AcceptTxResult.NonCanonicalPaymasterLimitReached;
        }

        return AcceptTxResult.Accepted;
    }

    // No canonical paymaster runtime is pinned in production yet, so every code-carrying pay target is
    // capped. Exempting a canonical instance additionally requires its balance reservation (EIP8141-GAP).
    private bool IsNonCanonicalPaymaster(Address paymaster) =>
        stateProvider.TryGetAccount(paymaster, out AccountStruct account) && account.HasCode;
}
