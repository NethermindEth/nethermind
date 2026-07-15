// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Evm.TransactionProcessing;

/// <summary>
/// EIP-8141 frame transaction outer loop. Runs pre-flight (nonce + signature validation), then each
/// frame as its own EVM call under the frame-transaction execution context, applying APPROVE effects,
/// enforcing the payer gate, charging spec gas, and producing per-frame receipts.
/// https://eips.ethereum.org/EIPS/eip-8141
///
/// Remaining slices (marked EIP8141 where stubbed): atomic batches, default code, the expiry
/// verifier runtime, EIP-3529-style refund netting inside frames, shared warm/cold journal.
/// </summary>
public abstract partial class TransactionProcessorBase<TGasPolicy>
{
    private TransactionResult ExecuteFrameTx(Transaction tx, ITxTracer tracer, ExecutionOptions opts, BlockHeader header, IReleaseSpec spec)
    {
        Address sender = tx.SenderAddress!;
        Snapshot txSnapshot = WorldState.TakeSnapshot();

        // Pre-flight: nonce and protocol-validated signatures.
        UInt256 accountNonce = WorldState.GetNonce(sender);
        if (accountNonce != tx.Nonce)
        {
            TransactionResult.ErrorType nonceError = tx.Nonce < accountNonce
                ? TransactionResult.ErrorType.TransactionNonceTooLow
                : TransactionResult.ErrorType.TransactionNonceTooHigh;
            return nonceError.WithDetail("frame transaction nonce mismatch");
        }

        ValueHash256 sigHash = FrameTxSigHash.ComputeValue(tx);
        if (!FrameTxSignatureValidator.Validate(tx, in sigHash, Ecdsa, out string? signatureError))
        {
            return TransactionResult.ErrorType.MalformedTransaction.WithDetail(signatureError!);
        }

        TxFrame[] frames = tx.Frames!;
        UInt256 effectiveGasPrice = CalculateEffectiveGasPrice(tx, spec.IsEip1559Enabled, header.BaseFeePerGas, out UInt256 premiumPerGas);

        // Spec gas: tx_gas_limit = intrinsic + per-frame + calldata + signature verification
        // + sum(frame.gas_limit); the sum is overflow-checked so the processor does not depend
        // on static validation having run.
        ulong intrinsicGas = CalculateFrameTxIntrinsicGas(tx, frames, spec);
        ulong totalFrameGas = 0;
        foreach (TxFrame frame in frames)
        {
            ulong accumulated = totalFrameGas + frame.GasLimit;
            if (accumulated < totalFrameGas)
            {
                return TransactionResult.ErrorType.MalformedTransaction.WithDetail("total frame gas overflows");
            }

            totalFrameGas = accumulated;
        }

        ulong txGasLimit = intrinsicGas + totalFrameGas;
        UInt256 maxCost = (UInt256)txGasLimit * effectiveGasPrice;

        FrameTxContext frameContext = new(
            sender,
            tx.Nonce,
            frames,
            tx.FrameSignatures ?? [],
            sigHash,
            in maxCost,
            in tx.MaxPriorityFeePerGas,
            tx.DecodedMaxFeePerGas,
            tx.MaxFeePerBlobGas.GetValueOrDefault());

        TxFrameReceipt[] frameReceipts = new TxFrameReceipt[frames.Length];
        List<LogEntry> allLogs = [];
        ulong totalFrameGasUsed = 0;

        for (int i = 0; i < frames.Length; i++)
        {
            TxFrame frame = frames[i];
            frameContext.CurrentFrameIndex = i;

            // Transient storage (TSTORE/TLOAD) is discarded between frames (spec: Cross-frame
            // interactions); resetting at frame entry also covers the first frame.
            WorldState.ResetTransient();

            bool isSender = frame.Mode == TxFrame.ModeSender;
            if (isSender && !frameContext.SenderApproved)
            {
                WorldState.Restore(txSnapshot);
                return TransactionResult.ErrorType.MalformedTransaction.WithDetail("SENDER frame before execution approval");
            }

            Address resolvedTarget = frame.Target ?? sender;
            Address caller = isSender ? sender : Eip8141Constants.EntryPointAddress;
            bool isStatic = frame.Mode == TxFrame.ModeVerify;

            // ORIGIN returns the frame's caller throughout all call depths.
            VirtualMachine.SetTxExecutionContext(new TxExecutionContext(
                caller, _codeInfoRepository, tx.BlobVersionedHashes, in effectiveGasPrice, frameContext));

            TransactionSubstate substate = ExecuteFrame(frame, resolvedTarget, caller, isStatic, frameContext, spec, tracer, out ulong frameGasUsed);
            totalFrameGasUsed += frameGasUsed;

            bool frameSucceeded = !substate.ShouldRevert && !substate.IsError;
            if (frameSucceeded)
            {
                frameContext.MarkFrameSucceeded(i);
            }

            LogEntry[] frameLogs = frameSucceeded && substate.Logs.Count != 0 ? substate.LogsToArray() : [];
            frameReceipts[i] = new TxFrameReceipt(
                frameSucceeded ? TxFrameReceipt.StatusSuccess : TxFrameReceipt.StatusFailure,
                frameGasUsed,
                frameLogs);
            allLogs.AddRange(frameLogs);

            if (frame.Mode == TxFrame.ModeVerify && !frameSucceeded)
            {
                // A failed VERIFY frame invalidates the whole transaction.
                WorldState.Restore(txSnapshot);
                return TransactionResult.ErrorType.MalformedTransaction.WithDetail("VERIFY frame reverted");
            }

            if (frameSucceeded)
            {
                ApplyApproval(frameContext, resolvedTarget, spec);
            }
            else
            {
                // An APPROVE that terminated an inner call can leave a signal behind even though
                // the enclosing frame reverted; its effects must not apply.
                frameContext.ApprovalScopeSignal = 0;
            }
        }

        if (frameContext.Payer is null)
        {
            WorldState.Restore(txSnapshot);
            return TransactionResult.ErrorType.MalformedTransaction.WithDetail("frame transaction never set a payer");
        }

        // EIP8141: EIP-3529-style refund netting inside frames is not applied yet; spent gas is
        // intrinsic plus the gas each frame consumed.
        ulong spentGas = intrinsicGas + totalFrameGasUsed;
        Address payer = frameContext.Payer;

        // The payer was charged the max cost at payment approval; refund the unused remainder and
        // pay the beneficiary premium. The base-fee share stays deducted (burned).
        UInt256 spentCost = (UInt256)spentGas * effectiveGasPrice;
        if (maxCost > spentCost)
        {
            WorldState.AddToBalance(payer, maxCost - spentCost, spec);
        }

        UInt256 fees = premiumPerGas * (UInt256)spentGas;
        if (!fees.IsZero)
        {
            WorldState.AddToBalanceAndCreateIfNotExists(header.GasBeneficiary!, fees, spec);
        }

        bool commit = opts.HasFlag(ExecutionOptions.Commit);
        if (commit)
        {
            WorldState.Commit(spec, commitRoots: false);
            header.GasUsed += spentGas;
        }

        if (opts.HasFlag(ExecutionOptions.Restore))
        {
            WorldState.Restore(txSnapshot);
        }

        if (tracer.IsTracingReceipt)
        {
            if (tracer is IFrameTxReceiptTracer frameReceiptTracer)
            {
                frameReceiptTracer.ReportFrameTxReceipt(payer, frameReceipts);
            }

            GasConsumed gasConsumed = new(spentGas, spentGas, spentGas);
            tracer.MarkAsSuccess(Eip8141Constants.EntryPointAddress, in gasConsumed, [], allLogs.ToArray());
        }

        return TransactionResult.Ok;
    }

