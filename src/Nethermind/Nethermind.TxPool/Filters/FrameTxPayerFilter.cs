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
/// Annotates the resolved payer and rejects only the structurally-invalid <see cref="FrameTxPayerOutcome.NoPayer"/>
/// prefixes — those with no payment-approving frame at all, provably dead regardless of any signature.
/// A signature-shape mismatch is deliberately not rejected here: it maps to
/// <see cref="FrameTxPayerOutcome.RequiresSimulation"/> and is left to execution, since the hoisted-list
/// vs VERIFY-frame-data signature placement is an open cross-client question the mempool must not
/// pre-judge (rejecting it would turn a local execution divergence into a network-propagation one on a
/// mixed devnet). The broader payer-based rejection (per-payer exposure, paymaster reservation, and the
/// other public-mempool DoS rules) is a deferred follow-up (ethereum/EIPs#12007). Frame txs reach the
/// pool only under the active fork, gated earlier by <see cref="NotSupportedTxFilter"/>. Runs last in
/// the post-hash pipeline so only otherwise-admissible frame txs are resolved.
/// <para>
/// Reachability caveat: the earlier balance filters gate on the <em>sender</em> balance, so a frame tx
/// reaches this filter only if its sender can cover <c>GasLimit * MaxFeePerGas + Value</c> — any prefix
/// shape whose sender is funded arrives here (a funded <c>self_verify</c>, an <c>only_verify</c> prefix,
/// a deployed-sender prefix). A sponsored prefix whose sender is unfunded — the case sponsorship exists
/// for — is rejected upstream by <see cref="BalanceZeroFilter"/> / <see cref="BalanceTooLowFilter"/>
/// before it arrives; making those filters payer-aware (charge the resolved payer for
/// <c>tx.SupportsFrames</c>) is a deferred follow-up.
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

        // A NoPayer outcome is a structural proof of invalidity — the prefix has no payment-approving
        // frame at all, so it never sets a payer regardless of any signature, and execution's terminal
        // payer gate would reject it. Drop it here rather than pool and re-gossip a transaction that can
        // never be included. Signature-shape verdicts are deferred (RequiresSimulation), not rejected
        // here, so an unsettled cross-client signature-placement divergence is left to execution.
        return resolution.Outcome == FrameTxPayerOutcome.NoPayer
            ? AcceptTxResult.Invalid.WithMessage("Frame transaction never approves a payer")
            : AcceptTxResult.Accepted;
    }
}
