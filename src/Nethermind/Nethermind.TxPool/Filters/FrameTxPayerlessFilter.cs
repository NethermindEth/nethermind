// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters;

/// <summary>
/// Rejects an EIP-8141 frame transaction whose validation prefix structurally can never approve a payer.
/// </summary>
/// <remarks>Purely structural, so it can run before <see cref="FrameTxSignatureFilter"/> and save recovering
/// an uncapped signature list; needs the sender and frames validated by <see cref="MalformedTxFilter"/>.</remarks>
internal sealed class FrameTxPayerlessFilter(ILogger logger) : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        if (!tx.SupportsFrames || !FrameTxPayerResolver.IsStructurallyPayerless(tx))
        {
            return AcceptTxResult.Accepted;
        }

        Metrics.PendingTransactionsFrameTxNoPayer++;
        if (logger.IsTrace) logger.Trace($"Skipped adding frame transaction {tx.Hash}, its validation prefix never approves a payer.");
        return AcceptTxResult.FrameTxNoPayer;
    }
}
