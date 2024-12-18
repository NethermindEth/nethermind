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
        // for blob txs max size limit (excluding blobs) is 8 * maxTxSize
        if (tx.GetLength(shouldCountBlobs: false) > _configuredMaxTxSize * (tx.SupportsBlobs ? 8 : 1))
        {
            if (logger.IsTrace) logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, max tx size exceeded.");
            return AcceptTxResult.MaxTxSizeExceeded;
        }

        return AcceptTxResult.Accepted;
    }
}
