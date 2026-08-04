// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;

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

    /// <summary>
    /// Calculates the gas an EIP-8141 frame transaction reserves: <c>max_gas</c>, the greater of its intrinsic cost
    /// plus the sum of the frame gas limits and its EIP-7623 calldata floor.
    /// </summary>
    /// <remarks>
    /// <see cref="Transaction.GasLimit"/> of a frame transaction carries only the sum of the frame gas limits, so any
    /// consumer gating on a gas budget (mempool admission, block production, execution) must price the transaction
    /// through this method or it under-counts by at least <see cref="Eip8141Constants.IntrinsicGasCost"/>.
    /// The intrinsic cost is FRAME_TX_INTRINSIC_COST + frames × FRAME_TX_PER_FRAME_COST + the calldata cost of the
    /// frame data and signature fields + the per-scheme signature verification cost. The rate and token weighting
    /// behind <paramref name="floorGas"/> are resolved from the spec, as
    /// <c>IntrinsicGasCalculator.CalculateFloorCost</c> does, so a frame transaction's floor cannot diverge from an
    /// ordinary transaction's under the same fork. A transaction whose frames reserve less than the floor raises its
    /// reservation rather than becoming invalid; the headroom is reserved and refunded, never spendable.
    /// </remarks>
    /// <param name="transaction">The frame transaction to price.</param>
    /// <param name="spec">The release spec supplying the calldata token pricing.</param>
    /// <param name="intrinsicGas">The intrinsic cost, charged before any frame runs.</param>
    /// <param name="floorGas">The minimum chargeable gas, or 0 when floor pricing is not active.</param>
    /// <param name="maxGas">The gas reserved against the payer's balance and the block gas limit.</param>
    /// <returns><c>false</c> if the frame gas limits or the resulting budget overflow <see cref="ulong"/>.</returns>
    public static bool TryCalculateGasBudget(Transaction transaction, IReleaseSpec spec, out ulong intrinsicGas, out ulong floorGas, out ulong maxGas)
    {
        intrinsicGas = 0;
        floorGas = 0;
        maxGas = 0;

        TxFrame[]? frames = transaction.Frames;
        if (frames is null)
        {
            return false;
        }

        ulong tokens = 0;
        ulong dataLength = 0;
        ulong totalFrameGas = 0;
        foreach (TxFrame frame in frames)
        {
            tokens += CountCalldataTokens(frame.Data.Span, spec);
            dataLength += (ulong)frame.Data.Length;

            ulong accumulated = totalFrameGas + frame.GasLimit;
            if (accumulated < totalFrameGas)
            {
                return false;
            }

            totalFrameGas = accumulated;
        }

        ulong signatureVerificationCost = 0;
        TxFrameSignature[]? signatures = transaction.FrameSignatures;
        if (signatures is not null)
        {
            foreach (TxFrameSignature signature in signatures)
            {
                tokens += signature.Signer is null ? 0 : CountCalldataTokens(signature.Signer.Bytes, spec);
                tokens += CountCalldataTokens(signature.Msg.Span, spec);
                tokens += CountCalldataTokens(signature.Signature.Span, spec);
                dataLength += (ulong)(signature.Signer is null ? 0 : Address.Size)
                              + (ulong)signature.Msg.Length
                              + (ulong)signature.Signature.Length;
                signatureVerificationCost += signature.Scheme switch
                {
                    TxFrameSignature.SchemeArbitrary => Eip8141Constants.ArbitraryVerificationGasCost,
                    TxFrameSignature.SchemeSecp256k1 => Eip8141Constants.Secp256k1VerificationGasCost,
                    TxFrameSignature.SchemeP256 => Eip8141Constants.P256VerificationGasCost,
                    _ => 0,
                };
            }
        }

        ulong mandatoryGas = (ulong)Eip8141Constants.IntrinsicGasCost
                             + (ulong)frames.Length * (ulong)Eip8141Constants.PerFrameGasCost
                             + signatureVerificationCost;
        ulong floorTokens = spec.IsEip7976Enabled ? dataLength * spec.GasCosts.TxDataNonZeroMultiplier : tokens;
        floorGas = spec.IsEip7623Enabled ? mandatoryGas + floorTokens * spec.GasCosts.TotalCostFloorPerToken : 0;
        intrinsicGas = mandatoryGas + tokens * GasCostOf.TxDataZero;

        ulong standardGas = intrinsicGas + totalFrameGas;
        if (standardGas < intrinsicGas)
        {
            return false;
        }

        maxGas = Math.Max(standardGas, floorGas);
        return true;
    }

    private static ulong CountCalldataTokens(ReadOnlySpan<byte> data, IReleaseSpec spec)
    {
        int zeros = data.CountZeros();
        return (ulong)zeros + (ulong)(data.Length - zeros) * spec.GasCosts.TxDataNonZeroMultiplier;
    }

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
