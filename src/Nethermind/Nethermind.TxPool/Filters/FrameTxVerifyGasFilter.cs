// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters;

/// <summary>
/// Rejects an EIP-8141 frame transaction whose validation prefix and signature verification would cost more than
/// <c>MAX_VERIFY_GAS</c> to check, or whose validation prefix budgets more than <c>MAX_VERIFY_STATE_GAS</c> of state gas.
/// </summary>
/// <remarks>
/// A public-mempool DoS bound, not a validity rule: a block carrying such a transaction stays valid. Must run after
/// <see cref="MalformedTxFilter"/>, which guarantees the frame list is well-formed. A configured limit of 0 lifts the
/// respective bound, matching the other per-sender pool limits.
/// </remarks>
internal sealed class FrameTxVerifyGasFilter(ITxPoolConfig txPoolConfig, ILogger logger) : IIncomingTxFilter
{
    private readonly ulong _maxVerifyGas = txPoolConfig.FrameTxMaxVerifyGas;
    private readonly ulong _maxVerifyStateGas = txPoolConfig.FrameTxMaxVerifyStateGas;

    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        if (!tx.SupportsFrames)
        {
            return AcceptTxResult.Accepted;
        }

        if (_maxVerifyGas != 0)
        {
            ulong verifyGas = FrameTxValidation.ValidationWorkGas(tx);
            if (verifyGas > _maxVerifyGas)
            {
                Metrics.PendingTransactionsFrameTxVerifyGasTooHigh++;
                if (logger.IsTrace) logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, validation prefix costs {verifyGas} gas (max {_maxVerifyGas}).");
                return AcceptTxResult.FrameTxVerifyGasTooHigh;
            }
        }

        if (_maxVerifyStateGas != 0)
        {
            ulong verifyStateGas = FrameTxValidation.ValidationWorkStateGas(tx);
            if (verifyStateGas > _maxVerifyStateGas)
            {
                Metrics.PendingTransactionsFrameTxVerifyStateGasTooHigh++;
                if (logger.IsTrace) logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, validation prefix budgets {verifyStateGas} state gas (max {_maxVerifyStateGas}).");
                return AcceptTxResult.FrameTxVerifyStateGasTooHigh;
            }
        }

        return AcceptTxResult.Accepted;
    }
}
