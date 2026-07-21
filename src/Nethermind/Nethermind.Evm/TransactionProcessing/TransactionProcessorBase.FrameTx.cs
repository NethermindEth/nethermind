// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;
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
/// Remaining slice (marked EIP8141 where stubbed): EIP-3529-style refund netting inside frames
/// (deliberately deferred — the spec is silent and the accounting is proposed upstream).
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
        IPrecompile? p256Precompile = _codeInfoRepository.GetCachedCodeInfo(FrameTxSignatureValidator.P256VerifyPrecompileAddress, spec, out _).Precompile;
        if (!FrameTxSignatureValidator.Validate(tx, in sigHash, Ecdsa, p256Precompile, spec, out string? signatureError))
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

        // EIP-2929 warm/cold journal shared across frames (spec: Cross-frame interactions).
        // Frame targets are warmed per frame; the sender and coinbase once per transaction, like
        // origin/coinbase on a regular transaction. EIP8141: whether ENTRY_POINT-as-caller should
        // be warm is unspecified — left cold.
        using StackAccessTracker accessTracker = new(tracer.IsTracingAccess);
        if (spec.UseHotAndColdStorage)
        {
            if (spec.AddCoinbaseToTxAccessList)
            {
                accessTracker.WarmUp(header.GasBeneficiary!);
            }

            accessTracker.WarmUp(sender);
        }

        // Atomic batch state: a maximal contiguous run [i, j] where i..j-1 have ATOMIC_BATCH_FLAG
        // and j does not. On any failure inside the run, state rolls back to before the run began
        // and the remaining frames are skipped (spec: Behavior, atomic batch).
        bool inBatch = false;
        Snapshot batchStartSnapshot = default;
        StackAccessTracker batchTracker = default;
        int batchStartLogCount = 0;

        for (int i = 0; i < frames.Length; i++)
        {
            TxFrame frame = frames[i];
            frameContext.CurrentFrameIndex = i;

            // A batch begins at the first flagged frame; snapshot the state and log count before it.
            if (!inBatch && frame.IsAtomicBatch)
            {
                inBatch = true;
                batchStartSnapshot = WorldState.TakeSnapshot();
                batchTracker = accessTracker;
                batchTracker.TakeSnapshot();
                batchStartLogCount = allLogs.Count;
            }

            // EIP-8288 dependency-verification frame: not executed as EVM (dependencies are proven by
            // the block-level recursive STARK). Its full gas_limit is charged unconditionally and it
            // always yields a success receipt with no logs; being unflagged, it commits any open batch.
            if (frame.Mode == TxFrame.ModeDepVerify)
            {
                frameContext.MarkFrameSucceeded(i);
                frameReceipts[i] = new TxFrameReceipt(TxFrameReceipt.StatusSuccess, frame.GasLimit, []);
                totalFrameGasUsed += frame.GasLimit;
                if (inBatch && !frame.IsAtomicBatch) inBatch = false;
                continue;
            }

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

            // The shared journal accumulates logs across frames; this frame's own logs start here.
            int frameLogStart = accessTracker.Logs.Count;
            TransactionSubstate substate = ExecuteFrame(frame, resolvedTarget, caller, isStatic, frameContext, in accessTracker, spec, tracer, out ulong frameGasUsed);
            totalFrameGasUsed += frameGasUsed;

            bool frameSucceeded = !substate.ShouldRevert && !substate.IsError;
            if (frameSucceeded)
            {
                frameContext.MarkFrameSucceeded(i);
            }

            LogEntry[] frameLogs = frameSucceeded && accessTracker.Logs.Count > frameLogStart
                ? accessTracker.Logs.Skip(frameLogStart).ToArray()
                : [];
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

            if (inBatch)
            {
                if (!frameSucceeded)
                {
                    // Unroll the batch: restore state to before it began, drop its logs, and mark
                    // every remaining frame in the batch as skipped (status 0x3, gas refunded by
                    // not being consumed). The failed frame keeps its failure receipt.
                    // EIP8141-ISSUE: the spec does not state the receipt status of frames that ran
                    // successfully earlier in the batch before the rollback, nor the fate of an
                    // APPROVE issued inside a rolled-back batch. The mempool forbids atomic-batched
                    // VERIFY frames and operation frames do not APPROVE, so this prototype supports
                    // batches of SENDER/DEFAULT operation frames only; earlier frames keep their
                    // recorded receipts while their state and logs are rolled back.
                    WorldState.Restore(batchStartSnapshot);
                    batchTracker.Restore();
                    allLogs.RemoveRange(batchStartLogCount, allLogs.Count - batchStartLogCount);

                    int terminal = i;
                    while (terminal < frames.Length && frames[terminal].IsAtomicBatch) terminal++;
                    for (int s = i + 1; s <= terminal && s < frames.Length; s++)
                    {
                        frameReceipts[s] = new TxFrameReceipt(TxFrameReceipt.StatusSkipped, 0, []);
                        frameContext.MarkFrameSkipped(s);
                    }

                    i = terminal;
                    inBatch = false;
                }
                else if (!frame.IsAtomicBatch)
                {
                    // Terminal frame reached without failure — the batch committed.
                    inBatch = false;
                }
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
        // Block-level gas accounting reads Transaction.BlockGasUsed (its getter falls back to the
        // tx GasLimit, which is 0 for a frame tx). Set it like the regular path so parallel block
        // validation (BlockAccessListManager) accumulates the frame tx's gas into the header.
        tx.BlockGasUsed = spentGas;
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

    private TransactionSubstate ExecuteFrame(TxFrame frame, Address resolvedTarget, Address caller, bool isStatic, FrameTxContext frameContext, in StackAccessTracker accessTracker, IReleaseSpec spec, ITxTracer tracer, out ulong gasUsed)
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

        // Shared journal: snapshot before warming the frame's target so a reverting frame also
        // reverts its warm/cold touches (spec: "If a frame reverts, warm / cold status reverts to
        // the state before the frame").
        StackAccessTracker frameTracker = accessTracker;
        frameTracker.TakeSnapshot();
        if (spec.UseHotAndColdStorage)
        {
            frameTracker.WarmUp(resolvedTarget);
        }

        using VmState<TGasPolicy> state = VmState<TGasPolicy>.RentTopLevel(
            TGasPolicy.FromULong(frame.GasLimit),
            isStatic ? ExecutionType.STATICCALL : ExecutionType.TRANSACTION,
            env,
            in frameTracker,
            in snapshot);

        TransactionSubstate substate = VirtualMachine.ExecuteTransaction(state, WorldState, tracer);

        ulong remainingGas = substate.IsError ? 0 : TGasPolicy.GetRemainingGas(in state.Gas);
        gasUsed = frame.GasLimit - remainingGas;

        if (substate.ShouldRevert || substate.IsError)
        {
            WorldState.Restore(snapshot);
            frameTracker.Restore();
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

            // The expected signature index depends on the frame's scope: execution (or both) reads
            // index 0; a payment-only verifier (a codeless EOA sponsor) reads index 1
            // (ethereum/EIPs#11954).
            int sigIndex = (allowedScope & TxFrame.ApproveExecution) != 0 ? 0 : 1;
            TxFrameSignature[] signatures = frameContext.Signatures;
            if (signatures.Length <= sigIndex
                || signatures[sigIndex].Scheme != TxFrameSignature.SchemeSecp256k1
                || !signatures[sigIndex].Msg.IsEmpty
                || frameContext.ResolvedSigner(sigIndex) != resolvedTarget)
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
