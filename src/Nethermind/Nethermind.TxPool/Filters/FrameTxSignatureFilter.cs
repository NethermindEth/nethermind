// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters;

/// <summary>Rejects an EIP-8141 frame transaction whose protocol-validated signatures do not verify.</summary>
/// <remarks>The frame sender is explicit, so nothing else in the pool checks <c>frame_signatures</c>; a failure here can never verify at any head.</remarks>
internal sealed class FrameTxSignatureFilter(
    IChainHeadSpecProvider specProvider,
    IEthereumEcdsa ecdsa,
    ILogger logger)
    : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        if (!tx.SupportsFrames || tx.FrameSignatures is not { Length: > 0 })
        {
            return AcceptTxResult.Accepted;
        }

        IReleaseSpec spec = specProvider.GetCurrentHeadSpec();
        // Same availability test the processor makes, so a chain reaching P256VERIFY via RIP-7212 rather
        // than EIP-7951 is not refused a signature block processing would verify.
        IPrecompile? p256Precompile = spec.IsPrecompile(FrameTxSignatureValidator.P256VerifyPrecompileAddress)
            ? SecP256r1Precompile.Instance
            : null;
        if (!FrameTxSignatureValidator.Validate(tx, ecdsa, p256Precompile, spec, out string? error))
        {
            Metrics.PendingTransactionsFrameTxSignatureInvalid++;
            if (logger.IsTrace) logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, {error}.");
            return AcceptTxResult.Invalid.WithMessage(error!);
        }

        return AcceptTxResult.Accepted;
    }
}