    /// <summary>
    /// FRAME_TX_INTRINSIC_COST + frames × FRAME_TX_PER_FRAME_COST + calldata cost of frame data
    /// and signature fields (EIP-7623 token pricing) + per-scheme signature verification cost.
    /// EIP8141: whether the EIP-7623 floor applies to frame transactions is unspecified; the
    /// standard token cost is used here.
    /// </summary>
    private static ulong CalculateFrameTxIntrinsicGas(Transaction tx, TxFrame[] frames, IReleaseSpec spec)
    {
        ulong tokens = 0;
        foreach (TxFrame frame in frames)
        {
            tokens += CountCalldataTokens(frame.Data.Span, spec);
        }

        ulong signatureVerificationCost = 0;
        TxFrameSignature[]? signatures = tx.FrameSignatures;
        if (signatures is not null)
        {
            foreach (TxFrameSignature signature in signatures)
            {
                tokens += signature.Signer is null ? 0 : CountCalldataTokens(signature.Signer.Bytes, spec);
                tokens += CountCalldataTokens(signature.Msg.Span, spec);
                tokens += CountCalldataTokens(signature.Signature.Span, spec);
                signatureVerificationCost += signature.Scheme switch
                {
                    TxFrameSignature.SchemeSecp256k1 => Eip8141Constants.Secp256k1VerificationGasCost,
                    TxFrameSignature.SchemeP256 => Eip8141Constants.P256VerificationGasCost,
                    _ => 0,
                };
            }
        }

        return (ulong)Eip8141Constants.IntrinsicGasCost
               + (ulong)frames.Length * (ulong)Eip8141Constants.PerFrameGasCost
               + tokens * GasCostOf.TxDataZero
               + signatureVerificationCost;
    }

