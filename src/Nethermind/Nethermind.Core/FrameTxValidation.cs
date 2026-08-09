// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
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
    public const string ApprovalScopeInAtomicBatch = "frames belonging to an atomic batch must not carry approval scope";
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

            if (BelongsToAtomicBatch(frames, i) && (frame.Flags & TxFrame.ApproveScopeMask) != 0)
            {
                error = ApprovalScopeInAtomicBatch;
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

        // EIP-8141: a frame belongs to a batch when flagged, or when it is the terminating frame
        // immediately after a flagged one (approval scope is forbidden on either).
        static bool BelongsToAtomicBatch(TxFrame[] frames, int i) =>
            (frames[i].Flags & TxFrame.AtomicBatchFlag) != 0
            || (i > 0 && (frames[i - 1].Flags & TxFrame.AtomicBatchFlag) != 0);
    }

    /// <summary>The gas charged for verifying a signature of the given EIP-8141 scheme.</summary>
    public static ulong SignatureVerificationGas(byte scheme) => scheme switch
    {
        TxFrameSignature.SchemeArbitrary => Eip8141Constants.ArbitraryVerificationGasCost,
        TxFrameSignature.SchemeSecp256k1 => Eip8141Constants.Secp256k1VerificationGasCost,
        TxFrameSignature.SchemeP256 => Eip8141Constants.P256VerificationGasCost,
        _ => 0,
    };

    /// <summary>
    /// An upper bound on the public-mempool validation work of <paramref name="transaction"/>: the gas limits
    /// of its validation prefix plus the cost of verifying its signatures, saturating at <see cref="ulong.MaxValue"/>.
    /// </summary>
    /// <remarks>
    /// Derived from the frame layout alone, so no state is read. Each layout of EIP-8141 "Public
    /// Mempool-recognized Validation Prefixes" ends in a <c>VERIFY</c> frame targeting the sender, whose
    /// approval is protocol-defined, so the prefix provably ends there. Under any other layout approval
    /// depends on code at an attacker-chosen target, so the whole frame list is charged. Signature
    /// validation counts against the same budget per EIP-8141 "Validation Prefix".
    /// </remarks>
    /// <param name="transaction">The frame transaction to price.</param>
    public static ulong ValidationWorkGas(Transaction transaction)
    {
        TxFrame[] frames = transaction.Frames ?? [];
        int counted = RecognizedPrefixLength(frames, transaction.SenderAddress) ?? frames.Length;

        ulong total = 0;
        for (int i = 0; i < counted; i++)
        {
            total = Saturating(total, frames[i].GasLimit);
        }

        foreach (TxFrameSignature signature in transaction.FrameSignatures ?? [])
        {
            total = Saturating(total, SignatureVerificationGas(signature.Scheme));
        }

        return total;
    }

    /// <summary>
    /// The number of leading frames forming a validation prefix EIP-8141 recognizes for the public
    /// mempool, or <c>null</c> when the layout matches none of them.
    /// </summary>
    private static int? RecognizedPrefixLength(TxFrame[] frames, Address? sender)
    {
        int next = 0;
        if (next < frames.Length && IsExpiryVerify(frames[next])) next++;
        if (next < frames.Length && IsDeploy(frames[next])) next++;

        if (next < frames.Length && IsSelfTargetedVerify(frames[next], TxFrame.ApproveExecutionAndPayment, sender))
        {
            return next + 1;
        }

        if (next + 1 < frames.Length
            && IsSelfTargetedVerify(frames[next], TxFrame.ApproveExecution, sender)
            && IsPay(frames[next + 1]))
        {
            return next + 2;
        }

        return null;
    }

    private static ulong Saturating(ulong total, ulong addend) =>
        addend > ulong.MaxValue - total ? ulong.MaxValue : total + addend;

    private static bool IsExpiryVerify(TxFrame frame) =>
        frame.Mode == TxFrame.ModeVerify
        && frame.Flags == TxFrame.ApproveScopeNone
        && frame.Target == Eip8141Constants.ExpiryVerifierAddress;

    private static bool IsDeploy(TxFrame frame) =>
        frame.Mode == TxFrame.ModeDefault && frame.Flags == TxFrame.ApproveScopeNone;

    /// <remarks>
    /// Comparing the whole <see cref="TxFrame.Flags"/> byte rather than the approve scope also enforces
    /// the EIP-8141 structural rule that no prefix frame carries <c>ATOMIC_BATCH_FLAG</c>.
    /// </remarks>
    private static bool IsSelfTargetedVerify(TxFrame frame, byte flags, Address? sender) =>
        frame.Mode == TxFrame.ModeVerify
        && frame.Flags == flags
        && (frame.Target is null || frame.Target == sender);

    private static bool IsPay(TxFrame frame) =>
        frame.Mode == TxFrame.ModeVerify && frame.Flags == TxFrame.ApprovePayment;

    /// <summary>
    /// Reads the EIP-8141 expiry deadline (Unix seconds) from the expiry-verifier VERIFY frame, if present.
    /// </summary>
    /// <remarks>
    /// The deadline is the big-endian <c>uint64</c> in that frame's 8-byte data; a tx whose deadline has passed can
    /// never be included and is dropped from the mempool (ethereum/EIPs#12007, "Revalidation"). Must be called only
    /// on well-formed frame txs: <see cref="IsWellFormed"/> already enforces the
    /// <see cref="Eip8141Constants.ExpiryDataLength"/> length, so it is not re-checked here.
    /// </remarks>
    /// <param name="transaction">The frame transaction to inspect.</param>
    /// <param name="deadline">The expiry deadline in Unix seconds when an expiry-verifier frame is present.</param>
    /// <returns><c>true</c> if an expiry-verifier frame is present and its deadline was read; otherwise <c>false</c>.</returns>
    /// <exception cref="System.ArgumentOutOfRangeException">
    /// The expiry frame carries fewer than <see cref="Eip8141Constants.ExpiryDataLength"/> bytes, i.e. the
    /// <see cref="IsWellFormed"/> precondition was not met.
    /// </exception>
    public static bool TryGetExpiryDeadline(Transaction transaction, out ulong deadline)
    {
        deadline = 0;

        TxFrame[]? frames = transaction.Frames;
        if (frames is null)
        {
            return false;
        }

        for (int i = 0; i < frames.Length; i++)
        {
            TxFrame frame = frames[i];
            if (frame.Mode == TxFrame.ModeVerify
                && frame.Target == Eip8141Constants.ExpiryVerifierAddress)
            {
                deadline = BinaryPrimitives.ReadUInt64BigEndian(frame.Data.Span);
                return true;
            }
        }

        return false;
    }
}
