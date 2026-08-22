// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Threading;
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
    public const string InvalidMode = "frame mode must be DEFAULT, VERIFY, SENDER, or POST_TX";
    public const string PostTxNotTrailing = "POST_TX frames must form a trailing suffix of the frame list";
    public const string PostTxNotEnabled = "POST_TX frames are not enabled";
    public const string InvalidFlags = "frame flags must not use reserved bits";
    public const string ValueOutsideSenderMode = "frame value is only allowed in SENDER mode";
    public const string ExecutionApprovalWrongTarget = "frames allowed to approve execution must target the sender";
    public const string AtomicBatchOnLastFrame = "the last frame must not have the atomic batch flag set";
    public const string AtomicBatchOnVerifyFrame = "the atomic batch flag must not be set on a VERIFY frame";
    public const string AtomicBatchOnPostTxFrame = "the atomic batch flag must not be set on a POST_TX frame";
    public const string AtomicBatchFollowedByVerifyFrame = "an atomic batch frame must not be followed by a VERIFY frame";
    public const string AtomicBatchFollowedByPostTxFrame = "an atomic batch frame must not be followed by a POST_TX frame";
    public const string ApprovalScopeInAtomicBatch = "frames belonging to an atomic batch must not carry approval scope";
    public const string FrameGasOverflow = "total frame gas must not exceed 2^64 - 1";
    public static string FrameExecutionGasExceedsCap(ulong executionReservation, ulong gasLimitCap) =>
        $"frame intrinsic and execution gas ({executionReservation}) exceeds the transaction gas cap of {gasLimitCap}";
    public const string InvalidExpiryFrame = "expiry verifier frame must have zero flags, zero value, and 8-byte data";
    public const string MultipleExpiryFrames = "at most one expiry verifier frame is allowed";
    public const string InvalidSignatureScheme = "unknown signature scheme";
    public const string ArbitrarySignatureWithSigner = "ARBITRARY signatures must not name a signer";
    public const string InvalidMsgLength = "signature msg must be empty or a 32-byte digest";
    public const string ZeroDigestMsg = "explicit signature msg must not be the zero digest";
    public const string BlobFeeWithoutBlobs = "max fee per blob gas must be 0 when there are no blob hashes";
    public const string KeyedNoncesNotEnabled = "keyed nonces are not enabled";
    public const string LegacyNonceNotAllowed = "legacy nonce is not allowed";
    public const string MalformedNonceKeySet = "malformed nonce key set";
    public const string TooManyRecentRootReferences = "at most 16 recent root references are allowed";

    public static bool IsWellFormed(Transaction transaction, bool postTxEnabled, out string? error)
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

            if (frame.Mode > TxFrame.ModePostTx)
            {
                error = InvalidMode;
                return false;
            }

            if (frame.Mode == TxFrame.ModePostTx && !postTxEnabled)
            {
                error = PostTxNotEnabled;
                return false;
            }

            // Assertions observe the finished transaction, so nothing may run after them.
            if (frame.Mode == TxFrame.ModePostTx)
            {
                if (i + 1 < frames.Length && frames[i + 1].Mode != TxFrame.ModePostTx)
                {
                    error = PostTxNotTrailing;
                    return false;
                }

                // A batch opened by a POST_TX frame can never unroll — the assertion path exits the
                // frame loop first — so the flag would be inert here and clients could diverge on it.
                if ((frame.Flags & TxFrame.AtomicBatchFlag) != 0)
                {
                    error = AtomicBatchOnPostTxFrame;
                    return false;
                }
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

                // An unroll moves the terminal frame onto the successor and marks it skipped. On a POST_TX
                // successor that silently drops the assertion, which is the case it exists to catch.
                if (i + 1 < frames.Length && frames[i + 1].Mode == TxFrame.ModePostTx)
                {
                    error = AtomicBatchFollowedByPostTxFrame;
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
                if (frame.Flags != 0 || !frame.Value.IsZero || frame.StateGasLimit != 0 || frame.Data.Length != Eip8141Constants.ExpiryDataLength)
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

            ulong frameGas = frame.ExecutionGasLimit + frame.StateGasLimit;
            ulong accumulated = totalFrameGas + frameGas;
            if (frameGas < frame.ExecutionGasLimit || accumulated < totalFrameGas)
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

        if (transaction.RecentRootReferences is { Length: > Eip8272Constants.MaxRecentRootReferences })
        {
            error = TooManyRecentRootReferences;
            return false;
        }

        // A value check, not a presence check: the decoder always populates both blob fields. Refusing a
        // blob-carrying frame tx here would be a block-validity rule, since BlockValidator reaches this.
        bool hasBlobs = transaction.BlobVersionedHashes is { Length: > 0 };
        if (!hasBlobs && transaction.MaxFeePerBlobGas is { IsZero: false })
        {
            error = BlobFeeWithoutBlobs;
            return false;
        }

        // The EIP-7594 blob-count limit and versioned-hash version byte need the release spec, so they
        // are enforced by FrameTxFieldsTxValidator rather than in this stateless check.
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
    /// Upper bound on the public-mempool validation work of a frame transaction: its validation prefix's
    /// execution limits (EIP-8141 <c>MAX_VERIFY_GAS</c>) plus signature verification, saturating at
    /// <see cref="ulong.MaxValue"/>. The prefix's <c>limits.state</c> is bounded separately by <c>MAX_VERIFY_STATE_GAS</c>.
    /// </summary>
    public static ulong ValidationWorkGas(Transaction transaction)
    {
        TxFrame[] frames = transaction.Frames ?? [];
        int counted = RecognizedPrefixLength(frames, transaction.SenderAddress) ?? frames.Length;

        ulong total = 0;
        for (int i = 0; i < counted; i++)
        {
            total = Saturating(total, frames[i].ExecutionGasLimit);
        }

        foreach (TxFrameSignature signature in transaction.FrameSignatures ?? [])
        {
            total = Saturating(total, SignatureVerificationGas(signature.Scheme));
        }

        return total;
    }

    /// <summary>
    /// Upper bound on the state growth EIP-8141 admits through the public mempool: the sum of a frame
    /// transaction's validation prefix <c>limits.state</c>, saturating at <see cref="ulong.MaxValue"/> and
    /// bounded separately by <c>MAX_VERIFY_STATE_GAS</c>.
    /// </summary>
    public static ulong ValidationWorkStateGas(Transaction transaction)
    {
        TxFrame[] frames = transaction.Frames ?? [];
        int counted = RecognizedPrefixLength(frames, transaction.SenderAddress) ?? frames.Length;

        ulong total = 0;
        for (int i = 0; i < counted; i++)
        {
            total = Saturating(total, frames[i].StateGasLimit);
        }

        return total;
    }

    /// <summary>
    /// True if <paramref name="transaction"/> carries a <c>VERIFY</c> frame behind its recognized
    /// validation prefix, which EIP-8141 bars from the public mempool.
    /// </summary>
    /// <remarks>
    /// A public-mempool rule, not a validity rule: such a transaction stays consensus-valid. A VERIFY frame
    /// that reverts invalidates the whole transaction, so one sitting past the prefix does so on state the
    /// pool never validated. Judged against the same prefix grammar <see cref="ValidationWorkGas"/> prices
    /// admission with, so a layout matching none of the recognized prefixes has no boundary to sit behind
    /// and is left to the rules that reject it on their own terms. That leaves the equivalent hole open for
    /// unrecognized layouts until the prefix structure itself is enforced.
    /// </remarks>
    /// <param name="transaction">The frame transaction to inspect.</param>
    public static bool HasVerifyFrameAfterPrefix(Transaction transaction)
    {
        TxFrame[] frames = transaction.Frames ?? [];
        if (RecognizedPrefixLength(frames, transaction.SenderAddress) is not int prefixLength)
        {
            return false;
        }

        for (int i = prefixLength; i < frames.Length; i++)
        {
            if (frames[i].Mode == TxFrame.ModeVerify)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True if <paramref name="transaction"/> carries an expiry-verifier frame anywhere but at the head of
    /// its frame list, the only placement EIP-8141 permits.
    /// </summary>
    /// <remarks>
    /// A public-mempool rule, not a validity rule: an expiry frame's shape and uniqueness are validated but
    /// never its position, so such a transaction stays consensus-valid. The pool needs the placement because
    /// it reads the deadline from the leading frame alone (<see cref="TryGetExpiryDeadline"/>); a misplaced
    /// frame would otherwise carry a deadline the expiry sweep can never see.
    /// </remarks>
    /// <param name="transaction">The frame transaction to inspect.</param>
    public static bool HasMisplacedExpiryFrame(Transaction transaction)
    {
        TxFrame[] frames = transaction.Frames ?? [];
        for (int i = 1; i < frames.Length; i++)
        {
            if (IsExpiryVerifyFrame(frames[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The number of leading frames forming a validation prefix EIP-8141 recognizes for the public
    /// mempool, or <c>null</c> when the layout matches none of them.
    /// </summary>
    private static int? RecognizedPrefixLength(TxFrame[] frames, Address? sender)
    {
        int next = 0;
        if (next < frames.Length && IsExpiryVerifyFrame(frames[next])) next++;
        if (next < frames.Length && IsDeployFrame(frames[next])) next++;

        if (next < frames.Length && IsSelfVerifyFrame(frames[next], sender))
        {
            return next + 1;
        }

        if (next + 1 < frames.Length
            && IsOnlyVerifyFrame(frames[next], sender)
            && IsPayFrame(frames[next + 1]))
        {
            return next + 2;
        }

        return null;
    }

    private static ulong Saturating(ulong total, ulong addend) =>
        addend > ulong.MaxValue - total ? ulong.MaxValue : total + addend;

    /// <summary>True if <paramref name="frame"/> is a well-formed EIP-8141 expiry-verifier VERIFY frame.</summary>
    /// <remarks>
    /// Position is not checked; the recognized prefix admits one only as the leading frame. The value and
    /// data-length checks are kept so a caller may read the deadline without re-validating the frame.
    /// </remarks>
    public static bool IsExpiryVerifyFrame(TxFrame frame) =>
        frame.Mode == TxFrame.ModeVerify
        && frame.Flags == TxFrame.ApproveScopeNone
        && frame.Target == Eip8141Constants.ExpiryVerifierAddress
        && frame.Value.IsZero
        && frame.Data.Length == Eip8141Constants.ExpiryDataLength;

    /// <summary>True if <paramref name="frame"/> is a deploy frame: any default-mode frame carrying no
    /// approval scope, so it can never approve a payer.</summary>
    public static bool IsDeployFrame(TxFrame frame) =>
        frame.Mode == TxFrame.ModeDefault && frame.Flags == TxFrame.ApproveScopeNone;

    /// <summary>True if <paramref name="frame"/> is a self-relay VERIFY frame approving both execution and payment for <paramref name="sender"/>.</summary>
    public static bool IsSelfVerifyFrame(TxFrame frame, Address? sender) =>
        IsSelfTargetedVerify(frame, TxFrame.ApproveExecutionAndPayment, sender);

    /// <summary>True if <paramref name="frame"/> is a VERIFY frame approving execution only (not payment) for <paramref name="sender"/>.</summary>
    public static bool IsOnlyVerifyFrame(TxFrame frame, Address? sender) =>
        IsSelfTargetedVerify(frame, TxFrame.ApproveExecution, sender);

    /// <summary>True if <paramref name="frame"/> is a VERIFY frame approving payment.</summary>
    private static bool IsPayFrame(TxFrame frame) =>
        frame.Mode == TxFrame.ModeVerify && frame.Flags == TxFrame.ApprovePayment;

    /// <remarks>
    /// Comparing the whole <see cref="TxFrame.Flags"/> byte rather than the approve scope also enforces
    /// the EIP-8141 structural rule that no prefix frame carries <c>ATOMIC_BATCH_FLAG</c>.
    /// </remarks>
    private static bool IsSelfTargetedVerify(TxFrame frame, byte flags, Address? sender) =>
        frame.Mode == TxFrame.ModeVerify
        && frame.Flags == flags
        && (frame.Target is null || frame.Target == sender);

    /// <summary>
    /// Calculates the gas an EIP-8141 frame transaction reserves: <c>max_gas</c>, the greater of its intrinsic cost
    /// plus the sum of the frame gas limits and its EIP-7623 calldata floor.
    /// </summary>
    /// <remarks>
    /// <see cref="Transaction.GasLimit"/> of a frame transaction carries only the sum of the frame gas limits, so any
    /// consumer gating on a gas budget (mempool admission, block production, execution) must price the transaction
    /// through this method or it under-counts by at least <see cref="Eip8141Constants.IntrinsicGasCost"/>.
    /// A transaction whose frames reserve less than the floor raises its reservation rather than becoming invalid;
    /// the headroom is reserved and refunded, never spendable.
    /// The result is memoized on <see cref="Transaction.IntrinsicGasMemo"/>, which a frame transaction otherwise
    /// leaves unused, and is keyed on the spec reference as <c>EthereumGasPolicy</c> keys its own memo.
    /// </remarks>
    /// <param name="transaction">The frame transaction to price.</param>
    /// <param name="spec">The release spec supplying the calldata token pricing.</param>
    /// <param name="intrinsicGas">The intrinsic cost, charged before any frame runs.</param>
    /// <param name="floorGas">The minimum chargeable gas, or 0 when floor pricing is not active.</param>
    /// <param name="maxGas">The gas reserved against the payer's balance and the block gas limit.</param>
    /// <returns><c>false</c> if the transaction carries no frames, or if the frame gas limits or the resulting budget
    /// overflow <see cref="ulong"/>. In every failure case the outputs are 0 and the transaction cannot be priced.</returns>
    public static bool TryCalculateGasBudget(Transaction transaction, IReleaseSpec spec, out ulong intrinsicGas, out ulong floorGas, out ulong maxGas)
    {
        if (Volatile.Read(ref transaction.IntrinsicGasMemo) is FrameGasBudgetMemo memo && ReferenceEquals(memo.Spec, spec))
        {
            (intrinsicGas, floorGas, maxGas) = (memo.IntrinsicGas, memo.FloorGas, memo.MaxGas);
            return memo.Priced;
        }

        bool priced = CalculateGasBudget(transaction, spec, out intrinsicGas, out floorGas, out maxGas);
        Volatile.Write(ref transaction.IntrinsicGasMemo, new FrameGasBudgetMemo(spec, priced, intrinsicGas, floorGas, maxGas));
        return priced;
    }

    private sealed record FrameGasBudgetMemo(IReleaseSpec Spec, bool Priced, ulong IntrinsicGas, ulong FloorGas, ulong MaxGas) : IIntrinsicGasMemo;

    private static bool CalculateGasBudget(Transaction transaction, IReleaseSpec spec, out ulong intrinsicGas, out ulong floorGas, out ulong maxGas)
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
        ulong totalStateGas = 0;
        ulong valueTransferCost = 0;
        foreach (TxFrame frame in frames)
        {
            tokens += CountCalldataTokens(frame.Data.Span, spec);
            dataLength += (ulong)frame.Data.Length;

            ulong frameGas = frame.ExecutionGasLimit + frame.StateGasLimit;
            ulong accumulated = totalFrameGas + frameGas;
            if (frameGas < frame.ExecutionGasLimit || accumulated < totalFrameGas)
            {
                return false;
            }

            totalFrameGas = accumulated;
            totalStateGas += frame.StateGasLimit;

            if (spec.IsEip2780Enabled && !frame.Value.IsZero && frame.Target is not null && frame.Target != transaction.SenderAddress)
            {
                valueTransferCost += GasCostOf.TxValueCostEip2780;
            }
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

        if (transaction.RecentRootReferences is not null && spec.IsEip8272Enabled)
        {
            (int zeroBytes, int nonZeroBytes) = transaction.ReferenceCalldataStats;
            tokens += (ulong)zeroBytes + (ulong)nonZeroBytes * spec.GasCosts.TxDataNonZeroMultiplier;
            dataLength += (ulong)(zeroBytes + nonZeroBytes);
        }

        if (transaction.NonceKeys is not null && spec.IsEip8250Enabled)
        {
            (int zeroBytes, int nonZeroBytes) = transaction.FrameCalldataStats;
            tokens += (ulong)zeroBytes + (ulong)nonZeroBytes * spec.GasCosts.TxDataNonZeroMultiplier;
            dataLength += (ulong)(zeroBytes + nonZeroBytes);
        }

        ulong mandatoryGas = (ulong)Eip8141Constants.IntrinsicGasCost
                             + (ulong)frames.Length * (ulong)Eip8141Constants.PerFrameGasCost
                             + signatureVerificationCost
                             + valueTransferCost
                             + RecentRootReference.IntrinsicGas(transaction.RecentRootReferences, spec);
        ulong floorTokens = spec.IsEip7976Enabled ? dataLength * spec.GasCosts.TxDataNonZeroMultiplier : tokens;
        floorGas = spec.IsEip7623Enabled ? mandatoryGas + floorTokens * spec.GasCosts.TotalCostFloorPerToken : 0;
        intrinsicGas = mandatoryGas + tokens * GasCostOf.TxDataZero;

        ulong standardGas = intrinsicGas + totalFrameGas;
        if (standardGas < intrinsicGas)
        {
            return false;
        }

        ulong floorReservation = floorGas + totalStateGas;
        if (floorReservation < floorGas)
        {
            return false;
        }

        maxGas = Math.Max(standardGas, floorReservation);
        return true;
    }

    /// <summary>Calculates the maximum execution and state gas a frame transaction can add to a block.</summary>
    public static bool TryCalculateBlockGasReservations(
        Transaction transaction,
        IReleaseSpec spec,
        out ulong executionReservation,
        out ulong stateReservation)
    {
        executionReservation = 0;
        stateReservation = 0;
        if (!TryCalculateGasBudget(transaction, spec, out ulong intrinsicGas, out ulong floorGas, out _))
        {
            return false;
        }

        ulong frameExecution = 0;
        foreach (TxFrame frame in transaction.Frames ?? [])
        {
            ulong nextExecution = frameExecution + frame.ExecutionGasLimit;
            ulong nextState = stateReservation + frame.StateGasLimit;
            if (nextExecution < frameExecution || nextState < stateReservation)
            {
                return false;
            }

            frameExecution = nextExecution;
            stateReservation = nextState;
        }

        ulong standardExecution = intrinsicGas + frameExecution;
        if (standardExecution < intrinsicGas)
        {
            return false;
        }

        executionReservation = Math.Max(standardExecution, floorGas);
        return true;
    }

    private static ulong CountCalldataTokens(ReadOnlySpan<byte> data, IReleaseSpec spec)
    {
        int zeros = data.CountZeros();
        return (ulong)zeros + (ulong)(data.Length - zeros) * spec.GasCosts.TxDataNonZeroMultiplier;
    }

    /// <summary>
    /// Reads the EIP-8141 expiry deadline (Unix seconds) from the expiry-verifier VERIFY frame, or from
    /// <see cref="Transaction.PersistedExpiryDeadline"/> for a transaction reloaded without its frames.
    /// </summary>
    /// <remarks>
    /// The deadline is the big-endian <c>uint64</c> in that frame's 8-byte data; a tx whose deadline has passed can
    /// never be included and is dropped from the mempool (ethereum/EIPs#12007, "Revalidation"). Total on any input:
    /// <see cref="IsExpiryVerifyFrame"/> guards the data length this dereferences. Only the leading frame is read —
    /// the sole placement EIP-8141 permits, and the one <see cref="HasMisplacedExpiryFrame"/> keeps the pool to.
    /// </remarks>
    /// <param name="transaction">The frame transaction to inspect.</param>
    /// <param name="deadline">The expiry deadline in Unix seconds when the transaction carries one.</param>
    /// <returns><c>true</c> if a deadline was read; otherwise <c>false</c>.</returns>
    public static bool TryGetExpiryDeadline(Transaction transaction, out ulong deadline)
    {
        deadline = 0;

        TxFrame[]? frames = transaction.Frames;
        if (frames is null)
        {
            // A reloaded light record has no frames; its deadline comes back from storage instead.
            deadline = transaction.PersistedExpiryDeadline.GetValueOrDefault();
            return transaction.PersistedExpiryDeadline is not null;
        }

        if (frames.Length == 0 || !IsExpiryVerifyFrame(frames[0]))
        {
            return false;
        }

        deadline = BinaryPrimitives.ReadUInt64BigEndian(frames[0].Data.Span);
        return true;
    }
}
