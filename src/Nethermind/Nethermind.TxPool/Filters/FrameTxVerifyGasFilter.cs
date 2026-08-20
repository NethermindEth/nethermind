// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters;

/// <summary>Rejects an EIP-8141 frame transaction costing more than <c>MAX_VERIFY_GAS</c> to check. A DoS bound, not a validity rule.</summary>
/// <remarks>A configured limit of 0 lifts the bound, matching the other per-sender pool limits.</remarks>
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
