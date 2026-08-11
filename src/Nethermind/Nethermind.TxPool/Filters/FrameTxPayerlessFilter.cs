// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters;

/// <summary>
/// Rejects an EIP-8141 frame transaction whose validation prefix structurally can never approve a payer.
/// </summary>
/// <remarks>
/// The verdict is purely structural (frame mode/flags/target and the explicit sender only), so it holds
/// regardless of signature validity. Running it ahead of <see cref="FrameTxSignatureFilter"/> drops a
/// payerless gossiped transaction before any elliptic-curve work is spent recovering its uncapped
/// signature list. Must run after <see cref="MalformedTxFilter"/>, which resolves the sender and
/// guarantees the frame list is well-formed.
/// </remarks>
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
        return AcceptTxResult.Invalid.WithMessage("Frame transaction never approves a payer");
    }
}
