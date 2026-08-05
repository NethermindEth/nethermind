// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters;

/// <summary>
/// Rejects an EIP-8141 frame transaction whose expiry-verifier deadline is already behind the current head.
/// </summary>
/// <remarks>
/// The expiry-verifier predeploy reverts once <c>block.timestamp &gt; deadline</c>, so such a transaction could never
/// satisfy its validation prefix and must not be pooled or broadcast (ethereum/EIPs#12007, "Revalidation"). This is the
/// admission-time counterpart of the on-head eviction pass and uses the exact same strict-greater-than predicate, so a
/// transaction the predeploy would still accept at the boundary (<c>deadline == head.timestamp</c>) is never dropped.
/// The deadline is read statically from the expiry-verifier frame, so no validation-prefix simulation is required.
/// Must run after <see cref="MalformedTxFilter"/>, which guarantees the frame is well-formed.
/// </remarks>
internal sealed class ExpiredFrameTxFilter(IChainHeadInfoProvider chainHeadInfoProvider, ILogger logger) : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        if (tx.SupportsFrames
            && FrameTxValidation.TryGetExpiryDeadline(tx, out ulong deadline)
            && chainHeadInfoProvider.HeadTimestamp > deadline)
        {
            Metrics.PendingTransactionsFrameTxExpired++;
            if (logger.IsTrace) logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, frame transaction expired at {deadline} (head timestamp {chainHeadInfoProvider.HeadTimestamp}).");
            return AcceptTxResult.FrameTxExpired;
        }

        return AcceptTxResult.Accepted;
    }
}
