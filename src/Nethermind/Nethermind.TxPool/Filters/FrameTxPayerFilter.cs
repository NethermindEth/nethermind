// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters;

/// <summary>
/// Resolves the fee-payer of an EIP-8141 frame transaction at admission and records it on the
/// transaction (<see cref="Transaction.PayerAddress"/>) for downstream mempool policy.
/// </summary>
/// <remarks>
/// Annotation step, not a gate: it always accepts and never rejects. Rejection on the resolved
/// payer (per-payer exposure, paymaster reservation, and the other public-mempool DoS rules) is a
/// deferred follow-up (ethereum/EIPs#12007). Frame txs reach the pool only under the active fork,
/// gated earlier by <see cref="NotSupportedTxFilter"/>.
/// https://eips.ethereum.org/EIPS/eip-8141
/// </remarks>
internal sealed class FrameTxPayerFilter(IReadOnlyStateProvider stateProvider, ILogger logger) : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        if (!tx.SupportsFrames)
        {
            return AcceptTxResult.Accepted;
        }

        FrameTxPayerResolution resolution = FrameTxPayerResolver.Resolve(tx, stateProvider);
        tx.PayerAddress = resolution.Payer;

        if (logger.IsTrace) logger.Trace($"Resolved frame transaction {tx.Hash} payer: {resolution.Outcome} ({resolution.Payer?.ToString() ?? "none"}).");

        return AcceptTxResult.Accepted;
    }
}
