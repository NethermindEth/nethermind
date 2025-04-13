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
    private readonly long _configuredMaxBlobTxSize = txPoolConfig.MaxBlobTxSize ?? long.MaxValue;

    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        long maxSize = tx.SupportsBlobs ? _configuredMaxBlobTxSize : _configuredMaxTxSize;

        if (tx.GetLength(shouldCountBlobs: false) > maxSize)
        {
            Metrics.PendingTransactionsSizeTooLarge++;
            if (logger.IsTrace) logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, max tx size exceeded.");
            return AcceptTxResult.MaxTxSizeExceeded;
        }

        return AcceptTxResult.Accepted;
    }
}
