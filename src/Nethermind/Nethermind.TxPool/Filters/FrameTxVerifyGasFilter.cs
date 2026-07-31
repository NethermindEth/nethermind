// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters;

/// <summary>
/// Rejects an EIP-8141 frame transaction whose validation prefix and signature verification would cost more than
/// <c>MAX_VERIFY_GAS</c> to check.
/// </summary>
/// <remarks>
/// This is a public-mempool DoS bound, not a validity rule: a block carrying such a transaction stays valid, and the
/// transaction can still be delivered out of band. Without the bound a sender can make every node on the network run
/// an arbitrarily expensive prefix before any gas is paid. The budget is read statically from the frame layout, so no
/// prefix simulation is required. Must run after <see cref="MalformedTxFilter"/>, which guarantees the frame list is
/// well-formed. A configured limit of 0 lifts the bound, matching the other per-sender pool limits.
/// </remarks>
internal sealed class FrameTxVerifyGasFilter(ITxPoolConfig txPoolConfig, ILogger logger) : IIncomingTxFilter
{
    private readonly ulong _maxVerifyGas = txPoolConfig.FrameTxMaxVerifyGas;

    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        if (tx.SupportsFrames && _maxVerifyGas != 0)
        {
            ulong verifyGas = FrameTxValidation.ValidationWorkGas(tx);
            if (verifyGas > _maxVerifyGas)
            {
                Metrics.PendingTransactionsFrameTxVerifyGasTooHigh++;
                if (logger.IsTrace) logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, validation prefix costs {verifyGas} gas (max {_maxVerifyGas}).");
                return AcceptTxResult.FrameTxVerifyGasTooHigh;
            }
        }

        return AcceptTxResult.Accepted;
    }
}
