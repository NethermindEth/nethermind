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
/// Rejects an EIP-8141 frame transaction whose witnesses do not satisfy its claims.
/// </summary>
/// <remarks>
/// The frame sender is explicit in the payload, so a frame transaction never goes through the sender
/// recovery that rejects a bad signature on every other transaction type, and nothing else in the pool
/// looks at the claims. A claim that fails verification can never verify at any future head, so pooling and
/// gossiping one only spends peer work on a payload every conforming client must reject. Checks the witnesses'
/// shape, then verifies the SECP256K1 and P256 claims, which need no state, before anything reads it.
/// A leanSPHINCS claim and a custom-verifier claim are left to the prefix simulation, which verifies every claim
/// the same way the processor does before any frame executes; <see cref="TxFilteringState.FrameClaimsVerified"/>
/// is then left unset, so a downstream filter does not take them as verified.
/// Must run after <see cref="MalformedTxFilter"/>, which guarantees the frame and claim lists are
/// structurally well-formed, and last among the incoming filters: verification costs elliptic-curve work per
/// claim, so the cheap state filters must reject what they can before any of it is spent on a payload.
/// A gossiped transaction's verification yields between claims to block processing and to this node's block
/// building, and is deferred, as <see cref="FrameTxSimulationFilter"/> defers its simulation.
/// </remarks>
internal sealed class FrameTxClaimFilter(
    IChainHeadSpecProvider specProvider,
    IEthereumEcdsa ecdsa,
    ILogger logger,
    IChainHeadInfoProvider? headInfo = null)
    : IIncomingTxFilter
{
    private readonly Func<bool>? _blockWorkInProgress = headInfo is null ? null : () => headInfo.IsProcessingBlock || headInfo.IsBuildingBlock;

    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        if (!tx.SupportsFrames || tx.FrameClaims is not { Length: > 0 })
        {
            state.FrameClaimsVerified = true; // vacuously: there is nothing to verify
            return AcceptTxResult.Accepted;
        }

        if (!FrameTxClaimVerifier.CheckWitnesses(tx, allowPlaceholders: false, out string? error))
        {
            return Reject(tx, error!);
        }

        IReleaseSpec spec = specProvider.GetCurrentHeadSpec();
        // Same availability test the processor makes, so a chain reaching P256VERIFY via RIP-7212 rather
        // than EIP-7951 is not refused a claim block processing would verify.
        IPrecompile? p256Precompile = spec.IsPrecompile(FrameTxClaimVerifier.P256VerifyPrecompileAddress)
            ? SecP256r1Precompile.Instance
            : null;
        bool local = (txHandlingOptions & TxHandlingOptions.PersistentBroadcast) != 0;
        bool valid = FrameTxClaimVerifier.VerifyProtocolClaims(tx, ecdsa, p256Precompile, leanSphincs: null, spec,
            local || state.ReAddedFromReorg ? null : _blockWorkInProgress, out bool preempted, out bool hasSkippedClaims, out error);
        // A verification that yielded to this node's own block work is deferred, not charged to the sending peer.
        state.FrameValidationRan = !preempted;
        if (!valid)
        {
            if (preempted)
            {
                Interlocked.Increment(ref Metrics.FrameTxSignatureVerificationsPreempted);
                state.FrameValidationYielded = !tx.CarriesBlobs;
                if (logger.IsTrace) logger.Trace($"Deferred frame transaction {tx.Hash}, its claim verification yielded to block processing or building.");
                return AcceptTxResult.FrameSimulationDeferred.WithMessage(TxPoolErrorMessages.FrameSignatureVerificationDeferred);
            }

            return Reject(tx, error!);
        }

        state.FrameClaimsVerified = !hasSkippedClaims;
        return AcceptTxResult.Accepted;
    }

    private AcceptTxResult Reject(Transaction tx, string error)
    {
        Metrics.PendingTransactionsFrameTxSignatureInvalid++;
        if (logger.IsTrace) logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, {error}.");
        return AcceptTxResult.Invalid.WithMessage(error);
    }
}
