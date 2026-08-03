// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters;

/// <summary>
/// Rejects an EIP-8141 frame transaction whose protocol-validated signatures do not verify.
/// </summary>
/// <remarks>
/// The frame sender is explicit in the payload, so a frame transaction never goes through the sender
/// recovery that rejects a bad signature on every other transaction type, and nothing else in the pool
/// looks at <c>frame_signatures</c>. A signature that fails <c>validate_signature</c> can never verify
/// at any future head, so pooling and gossiping one only spends peer work on a payload every conforming
/// client must reject. Runs the same check the processor runs before any frame executes, so a pooled
/// transaction cannot fail pre-flight on its signatures.
/// Must run after <see cref="MalformedTxFilter"/>, which guarantees the frame and signature lists are
/// structurally well-formed, and last among the incoming filters: the signature list is uncapped, so the
/// cheap state filters must reject what they can before any elliptic-curve work is spent on a payload.
/// </remarks>
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
        // Same availability test the processor resolves through ICodeInfoRepository.GetPrecompile, so a
        // chain that reaches P256VERIFY through RIP-7212 rather than EIP-7951 is not refused here for a
        // signature block processing verifies.
        IPrecompile? p256Precompile = spec.IsPrecompile(FrameTxSignatureValidator.P256VerifyPrecompileAddress)
            ? SecP256r1Precompile.Instance
            : null;
        if (!FrameTxSignatureValidator.Validate(tx, ecdsa, p256Precompile, spec, out string? error))
        {
            Metrics.PendingTransactionsMalformed++;
            if (logger.IsTrace) logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, {error}.");
            return AcceptTxResult.Invalid.WithMessage(error!);
        }

        return AcceptTxResult.Accepted;
    }
}
