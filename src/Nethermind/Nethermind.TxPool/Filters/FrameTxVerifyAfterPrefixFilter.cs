// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters;

/// <summary>
/// Rejects an EIP-8141 frame transaction carrying a <c>VERIFY</c> frame behind its validation prefix.
/// </summary>
/// <remarks>
/// A public-mempool rule, not a validity rule: a block carrying such a transaction stays valid. A VERIFY frame that
/// reverts invalidates the whole transaction, so one placed past the prefix lets state the pool never validated
/// invalidate a pooled transaction. Purely structural, so it runs before <see cref="FrameTxSignatureFilter"/> spends
/// any elliptic-curve work. Must run after <see cref="MalformedTxFilter"/>, which resolves the sender the prefix
/// grammar is matched against and guarantees the frame list is well-formed.
/// </remarks>
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