    private static ulong CountCalldataTokens(ReadOnlySpan<byte> data, IReleaseSpec spec)
    {
        int zeros = data.CountZeros();
        return (ulong)zeros + (ulong)(data.Length - zeros) * spec.GasCosts.TxDataNonZeroMultiplier;
    }

    private TransactionSubstate ExecuteFrame(TxFrame frame, Address resolvedTarget, Address caller, bool isStatic, FrameTxContext frameContext, IReleaseSpec spec, ITxTracer tracer, out ulong gasUsed)
    {
        // As with an ordinary CALL, a caller unable to fund the value transfer reverts the frame.
        UInt256 value = frame.Value;
        if (!value.IsZero && WorldState.GetBalance(caller) < value)
        {
            gasUsed = 0;
            return new TransactionSubstate(EvmExceptionType.Revert, tracer.IsTracingInstructions);
        }

        // Default code: a codeless target (empty code hash, no EIP-7702 delegation indicator) runs
        // the protocol-defined behavior instead of the EVM.
        if (WorldState.GetCodeHash(resolvedTarget) == Keccak.OfAnEmptyString)
        {
            return ExecuteDefaultCode(frame, resolvedTarget, caller, isStatic, frameContext, spec, tracer, out gasUsed);
        }

        CodeInfo codeInfo = _codeInfoRepository.GetCachedCodeInfo(resolvedTarget, spec, out _);
        ReadOnlyMemory<byte> inputData = frame.Data;

        ExecutionEnvironment env = ExecutionEnvironment.Rent(
            codeInfo: codeInfo,
            executingAccount: resolvedTarget,
            caller: caller,
            codeSource: resolvedTarget,
            callDepth: 0,
            value: in value,
            inputData: in inputData);

        Snapshot snapshot = WorldState.TakeSnapshot();
        if (!value.IsZero)
        {
            // The VM credits the executing account; the caller-side debit is the processor's job.
            WorldState.SubtractFromBalance(caller, in value, spec);
        }

        // EIP8141: a fresh tracker per frame resets EIP-2929 warm/cold state, but the spec shares
        // the warm/cold journal across frames (with per-frame revert of touches) — restructure
        // together with the gas-accounting refinement.
        StackAccessTracker accessTracker = new();

        using VmState<TGasPolicy> state = VmState<TGasPolicy>.RentTopLevel(
            TGasPolicy.FromULong(frame.GasLimit),
            isStatic ? ExecutionType.STATICCALL : ExecutionType.TRANSACTION,
            env,
            in accessTracker,
            in snapshot);

        TransactionSubstate substate = VirtualMachine.ExecuteTransaction(state, WorldState, tracer);

        ulong remainingGas = substate.IsError ? 0 : TGasPolicy.GetRemainingGas(in state.Gas);
        gasUsed = frame.GasLimit - remainingGas;

        if (substate.ShouldRevert || substate.IsError)
        {
            WorldState.Restore(snapshot);
        }

        return substate;
    }

