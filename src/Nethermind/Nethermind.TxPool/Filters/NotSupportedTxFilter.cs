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

        // EIP8141-GAP (TEMPORARY — devnet only, must change before any public activation): the
        // public mempool DoS rules for frame transactions (validation prefixes, MAX_VERIFY_GAS,
        // canonical paymaster reservation, failed-APPROVE replay bound) are NOT implemented yet.
        // Admitting frame txs here is safe only because the EIP-8141 fork (Bogota) is not scheduled on
        // any public network, so this branch is exercised on devnets alone; it exists purely to let
        // rex/tooling submit frame txs for end-to-end devnet testing. Before frame txs may enter a
        // public mempool this gate must be tightened to also require those DoS filters. Static
        // well-formedness is already enforced downstream by MalformedTxFilter regardless.
        // EIP-8288 dependency-verification frames ride this same admission path; their mode and
        // per-tx dependency limits are validated downstream by MalformedTxFilter when EIP-8288 is
        // active. Wrapper-based proof distribution is a separate mempool layer, not required while
        // proofs are stubbed on devnets.
        if (tx.SupportsFrames && !_specProvider.GetCurrentHeadSpec().IsEip8141Enabled)
        {
            Metrics.PendingTransactionsNotSupportedTxType++;
            if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, frame transactions are not supported in the transaction pool.");
            return AcceptTxResult.NotSupportedTxType;
        }

        return AcceptTxResult.Accepted;
    }
}
