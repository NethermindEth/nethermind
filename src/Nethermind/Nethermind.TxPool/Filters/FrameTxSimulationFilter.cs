// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters;

/// <summary>
/// Simulates the validation prefix of the opaque EIP-8141 frame transactions the native resolver defers
/// (<see cref="FrameTxPayerOutcome.RequiresSimulation"/>), rejecting those whose prefix does not validate.
/// </summary>
/// <remarks>
/// Must run after <see cref="FrameTxPayerFilter"/>, whose resolved payer is the EVM-free fast path here.
/// Runs inside the pool's head read lock, so the simulator has to bound its own wait.
/// </remarks>
internal sealed class FrameTxSimulationFilter(
    IReadOnlyStateProvider stateProvider,
    IFrameTxPrefixSimulator? simulator,
    ILogger logger) : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        // Fast path: legible prefixes resolved natively (payer already set) never reach the EVM.
        if (!tx.SupportsFrames || tx.PayerAddress is not null || simulator is null)
        {
            return AcceptTxResult.Accepted;
        }

        // An unresolved payer is either provably invalid (NoPayer) or opaque; only the latter is simulated.
        if (FrameTxPayerResolver.Resolve(tx, stateProvider, state.SenderAccount).Outcome != FrameTxPayerOutcome.RequiresSimulation)
        {
            return AcceptTxResult.Accepted;
        }

        FrameTxSimulationResult result = simulator.Simulate(tx, local: (txHandlingOptions & TxHandlingOptions.PersistentBroadcast) != 0);
        if (!result.Accepted)
        {
            Metrics.PendingTransactionsFrameTxSimulationFailed++;
            if (logger.IsTrace) logger.Trace($"Skipped adding frame transaction {tx.Hash}, validation-prefix simulation rejected it: {result.RejectionReason}.");
            // Deferred only for a bound this node spent on itself. A timeout is retained by revalidation
            // (Indeterminate) yet chargeable here, because the prefix's own wall clock is what tripped it.
            return (result.NodeBound ? AcceptTxResult.FrameSimulationDeferred : AcceptTxResult.FrameSimulationFailed)
                .WithMessage(result.RejectionReason ?? TxPoolErrorMessages.FrameSimulationFailed);
        }

        tx.PayerAddress = result.Payer;
        if (logger.IsTrace) logger.Trace($"Simulated frame transaction {tx.Hash} validation prefix; resolved payer {result.Payer}.");
        return AcceptTxResult.Accepted;
    }
}
