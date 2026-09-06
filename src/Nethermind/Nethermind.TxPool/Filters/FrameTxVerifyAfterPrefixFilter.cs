// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters;

/// <summary>Rejects an EIP-8141 frame transaction carrying a <c>VERIFY</c> frame behind its validation prefix. A propagation bound, not a validity rule.</summary>
/// <remarks>A VERIFY revert invalidates the whole transaction, so one past the prefix invalidates on state the pool never validated.</remarks>
internal sealed class FrameTxVerifyAfterPrefixFilter(ILogger logger) : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        if (!tx.SupportsFrames || !FrameTxValidation.HasVerifyFrameAfterPrefix(tx))
        {
            return AcceptTxResult.Accepted;
        }

        Metrics.PendingTransactionsFrameTxVerifyAfterPrefix++;
        if (logger.IsTrace) logger.Trace($"Skipped adding frame transaction {tx.Hash}, it has a VERIFY frame after its validation prefix.");
        return AcceptTxResult.FrameTxVerifyAfterPrefix;
    }
}
