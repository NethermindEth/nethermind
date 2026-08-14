// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters;

/// <summary>
/// Resolves the fee-payer of an EIP-8141 frame transaction at admission, records it on
/// <see cref="Transaction.PayerAddress"/>, and rejects prefixes that can never approve a payer.
/// </summary>
internal sealed class FrameTxPayerFilter(IReadOnlyStateProvider stateProvider, ILogger logger) : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        if (!tx.SupportsFrames)
        {
            return AcceptTxResult.Accepted;
        }

        FrameTxPayerResolution resolution = FrameTxPayerResolver.Resolve(tx, stateProvider, state.SenderAccount);
        tx.PayerAddress = resolution.Payer;

        // NoPayer is structural: the prefix has no payment-approving frame, so it can never be included
        // — drop it rather than re-gossip. RequiresSimulation is deferred to execution, not rejected.
        if (resolution.Outcome == FrameTxPayerOutcome.NoPayer)
        {
            Metrics.PendingTransactionsFrameTxNoPayer++;
            if (logger.IsTrace) logger.Trace($"Skipped adding frame transaction {tx.Hash}, its validation prefix never approves a payer.");
            return AcceptTxResult.Invalid.WithMessage("Frame transaction never approves a payer");
        }

        if (logger.IsTrace) logger.Trace($"Resolved frame transaction {tx.Hash} payer: {resolution.Outcome} ({resolution.Payer?.ToString() ?? "none"}).");
        return AcceptTxResult.Accepted;
    }
}
