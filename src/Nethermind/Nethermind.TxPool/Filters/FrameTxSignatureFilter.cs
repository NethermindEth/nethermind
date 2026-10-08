// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
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
/// structurally well-formed, and last among the incoming filters: recovery costs elliptic-curve work per
/// signature, so the cheap state filters must reject what they can before any of it is spent on a payload.
/// Records <see cref="TxFilteringState.FrameSignaturesVerified"/> so a downstream filter can assert
/// pre-validation from what ran rather than from this filter's position in the chain.
/// A gossiped transaction's verification yields between signatures to block processing and to this node's block
/// building, and is deferred, as <see cref="FrameTxSimulationFilter"/> defers its simulation.
/// </remarks>
internal sealed class FrameTxSignatureFilter(
    IChainHeadSpecProvider specProvider,
    IEthereumEcdsa ecdsa,
    ILogger logger,
    IChainHeadInfoProvider? headInfo = null)
    : IIncomingTxFilter
{
    private readonly Func<bool>? _blockWorkInProgress = headInfo is null ? null : () => headInfo.IsProcessingBlock || headInfo.IsBuildingBlock;

    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        if (!tx.SupportsFrames || tx.FrameSignatures is not { Length: > 0 })
        {
            state.FrameSignaturesVerified = true; // vacuously: validate_signature passes an empty list
            return AcceptTxResult.Accepted;
        }

        IReleaseSpec spec = specProvider.GetCurrentHeadSpec();
        // Same availability test the processor makes, so a chain reaching P256VERIFY via RIP-7212 rather
        // than EIP-7951 is not refused a signature block processing would verify.
        IPrecompile? p256Precompile = spec.IsPrecompile(FrameTxSignatureValidator.P256VerifyPrecompileAddress)
            ? SecP256r1Precompile.Instance
            : null;
        bool local = (txHandlingOptions & TxHandlingOptions.PersistentBroadcast) != 0;
        if (!FrameTxSignatureValidator.Validate(tx, ecdsa, p256Precompile, spec, local || state.ReAddedFromReorg ? null : _blockWorkInProgress, out bool preempted, out string? error))
        {
            if (preempted)
            {
                Interlocked.Increment(ref Metrics.FrameTxSignatureVerificationsPreempted);
                state.FrameValidationYielded = !tx.CarriesBlobs;
                if (logger.IsTrace) logger.Trace($"Deferred frame transaction {tx.Hash}, its signature verification yielded to block processing or building.");
                return AcceptTxResult.FrameSimulationDeferred.WithMessage(TxPoolErrorMessages.FrameSignatureVerificationDeferred);
            }

            Metrics.PendingTransactionsFrameTxSignatureInvalid++;
            if (logger.IsTrace) logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, {error}.");
            return AcceptTxResult.Invalid.WithMessage(error!);
        }

        state.FrameSignaturesVerified = true;
        return AcceptTxResult.Accepted;
    }
}
