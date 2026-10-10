// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters;

/// <summary>Rejects an EIP-8141 frame transaction whose expiry-verifier deadline is already behind the current head.</summary>
/// <remarks>The predeploy reverts only once <c>block.timestamp &gt; deadline</c>, so the comparison is strict here too.</remarks>
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
