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

        // EIP8141-GAP (devnet only): frame txs are admitted while the fork is unscheduled on public networks.
        // Still missing before any public activation:
        //  - canonical-paymaster reservation, and re-counting the cap when a pay target gains code
        //  - the failed-APPROVE replay bound
        //  - dependency-set revalidation/eviction ordering
        //  - payer-exposure accounting beyond natively-resolved payers, under-reserved until the shared
        //    max_cost helper
        //  - payer and paymaster on blob-pool records restored from disk, which carry neither until
        //    LightTxDecoder encodes them as lists, the way NonceKeys already is
        //  - reorg re-admission beyond one tx per sponsor
        //  - a prefix frame flagged to approve payment whose target declines, which moves the real payer to a
        //    later frame the cap does not key on
        // MalformedTxFilter still enforces static well-formedness downstream.
        if (tx.SupportsFrames && !_specProvider.GetCurrentHeadSpec().IsEip8141Enabled)
        {
            Metrics.PendingTransactionsNotSupportedTxType++;
            if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, frame transactions are not supported in the transaction pool.");
            return AcceptTxResult.NotSupportedTxType;
        }

        // EIP-8141: as for type-3, the mempool form of a blob-carrying frame tx is the sidecar wrapper — without it
        // the pool can neither serve nor re-encode the transaction, and its persisted record fails to decode back.
        // The RLP decoder enforces this for everything off the wire; a transaction built field-by-field over
        // eth_sendTransaction never passes through it.
        if (tx.SupportsFrames && tx.CarriesBlobs && !tx.IsInMempoolForm())
        {
            Metrics.PendingTransactionsFrameTxMissingSidecar++;
            if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, blob-carrying frame transaction has no blob sidecar.");
            return AcceptTxResult.FrameTxMissingSidecar;
        }

        return AcceptTxResult.Accepted;
    }
}
