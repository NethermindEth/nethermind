// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters;

/// <summary>
/// Admits the opaque EIP-8141 frame transactions the native resolver defers
/// (<see cref="FrameTxPayerOutcome.RequiresSimulation"/>) by simulating their validation prefix in a
/// bounded, read-only EVM, and rejects those whose prefix does not validate.
/// </summary>
/// <remarks>
/// Runs after <see cref="FrameTxPayerFilter"/>, so a natively-resolved payer keeps the EVM-free fast
/// path and never reaches the simulator. Only a <see cref="FrameTxPayerOutcome.RequiresSimulation"/>
/// outcome is simulated, and only when a simulator is wired; a successful simulation records the payer
/// the exposure gate downstream reads. https://eips.ethereum.org/EIPS/eip-8141
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

        // A null payer is either a provably-invalid legible prefix (NoPayer) or an opaque one that
        // needs simulation; only the latter is simulated. Re-resolving is cheap (native, ≤2 reads)
        // and reached only for the rare unresolved frame tx, not the common resolved fast path.
        if (FrameTxPayerResolver.Resolve(tx, stateProvider, state.SenderAccount).Outcome != FrameTxPayerOutcome.RequiresSimulation)
        {
            return AcceptTxResult.Accepted;
        }

        FrameTxSimulationResult result = simulator.Simulate(tx);
        if (!result.Accepted)
        {
            if (logger.IsTrace) logger.Trace($"Skipped adding frame transaction {tx.Hash}, validation-prefix simulation rejected it: {result.RejectionReason}.");
            // An admission bound the node spent on itself is not the sending peer's fault.
            return (result.Indeterminate ? AcceptTxResult.FrameSimulationDeferred : AcceptTxResult.FrameSimulationFailed)
                .WithMessage(result.RejectionReason ?? TxPoolErrorMessages.FrameSimulationFailed);
        }

        tx.PayerAddress = result.Payer;
        if (logger.IsTrace) logger.Trace($"Simulated frame transaction {tx.Hash} validation prefix; resolved payer {result.Payer}.");
        return AcceptTxResult.Accepted;
    }
}
