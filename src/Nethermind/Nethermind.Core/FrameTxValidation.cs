// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;

namespace Nethermind.Core;

/// <summary>
/// Static validity constraints of EIP-8141 frame transactions (the spec "Constraints" block), plus
/// the EIP-8288 dependency-verification frame constraints when that fork is enabled.
/// Structural RLP shape is enforced at decode time; this class covers the semantic constraints
/// that are checkable without state.
/// </summary>
public static class FrameTxValidation
{
    public const string MissingFrames = "frame transaction must contain between 1 and 64 frames";
    public const string MissingSender = "frame transaction sender must be set";
    public const string InvalidMode = "frame mode must be DEFAULT, VERIFY, or SENDER";
    public const string InvalidFlags = "frame flags must not use reserved bits";
    public const string ValueOutsideSenderMode = "frame value is only allowed in SENDER mode";
    public const string ExecutionApprovalWrongTarget = "frames allowed to approve execution must target the sender";
    public const string AtomicBatchOnLastFrame = "the last frame must not have the atomic batch flag set";
    public const string FrameGasOverflow = "total frame gas must not exceed 2^64 - 1";
    public const string InvalidExpiryFrame = "expiry verifier frame must have zero flags, zero value, and 8-byte data";
    public const string MultipleExpiryFrames = "at most one expiry verifier frame is allowed";
    public const string InvalidSignatureScheme = "unknown signature scheme";
    public const string ArbitrarySignatureWithSigner = "ARBITRARY signatures must not name a signer";
    public const string InvalidMsgLength = "signature msg must be empty or a 32-byte digest";
    public const string ZeroDigestMsg = "explicit signature msg must not be the zero digest";
    public const string BlobFeeWithoutBlobs = "max fee per blob gas must be 0 when there are no blob hashes";
    public const string DependencyFrameShape = "dependency verification frame must have a null target, zero value, and zero flags";
    public const string DependencyFrameDataLength = "dependency verification frame data must be a non-empty multiple of 96 bytes";
    public const string TooManyDependenciesPerFrame = "dependency verification frame must declare at most 256 dependencies";
    public const string DependencyPaddingNotZero = "each dependency must begin with 31 zero bytes before the scheme id";
    public const string InvalidDependencyScheme = "dependency scheme must be LEANSPHINCS or LEANSTARK";
    public const string DependencyFrameGasMismatch = "dependency verification frame gas limit must equal the sum of per-scheme verification gas";
    public const string TooManySigDeps = "at most 16 leanSPHINCS dependencies per transaction";
    public const string TooManyStarkDeps = "at most 1 leanSTARK dependency per transaction";