    /// <summary>
    /// EIP-8141 default code for a codeless target. VERIFY: require a canonical-hash SECP256K1
    /// signature at index 0 whose resolved signer is the target, then signal APPROVE with the
    /// frame's allowed scope. SENDER/DEFAULT: succeed as empty code (value transfer only).
    /// The signature's cryptographic validity is already checked in pre-flight; default code checks
    /// only the structural conditions the spec pins.
    /// EIP8141-ISSUE: the spec reads the signature from the hoisted <c>signatures</c> list at index
    /// 0; the ethrex public devnet carries it in the VERIFY frame's data instead — an open
    /// cross-client divergence to raise upstream. This follows the spec (hoisted list).
    /// EIP8141: default-code gas metering is pending; the sig verification cost is already charged
    /// in the intrinsic, so no gas is charged here yet.
    /// </summary>
    private TransactionSubstate ExecuteDefaultCode(TxFrame frame, Address resolvedTarget, Address caller, bool isStatic, FrameTxContext frameContext, IReleaseSpec spec, ITxTracer tracer, out ulong gasUsed)
    {
        gasUsed = 0;

        if (isStatic)
        {
            byte allowedScope = frame.AllowedApproveScope;
            if (allowedScope == 0)
            {
                return new TransactionSubstate(EvmExceptionType.Revert, tracer.IsTracingInstructions);
            }

            TxFrameSignature[] signatures = frameContext.Signatures;
            if (signatures.Length == 0
                || signatures[0].Scheme != TxFrameSignature.SchemeSecp256k1
                || !signatures[0].Msg.IsEmpty
                || frameContext.ResolvedSigner(0) != resolvedTarget)
            {
                return new TransactionSubstate(EvmExceptionType.Revert, tracer.IsTracingInstructions);
            }

            frameContext.ApprovalScopeSignal = allowedScope;
            return DefaultCodeSuccess();
        }

        // SENDER / DEFAULT: as if calling empty code — perform only the value transfer.
        UInt256 value = frame.Value;
        if (!value.IsZero)
        {
            WorldState.SubtractFromBalance(caller, in value, spec);
            WorldState.AddToBalanceAndCreateIfNotExists(resolvedTarget, in value, spec);
        }

        return DefaultCodeSuccess();
    }

    private static TransactionSubstate DefaultCodeSuccess() =>
        new(ReadOnlyMemory<byte>.Empty, refund: 0, destroyList: null, logs: null, shouldRevert: false);

    private void ApplyApproval(FrameTxContext frameContext, Address resolvedTarget, IReleaseSpec spec)
    {
        // Approval validity (scope allowance, re-approval, target, prior execution approval, payer
        // balance) is enforced by the APPROVE handler, which reverts the frame on violation; the
        // outer loop only forwards signals of successfully completed frames.
        byte scope = frameContext.ApprovalScopeSignal;
        if (scope == 0) return;
        frameContext.ApprovalScopeSignal = 0;

        if ((scope & TxFrame.ApproveExecution) != 0)
        {
            frameContext.SenderApproved = true;
        }

        if ((scope & TxFrame.ApprovePayment) != 0)
        {
            // Re-checked at charge time: the frame may have moved the payer's balance after an
            // APPROVE issued from an inner call, and the debit must never throw mid-block. A void
            // payment leaves Payer unset, so the transaction fails the payer gate unless a later
            // frame approves payment.
            if (WorldState.GetBalance(resolvedTarget) < frameContext.MaxCost) return;

            // Charge the max cost up front from the payer and consume the sender nonce; unused
            // gas is refunded to the payer at the end of the transaction.
            WorldState.SubtractFromBalance(resolvedTarget, frameContext.MaxCost, spec);
            WorldState.IncrementNonce(frameContext.Sender);
            frameContext.Payer = resolvedTarget;
        }
    }
}
