// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters;

/// <summary>Rejects an EIP-8141 frame transaction whose expiry verifier frame does not lead its frame list. A propagation bound, not a validity rule.</summary>
/// <remarks>The pool reads the deadline from the leading frame alone, so a misplaced frame would outlive the expiry sweep.</remarks>
internal sealed class FrameTxMisplacedExpiryFrameFilter(ILogger logger) : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        if (!tx.SupportsFrames || !FrameTxValidation.HasMisplacedExpiryFrame(tx))
        {
            return AcceptTxResult.Accepted;
        }

        Metrics.PendingTransactionsFrameTxMisplacedExpiryFrame++;
        if (logger.IsTrace) logger.Trace($"Skipped adding frame transaction {tx.Hash}, its expiry verifier frame does not lead the frame list.");
        return AcceptTxResult.FrameTxMisplacedExpiryFrame;
    }
}
