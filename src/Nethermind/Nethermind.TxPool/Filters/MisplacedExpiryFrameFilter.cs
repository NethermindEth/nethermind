// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters;

/// <summary>
/// Rejects an EIP-8141 frame transaction whose expiry verifier frame does not lead its frame list.
/// </summary>
/// <remarks>
/// A public-mempool rule, not a validity rule: the reference implementation validates an expiry frame's shape and
/// uniqueness but never its position, so a block carrying one stays valid. The pool reads the deadline from the
/// leading frame alone, so a misplaced frame would carry a deadline the expiry sweep can never see and the
/// transaction would outlive it. Must run after <see cref="MalformedTxFilter"/>, whose expiry-frame shape rules make
/// the leading-frame test exact, and before <see cref="ExpiredFrameTxFilter"/>, which reads that deadline.
/// </remarks>
internal sealed class MisplacedExpiryFrameFilter(ILogger logger) : IIncomingTxFilter
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
