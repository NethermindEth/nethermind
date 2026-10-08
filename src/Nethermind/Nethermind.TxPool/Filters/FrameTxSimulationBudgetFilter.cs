// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters;

/// <summary>Defers a gossiped EIP-8141 frame transaction that needs simulation once this head's simulation budget is
/// spent, before its signatures are verified.</summary>
/// <remarks>Without it, <see cref="FrameTxSignatureFilter"/> verifies every signature and only then does
/// <see cref="FrameTxSimulationFilter"/> learn the budget is gone and defer: the recoveries are wasted, and since a
/// deferral is this node's own load it charges no peer, so a flood that spends the budget buys them for free.
/// Must run before <see cref="FrameTxSignatureFilter"/> and after the sender is recovered. Reads the simulator's
/// lock-free hint, so a head transition can let one through that the simulation filter then defers, or defer one the
/// next head would admit; a local submission is exempt, as it is from the budget itself.</remarks>
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
