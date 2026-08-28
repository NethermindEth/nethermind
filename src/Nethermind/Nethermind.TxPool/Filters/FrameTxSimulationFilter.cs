// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters;

/// <summary>Simulates the validation prefix of opaque EIP-8141 frame transactions
/// (<see cref="FrameTxPayerOutcome.RequiresSimulation"/>), rejecting those that do not validate.</summary>
/// <remarks>Must run after <see cref="FrameTxPayerFilter"/>, whose resolved payer is the EVM-free fast
/// path here, and after <see cref="FrameTxVerifyGasFilter"/>, which is what bounds the state gas a deploy
/// frame may spend here — with no simulator-side floor under it, unlike execution gas, so zeroing
/// <see cref="ITxPoolConfig.FrameTxMaxVerifyStateGas"/> lifts that bound entirely. Runs inside the pool's
/// head read lock, so the simulator has to bound its own wait.
/// The simulation re-verifies the frame signatures unless <see cref="FrameTxSignatureFilter"/> already has.</remarks>
internal sealed class FrameTxSimulationFilter(IFrameTxPrefixSimulator? simulator, ILogger logger) : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        // Fast path: legible prefixes resolved natively (payer already set) never reach the EVM.
        if (!tx.SupportsFrames || tx.PayerAddress is not null || simulator is null)
        {
            return AcceptTxResult.Accepted;
        }

        // An unresolved payer is either provably invalid (NoPayer) or opaque; only the latter is simulated.
        if (FrameTxPayerResolver.Resolve(tx, state.SenderAccount).Outcome != FrameTxPayerOutcome.RequiresSimulation)
        {
            return AcceptTxResult.Accepted;
        }

        FrameTxSimulationResult result = simulator.Simulate(tx, signaturesPreValidated: state.FrameSignaturesVerified);
        switch (result.Outcome)
        {
            case FrameTxSimulationOutcome.Rejected:
                // Atomic: this filter runs under the pool's head read lock, so submissions land concurrently.
                Interlocked.Increment(ref Metrics.PendingTransactionsFrameTxSimulationFailed);
                if (logger.IsTrace) logger.Trace($"Skipped adding frame transaction {tx.Hash}, validation-prefix simulation rejected it: {result.Reason}.");
                return AcceptTxResult.FrameSimulationFailed.WithMessage(result.Reason ?? TxPoolErrorMessages.FrameSimulationFailed);

            case FrameTxSimulationOutcome.Undecided:
                // No verdict, so defer as an unwired simulator does rather than charge the sending peer for it.
                Interlocked.Increment(ref Metrics.PendingTransactionsFrameTxSimulationUndecided);
                if (logger.IsDebug) logger.Debug($"Admitting frame transaction {tx.Hash} with an unresolved payer, validation-prefix simulation was unavailable: {result.Reason}.");
                return AcceptTxResult.Accepted;

            case FrameTxSimulationOutcome.Accepted:
                tx.PayerAddress = result.Payer;
                if (logger.IsTrace) logger.Trace($"Simulated frame transaction {tx.Hash} validation prefix; resolved payer {result.Payer}.");
                return AcceptTxResult.Accepted;

            default:
                // Only Accepted carries a payer, so an outcome this filter cannot price must not record one.
                Interlocked.Increment(ref Metrics.PendingTransactionsFrameTxSimulationUndecided);
                if (logger.IsDebug) logger.Debug($"Admitting frame transaction {tx.Hash} with an unresolved payer, unhandled simulation outcome {result.Outcome}.");
                return AcceptTxResult.Accepted;
        }
    }
}
