// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters;

/// <summary>
/// Filters out transactions types that are not supported
/// </summary>
internal sealed class NotSupportedTxFilter : IIncomingTxFilter
{
    private readonly ITxPoolConfig _txPoolConfig;
    private readonly ILogger _logger;

    public NotSupportedTxFilter(ITxPoolConfig txPoolConfig, ILogger logger)
    {
        _txPoolConfig = txPoolConfig;
        _logger = logger;
    }

    public AcceptTxResult Accept(Transaction tx, TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        if (!_txPoolConfig.BlobSupportEnabled && tx.SupportsBlobs)
        {
            if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, blob transactions are not supported.");
            return AcceptTxResult.NotSupportedTxType;
        }

        return AcceptTxResult.Accepted;
    }
}
