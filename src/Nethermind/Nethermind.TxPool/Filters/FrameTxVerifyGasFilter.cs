// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters;

/// <summary>
/// Enforces the EIP-8141 <c>MAX_VERIFY_GAS</c> admission bound: rejects a frame transaction whose
/// validation-prefix gas plus signature-validation cost exceeds <see cref="Eip8141Constants.MaxVerifyGas"/>.
/// </summary>
/// <remarks>
/// State-free structural gate, so it runs right after <c>MalformedTxFilter</c> (which recovers the
/// sender and establishes a well-formed frame array) and before any state-reading filter. Only frame
/// txs with a recognized validation prefix are bounded; other txs pass through (structural-match gate
/// deferred). This is the Direct Evaluation form of the bound and needs no simulation
/// (ethereum/EIPs#12007, "the sum of gas_limit values across the validation prefix, plus the intrinsic
/// cost of validating tx.signatures, must not exceed MAX_VERIFY_GAS"). The bound targets gossiped
/// public-mempool traffic, so locally-submitted (persistent-broadcast) txs — the operator's own — are
/// exempt; an over-budget local tx still won't propagate, as peers apply the same bound.
/// https://eips.ethereum.org/EIPS/eip-8141
/// </remarks>
internal sealed class FrameTxVerifyGasFilter(ILogger logger) : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        bool isLocal = (txHandlingOptions & TxHandlingOptions.PersistentBroadcast) != 0;
        if (isLocal || !tx.SupportsFrames || !FrameTxPayerResolver.TryGetValidationPrefixVerifyGas(tx, out ulong verifyGas))
        {
            return AcceptTxResult.Accepted;
        }

        if (verifyGas > Eip8141Constants.MaxVerifyGas)
        {
            if (logger.IsTrace)
                logger.Trace($"Skipped adding frame transaction {tx.Hash}, validation-prefix verify gas {verifyGas} exceeds MAX_VERIFY_GAS {Eip8141Constants.MaxVerifyGas}.");
            return AcceptTxResult.VerifyGasExceeded;
        }

        return AcceptTxResult.Accepted;
    }
}
