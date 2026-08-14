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
        if (_txPoolConfig.BlobsSupport.IsDisabled() && tx.CarriesBlobs)
        {
            Metrics.PendingTransactionsNotSupportedTxType++;
            if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, blob transactions are not supported.");
            return AcceptTxResult.NotSupportedTxType;
        }

        // EIP8141-GAP: frame expiry cannot see a persisted blob-carrying frame tx, so admit in memory only.
        if (tx.SupportsFrames && tx.CarriesBlobs && !_txPoolConfig.BlobsSupport.SupportsBlobFrameTxs())
        {
            Metrics.PendingTransactionsNotSupportedTxType++;
            if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, blob-carrying frame transactions require in-memory blob support.");
            return AcceptTxResult.NotSupportedTxType;
        }

        // EIP8141-GAP (devnet only): frame txs are admitted while the fork is unscheduled on public networks.
        // Still missing before any public activation: canonical-paymaster reservation, the failed-APPROVE
        // replay bound, dependency-set revalidation/eviction ordering, payer-exposure accounting beyond
        // natively-resolved payers (under-reserved until the shared max_cost helper), re-counting the cap
        // when a pay target gains code, and reorg re-admission beyond one tx per sponsor.
        // MalformedTxFilter still enforces static well-formedness downstream.
        if (tx.SupportsFrames && !_specProvider.GetCurrentHeadSpec().IsEip8141Enabled)
        {
            Metrics.PendingTransactionsNotSupportedTxType++;
            if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, frame transactions are not supported in the transaction pool.");
            return AcceptTxResult.NotSupportedTxType;
        }

        return AcceptTxResult.Accepted;
    }
}