    public static bool IsWellFormed(Transaction transaction, IReleaseSpec spec, out string? error)
    {
        error = null;

        TxFrame[]? frames = transaction.Frames;
        if (frames is null || frames.Length == 0 || frames.Length > Eip8141Constants.MaxFrames)
        {
            error = MissingFrames;
            return false;
        }

        if (transaction.SenderAddress is null)
        {
            error = MissingSender;
            return false;
        }

        ulong totalFrameGas = 0;
        bool hasExpiryFrame = false;
        int sphincsDeps = 0;
        int starkDeps = 0;
        for (int i = 0; i < frames.Length; i++)
        {
            TxFrame frame = frames[i];

            if (frame.Mode == TxFrame.ModeDepVerify)
            {
                // EIP-8288 dependency-verification frame; only valid once that fork is enabled.
                if (!spec.IsEip8288Enabled || !IsWellFormedDependencyFrame(frame, ref sphincsDeps, ref starkDeps, out error))
                {
                    error ??= InvalidMode;
                    return false;
                }
            }
            else if (frame.Mode > TxFrame.ModeSender)
            {
                error = InvalidMode;
                return false;
            }
            else
            {
                if (frame.Flags > (TxFrame.ApproveScopeMask | TxFrame.AtomicBatchFlag))
                {
                    error = InvalidFlags;
                    return false;
                }

                if (frame.Mode != TxFrame.ModeSender && !frame.Value.IsZero)
                {
                    error = ValueOutsideSenderMode;
                    return false;
                }

                if ((frame.Flags & TxFrame.ApproveExecution) != 0
                    && frame.Target is not null
                    && frame.Target != transaction.SenderAddress)
                {
                    error = ExecutionApprovalWrongTarget;
                    return false;
                }

                if ((frame.Flags & TxFrame.AtomicBatchFlag) != 0 && i + 1 == frames.Length)
                {
                    error = AtomicBatchOnLastFrame;
                    return false;
                }

                if (frame.Mode == TxFrame.ModeVerify && frame.Target == Eip8141Constants.ExpiryVerifierAddress)
                {
                    if (frame.Flags != 0 || !frame.Value.IsZero || frame.Data.Length != Eip8141Constants.ExpiryDataLength)
                    {
                        error = InvalidExpiryFrame;
                        return false;
                    }

                    if (hasExpiryFrame)
                    {
                        error = MultipleExpiryFrames;
                        return false;
                    }

                    hasExpiryFrame = true;
                }
            }

            ulong accumulated = totalFrameGas + frame.GasLimit;
            if (accumulated < totalFrameGas)
            {
                error = FrameGasOverflow;
                return false;
            }

            totalFrameGas = accumulated;
        }

        // Spec per-transaction dependency limits (MAX_SIGS_PER_TX, MAX_STARKS_PER_TX).
        if (sphincsDeps > Eip8288Constants.MaxSigsPerTx)
        {
            error = TooManySigDeps;
            return false;
        }

        if (starkDeps > Eip8288Constants.MaxStarksPerTx)
        {
            error = TooManyStarkDeps;
            return false;
        }

        TxFrameSignature[]? signatures = transaction.FrameSignatures;
        if (signatures is not null)
        {
            for (int i = 0; i < signatures.Length; i++)
            {
                TxFrameSignature signature = signatures[i];

                if (signature.Scheme > TxFrameSignature.SchemeP256)
                {
                    error = InvalidSignatureScheme;
                    return false;
                }

                if (signature.Scheme == TxFrameSignature.SchemeArbitrary && signature.Signer is not null)
                {
                    error = ArbitrarySignatureWithSigner;
                    return false;
                }

                int msgLength = signature.Msg.Length;
                if (msgLength != 0 && msgLength != 32)
                {
                    error = InvalidMsgLength;
                    return false;
                }

                if (msgLength == 32 && signature.Msg.Span.IsZero())
                {
                    error = ZeroDigestMsg;
                    return false;
                }
            }
        }

        bool hasBlobs = transaction.BlobVersionedHashes is { Length: > 0 };
        if (!hasBlobs && transaction.MaxFeePerBlobGas is { IsZero: false })
        {
            error = BlobFeeWithoutBlobs;
            return false;
        }

        return true;
    }

    /// <summary>
    /// EIP-8288 dependency-verification frame constraints: null target, zero value/flags, data a
    /// non-empty multiple of 96 bytes with each triple 31-byte zero-padded, a known scheme, at most
    /// 256 triples, and a gas limit equal to the sum of per-scheme verification gas. Accumulates the
    /// per-scheme counts for the caller's per-transaction limits.
    /// EIP8288-ISSUE: "None" target is modelled as null (empty RLP), matching EIP-8141.
    /// </summary>
    private static bool IsWellFormedDependencyFrame(TxFrame frame, ref int sphincsDeps, ref int starkDeps, out string? error)
    {
        error = null;

        if (frame.Target is not null || !frame.Value.IsZero || frame.Flags != 0)
        {
            error = DependencyFrameShape;
            return false;
        }

        ReadOnlySpan<byte> data = frame.Data.Span;
        if (data.Length == 0 || data.Length % Eip8288Constants.DependencyTripleLength != 0)
        {
            error = DependencyFrameDataLength;
            return false;
        }

        int count = data.Length / Eip8288Constants.DependencyTripleLength;
        if (count > Eip8288Constants.MaxDependenciesPerFrame)
        {
            error = TooManyDependenciesPerFrame;
            return false;
        }

        ulong expectedGas = 0;
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> triple = data.Slice(i * Eip8288Constants.DependencyTripleLength, Eip8288Constants.DependencyTripleLength);
            if (!triple[..31].IsZero())
            {
                error = DependencyPaddingNotZero;
                return false;
            }

            switch (triple[31])
            {
                case Eip8288Constants.LeanSphincsScheme:
                    expectedGas += Eip8288Constants.LeanSphincsVerificationGas;
                    sphincsDeps++;
                    break;
                case Eip8288Constants.LeanStarkScheme:
                    expectedGas += Eip8288Constants.LeanStarkVerificationGas;
                    starkDeps++;
                    break;
                default:
                    error = InvalidDependencyScheme;
                    return false;
            }
        }

        if (frame.GasLimit != expectedGas)
        {
            error = DependencyFrameGasMismatch;
            return false;
        }

        return true;
    }
}
