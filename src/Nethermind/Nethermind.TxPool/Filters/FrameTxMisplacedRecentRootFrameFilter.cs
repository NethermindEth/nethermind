// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters;

/// <summary>Rejects an EIP-8141 frame transaction carrying a <c>VERIFY</c> frame to <c>RECENT_ROOT_ADDRESS</c> that is not a
/// single well-formed EIP-8272 <c>recent_root_verify</c> frame behind the optional leading expiry verifier frame.</summary>
/// <remarks>A propagation bound, not a validity rule, decided on the frame list before any sender state is read.</remarks>
internal sealed class FrameTxMisplacedRecentRootFrameFilter(ILogger logger) : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        if (!tx.SupportsFrames || !FrameTxValidation.HasMisplacedRecentRootVerifyFrame(tx))
        {
            return AcceptTxResult.Accepted;
        }

        Metrics.PendingTransactionsFrameTxMisplacedRecentRootFrame++;
        if (logger.IsTrace) logger.Trace($"Skipped adding frame transaction {tx.Hash}, its recent_root_verify frame is malformed or misplaced.");
        return AcceptTxResult.FrameTxMisplacedRecentRootFrame;
    }
}
