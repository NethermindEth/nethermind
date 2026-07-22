// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;

namespace Nethermind.Core;

/// <summary>
/// Static validity constraints of EIP-8141 frame transactions (the spec "Constraints" block).
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
    public const string AtomicBatchOnVerifyFrame = "the atomic batch flag must not be set on a VERIFY frame";
    public const string AtomicBatchFollowedByVerifyFrame = "an atomic batch frame must not be followed by a VERIFY frame";
    public const string FrameGasOverflow = "total frame gas must not exceed 2^64 - 1";
    public const string InvalidExpiryFrame = "expiry verifier frame must have zero flags, zero value, and 8-byte data";
    public const string MultipleExpiryFrames = "at most one expiry verifier frame is allowed";
    public const string InvalidSignatureScheme = "unknown signature scheme";
    public const string ArbitrarySignatureWithSigner = "ARBITRARY signatures must not name a signer";
    public const string InvalidMsgLength = "signature msg must be empty or a 32-byte digest";
    public const string ZeroDigestMsg = "explicit signature msg must not be the zero digest";
    public const string BlobFeeWithoutBlobs = "max fee per blob gas must be 0 when there are no blob hashes";

    public static bool IsWellFormed(Transaction transaction, out string? error)
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
        for (int i = 0; i < frames.Length; i++)
        {
            TxFrame frame = frames[i];

            if (frame.Mode > TxFrame.ModeSender)
            {
                error = InvalidMode;
                return false;
            }

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

            // EIP-8141 (ethereum/EIPs#11955): atomic batches contain only non-VERIFY frames — the
            // flagged frame and its successor must both be non-VERIFY.
            if ((frame.Flags & TxFrame.AtomicBatchFlag) != 0)
            {
                if (frame.Mode == TxFrame.ModeVerify)
                {
                    error = AtomicBatchOnVerifyFrame;
                    return false;
                }

                if (i + 1 < frames.Length && frames[i + 1].Mode == TxFrame.ModeVerify)
                {
                    error = AtomicBatchFollowedByVerifyFrame;
                    return false;
                }
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

            ulong accumulated = totalFrameGas + frame.GasLimit;
            if (accumulated < totalFrameGas)
            {
                error = FrameGasOverflow;
                return false;
            }

            totalFrameGas = accumulated;
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
}
