// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters;

/// <summary>
/// Ignores transactions that exceed configured max transaction size limit.
/// </summary>
internal sealed class SizeTxFilter(ITxPoolConfig txPoolConfig, ILogger logger) : IIncomingTxFilter
{
    private readonly long _configuredMaxTxSize = txPoolConfig.MaxTxSize ?? long.MaxValue;

    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        if (tx.SupportsBlobs)
        {
            return AcceptTxResult.Accepted;
        }

        if (tx.GetLength() > _configuredMaxTxSize)
        {
            if (logger.IsTrace) logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, max tx size exceeded.");
            return AcceptTxResult.MaxTxSizeExceeded;
        }

        return AcceptTxResult.Accepted;
    }
}
