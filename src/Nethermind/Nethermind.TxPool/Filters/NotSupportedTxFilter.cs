// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters;

/// <summary>
/// Filters out transactions types that are not supported
/// </summary>
internal sealed class NotSupportedTxFilter(ITxPoolConfig txPoolConfig, IChainHeadSpecProvider specProvider, ILogger logger) : IIncomingTxFilter
{
    private readonly ITxPoolConfig _txPoolConfig = txPoolConfig;
    private readonly IChainHeadSpecProvider _specProvider = specProvider;
    private readonly ILogger _logger = logger;

    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        if (_txPoolConfig.BlobsSupport.IsDisabled() && tx.SupportsBlobs)
        {
            Metrics.PendingTransactionsNotSupportedTxType++;
            if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, blob transactions are not supported.");
            return AcceptTxResult.NotSupportedTxType;
        }

        // EIP8141-GAP: a blob-carrying frame tx is block-valid, but the pool has no sidecar path for it —
        // it would land in the non-blob pool and a payload built from it would declare blob gas it cannot
        // supply blobs for (BlobsBundle skips non-type-3 txs).
        if (tx.SupportsFrames && tx.BlobVersionedHashes is { Length: > 0 })
        {
            Metrics.PendingTransactionsNotSupportedTxType++;
            if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, blob-carrying frame transactions are not supported in the transaction pool.");
            return AcceptTxResult.NotSupportedTxType;
        }

        // EIP8141-GAP (devnet only): frame txs are admitted while the fork is unscheduled on public networks.
        // Still missing before any public activation: canonical-paymaster reservation, the failed-APPROVE
        // replay bound, and the nearest-expiry-then-lowest-fee half of the eviction order.
        if (tx.SupportsFrames && !_specProvider.GetCurrentHeadSpec().IsEip8141Enabled)
        {
            Metrics.PendingTransactionsNotSupportedTxType++;
            if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, frame transactions are not supported in the transaction pool.");
            return AcceptTxResult.NotSupportedTxType;
        }

        return AcceptTxResult.Accepted;
    }
}
