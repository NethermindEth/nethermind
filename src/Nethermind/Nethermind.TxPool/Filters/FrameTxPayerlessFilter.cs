// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters;

/// <summary>Rejects an EIP-8141 frame transaction whose validation prefix is unrecognized or structurally cannot approve a payer.</summary>
/// <remarks>The verdict needs no signatures, so it runs ahead of <see cref="FrameTxSignatureFilter"/> and its per-signature recovery.</remarks>
internal sealed class FrameTxPayerlessFilter(ILogger logger) : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        if (!tx.SupportsFrames) return AcceptTxResult.Accepted;

        if (!FrameTxValidation.HasRecognizedValidationPrefix(tx)
            && !FrameTxPayerResolver.IsStructurallyPayerless(tx))
            return AcceptTxResult.Invalid.WithMessage("unrecognized frame transaction validation prefix");

        if (!FrameTxPayerResolver.IsStructurallyPayerless(tx))
        {
            return AcceptTxResult.Accepted;
        }

        Metrics.PendingTransactionsFrameTxNoPayer++;
        if (logger.IsTrace) logger.Trace($"Skipped adding frame transaction {tx.Hash}, its validation prefix never approves a payer.");
        return AcceptTxResult.FrameTxNoPayer;
    }
}
