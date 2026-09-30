// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters;

/// <summary>Rejects an EIP-8141 frame transaction carrying a <c>VERIFY</c> frame to <c>RECENT_ROOT_ADDRESS</c> that is not a
/// single well-formed EIP-8272 <c>recent_root_verify</c> frame behind the optional leading expiry verifier frame.</summary>
/// <remarks>A propagation bound, not a validity rule, decided on the frame list before any sender state is read.
/// A persistent blob pool keeps its records without frames, so the per-head recent-root sweep cannot see their
/// tuples; there a blob-carrying frame transaction with a <c>recent_root_verify</c> frame is refused instead.</remarks>
internal sealed class FrameTxMisplacedRecentRootFrameFilter(ILogger logger, bool blobRecordsDropFrames) : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        if (!tx.SupportsFrames)
        {
            return AcceptTxResult.Accepted;
        }

        if (FrameTxValidation.HasMisplacedRecentRootVerifyFrame(tx))
        {
            Metrics.PendingTransactionsFrameTxMisplacedRecentRootFrame++;
            if (logger.IsTrace) logger.Trace($"Skipped adding frame transaction {tx.Hash}, its recent_root_verify frame is malformed or misplaced.");
            return AcceptTxResult.FrameTxMisplacedRecentRootFrame;
        }

        if (blobRecordsDropFrames && tx.CarriesBlobs && FrameTxValidation.TryGetRecentRootTuples(tx, out _))
        {
            Metrics.PendingTransactionsFrameTxMisplacedRecentRootFrame++;
            if (logger.IsTrace) logger.Trace($"Skipped adding blob-carrying frame transaction {tx.Hash}, the persistent blob pool cannot revalidate its recent roots.");
            return AcceptTxResult.FrameTxRecentRootWithPersistentBlobs;
        }

        return AcceptTxResult.Accepted;
    }
}
