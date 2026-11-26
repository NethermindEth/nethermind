// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters;

/// <summary>Defers a gossiped EIP-8141 frame transaction that needs simulation once this head's simulation budget is
/// spent, before its signatures are verified.</summary>
/// <remarks>Runs after sender recovery and before <see cref="FrameTxSignatureFilter"/> to avoid signature recoveries
/// that a spent head budget would waste. The budget hint is advisory: a head transition may let through a transaction
/// that simulation then defers, or defer one the next head would admit. Local submissions are exempt.</remarks>
internal sealed class FrameTxSimulationBudgetFilter(IFrameTxPrefixSimulator? simulator, ILogger logger) : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        if (simulator is null || !tx.SupportsFrames || (txHandlingOptions & TxHandlingOptions.PersistentBroadcast) != 0)
        {
            return AcceptTxResult.Accepted;
        }

        // Cheapest test first: under spam nearly every arrival finds the budget spent, and the rest skip the resolver.
        if (!simulator.IsHeadBudgetSpent
            || FrameTxPayerResolver.Resolve(tx, state.SenderAccount).Outcome != FrameTxPayerOutcome.RequiresSimulation)
        {
            return AcceptTxResult.Accepted;
        }

        Interlocked.Increment(ref Metrics.FrameTxSimulationsBudgetExhausted);
        Interlocked.Increment(ref Metrics.PendingTransactionsFrameTxSimulationDeferred);
        if (logger.IsTrace) logger.Trace($"Deferred frame transaction {tx.Hash} before signature verification, this head's validation-prefix simulation budget is spent.");
        return AcceptTxResult.FrameSimulationDeferred.WithMessage(TxPoolErrorMessages.FrameSimulationBudgetSpent);
    }
}
