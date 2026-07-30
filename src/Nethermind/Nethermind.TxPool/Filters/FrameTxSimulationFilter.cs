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
/// Runs after <see cref="FrameTxPayerFilter"/>. The standard, natively-resolvable prefixes keep the
/// EVM-free fast path: a transaction whose payer was already resolved (non-null
/// <see cref="Transaction.PayerAddress"/>) returns immediately without re-resolving or simulating, so
/// the simulator is never consulted for it. Only a frame tx with an unresolved payer is re-classified
/// here; a <see cref="FrameTxPayerOutcome.RequiresSimulation"/> outcome is simulated when a simulator
/// is wired, and on failure the transaction is rejected. When no simulator is wired, or the outcome is
/// the provably-invalid <see cref="FrameTxPayerOutcome.NoPayer"/>, the transaction passes through
/// unchanged (Phase-1 behavior). A successful simulation records the resolved payer, feeding the
/// exposure gate downstream. https://eips.ethereum.org/EIPS/eip-8141 (ethereum/EIPs#12007)
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
        if (FrameTxPayerResolver.Resolve(tx, stateProvider).Outcome != FrameTxPayerOutcome.RequiresSimulation)
        {
            return AcceptTxResult.Accepted;
        }

        FrameTxSimulationResult result = simulator.Simulate(tx);
        if (!result.Accepted)
        {
            if (logger.IsTrace) logger.Trace($"Skipped adding frame transaction {tx.Hash}, validation-prefix simulation rejected it: {result.RejectionReason}.");
            return AcceptTxResult.FrameSimulationFailed.WithMessage(result.RejectionReason ?? TxPoolErrorMessages.FrameSimulationFailed);
        }

        tx.PayerAddress = result.Payer;
        if (logger.IsTrace) logger.Trace($"Simulated frame transaction {tx.Hash} validation prefix; resolved payer {result.Payer}.");
        return AcceptTxResult.Accepted;
    }
}
