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

        // EIP-8141: the frame codec now round-trips the sidecar, but the persistent blob pool's type-6 arc
        // (reload, serve, frame expiry) is still unexercised, so admit these under BlobsSupportMode.InMemory only.
        if (tx.SupportsFrames && tx.CarriesBlobs && !_txPoolConfig.BlobsSupport.SupportsBlobFrameTxs())
        {
            Metrics.PendingTransactionsNotSupportedTxType++;
            if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, blob-carrying frame transactions require in-memory blob support.");
            return AcceptTxResult.NotSupportedTxType;
        }

        // EIP8141-GAP (devnet only): frame txs are admitted while the fork is unscheduled on public networks.
        // The public-mempool DoS rules (validation-prefix simulation, MAX_VERIFY_GAS, paymaster reservation,
        // failed-APPROVE replay bound, payer-exposure accounting, dependency-set revalidation/eviction ordering)
        // are NOT implemented and must gate this branch before any public activation. MalformedTxFilter still
        // enforces static well-formedness downstream.
        if (tx.SupportsFrames && !_specProvider.GetCurrentHeadSpec().IsEip8141Enabled)
        {
            Metrics.PendingTransactionsNotSupportedTxType++;
            if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, frame transactions are not supported in the transaction pool.");
            return AcceptTxResult.NotSupportedTxType;
        }

        return AcceptTxResult.Accepted;
    }
}
