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
        //
        // EIP8141: remaining pieces of the merged mempool lifecycle rules (ethereum/EIPs#12007) are
        // deferred because they all require a frame-tx validation-prefix simulation + payer resolution
        // layer that does not exist yet:
        //   - per-payer / canonical-paymaster exposure accounting (reserved_pending_cost <= balance),
        //     with atomic payer-switch on replacement;
        //   - dependency-set-indexed revalidation on head change, incl. the payer balance/code trigger;
        //   - the full eviction ordering (invalidated-first tier).
        // The expiry-deadline eviction tier is implemented in TxPool.RemoveExpiredFrameTransactions,
        // and the (sender, nonce) + both-fee bump replacement rule is already provided by the shared
        // CompareReplacedTxByFee path.
        if (tx.SupportsFrames && !_specProvider.GetCurrentHeadSpec().IsEip8141Enabled)
        {
            Metrics.PendingTransactionsNotSupportedTxType++;
            if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, frame transactions are not supported in the transaction pool.");
            return AcceptTxResult.NotSupportedTxType;
        }

        return AcceptTxResult.Accepted;
    }
}
