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
/// Annotates the resolved payer and rejects only the provably-invalid <see cref="FrameTxPayerOutcome.NoPayer"/>
/// prefixes; the broader payer-based rejection (per-payer exposure, paymaster reservation, and the
/// other public-mempool DoS rules) is a deferred follow-up (ethereum/EIPs#12007). Frame txs reach the
/// pool only under the active fork, gated earlier by <see cref="NotSupportedTxFilter"/>. Runs last in
/// the post-hash pipeline so only otherwise-admissible frame txs are resolved.
/// <para>
/// Reachability caveat: the earlier balance filters gate on the <em>sender</em> balance, so today only
/// the <c>self_verify</c> prefix (payer == sender, sender therefore funded) reaches this filter. A
/// sponsored / third-party-payer prefix from a zero-balance sender is rejected upstream by
/// <see cref="BalanceZeroFilter"/> / <see cref="BalanceTooLowFilter"/> before it arrives; making those
/// filters payer-aware (charge the resolved payer for <c>tx.SupportsFrames</c>) is a deferred follow-up.
/// That third-party-payer prefix is in any case not resolved natively yet — see
/// <see cref="FrameTxPayerResolver"/> — so both gaps are removed by the same signature-verification /
/// exposure follow-up.
/// </para>
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

        FrameTxPayerResolution resolution = FrameTxPayerResolver.Resolve(tx, stateProvider, state.SenderAccount);
        tx.PayerAddress = resolution.Payer;

        if (logger.IsTrace) logger.Trace($"Resolved frame transaction {tx.Hash} payer: {resolution.Outcome} ({resolution.Payer?.ToString() ?? "none"}).");

        // A NoPayer prefix is provably invalid — its validation prefix never approves payment, so
        // execution's terminal payer gate would reject it. Drop it here rather than pool and re-gossip
        // a transaction that can never be included.
        return resolution.Outcome == FrameTxPayerOutcome.NoPayer
            ? AcceptTxResult.Invalid.WithMessage("Frame transaction never approves a payer")
            : AcceptTxResult.Accepted;
    }
}
