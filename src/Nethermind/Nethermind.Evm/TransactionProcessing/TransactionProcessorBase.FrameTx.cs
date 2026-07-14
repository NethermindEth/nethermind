// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Evm.TransactionProcessing;

/// <summary>
/// EIP-8141 frame transaction outer loop. Runs pre-flight (nonce + signature validation), then each
/// frame as its own EVM call under the frame-transaction execution context, applying APPROVE effects
/// and enforcing that a payer is set before the transaction is valid.
/// https://eips.ethereum.org/EIPS/eip-8141
///
/// Slice 2 scope: dispatch, pre-flight, and the basic per-frame loop (VERIFY/SENDER/DEFAULT +
/// approval + payer gate). Atomic batches, default code, the expiry verifier, per-frame receipts,
/// and precise gas/refund accounting are subsequent slices — marked EIP8141 where stubbed.
/// </summary>
public abstract partial class TransactionProcessorBase<TGasPolicy>
{
    private TransactionResult ExecuteFrameTx(Transaction tx, ITxTracer tracer, ExecutionOptions opts, BlockHeader header, IReleaseSpec spec)
    {
        Address sender = tx.SenderAddress!;

        // Pre-flight: nonce and protocol-validated signatures.
        UInt256 accountNonce = WorldState.GetNonce(sender);
        if (accountNonce != tx.Nonce)
        {
            TransactionResult.ErrorType nonceError = tx.Nonce < accountNonce
                ? TransactionResult.ErrorType.TransactionNonceTooLow
                : TransactionResult.ErrorType.TransactionNonceTooHigh;
            return nonceError.WithDetail("frame transaction nonce mismatch");
        }

        if (!FrameTxSignatureValidator.Validate(tx, Ecdsa, out string? signatureError))
        {
            return TransactionResult.ErrorType.MalformedTransaction.WithDetail(signatureError!);
        }

        TxFrame[] frames = tx.Frames!;
        UInt256 effectiveGasPrice = CalculateEffectiveGasPrice(tx, spec.IsEip1559Enabled, header.BaseFeePerGas, out _);

        // EIP8141: max cost approximated as sum(frame.gas_limit) * effective price; the full formula
        // (intrinsic + per-frame + calldata + signature verification) is wired with gas accounting
        // in a later slice.
        ulong totalFrameGas = 0;
        foreach (TxFrame frame in frames) totalFrameGas += frame.GasLimit;
        UInt256 maxCost = (UInt256)totalFrameGas * effectiveGasPrice;

        FrameTxContext frameContext = new(
            sender,
            tx.Nonce,
            frames,
            tx.FrameSignatures ?? [],
            FrameTxSigHash.ComputeValue(tx),
            in maxCost,
            in tx.MaxPriorityFeePerGas,
            tx.DecodedMaxFeePerGas,
            tx.MaxFeePerBlobGas.GetValueOrDefault());

        for (int i = 0; i < frames.Length; i++)
        {
            TxFrame frame = frames[i];
            frameContext.CurrentFrameIndex = i;

            bool isSender = frame.Mode == TxFrame.ModeSender;
            if (isSender && !frameContext.SenderApproved)
            {
                return TransactionResult.ErrorType.MalformedTransaction.WithDetail("SENDER frame before execution approval");
            }

            Address resolvedTarget = frame.Target ?? sender;
            Address caller = isSender ? sender : Eip8141Constants.EntryPointAddress;
            bool isStatic = frame.Mode == TxFrame.ModeVerify;

            // ORIGIN returns the frame's caller throughout all call depths.
            VirtualMachine.SetTxExecutionContext(new TxExecutionContext(
                caller, _codeInfoRepository, tx.BlobVersionedHashes, in effectiveGasPrice, frameContext));

            TransactionSubstate substate = ExecuteFrame(frame, resolvedTarget, caller, isStatic, spec, tracer);

            frameContext.FrameCompleted[i] = true;
            frameContext.FrameSucceeded[i] = !substate.ShouldRevert && !substate.IsError;

            if (frame.Mode == TxFrame.ModeVerify && !frameContext.FrameSucceeded[i])
            {
                // A failed VERIFY frame invalidates the whole transaction.
                return TransactionResult.ErrorType.MalformedTransaction.WithDetail("VERIFY frame reverted");
            }

            ApplyApproval(frameContext, resolvedTarget, spec);
        }

        if (frameContext.Payer is null)
        {
            return TransactionResult.ErrorType.MalformedTransaction.WithDetail("frame transaction never set a payer");
        }

        return TransactionResult.Ok;
    }

    private TransactionSubstate ExecuteFrame(TxFrame frame, Address resolvedTarget, Address caller, bool isStatic, IReleaseSpec spec, ITxTracer tracer)
    {
        // As with an ordinary CALL, a caller unable to fund the value transfer reverts the frame.
        UInt256 value = frame.Value;
        if (!value.IsZero && WorldState.GetBalance(caller) < value)
        {
            return new TransactionSubstate(EvmExceptionType.Revert, tracer.IsTracingInstructions);
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

        StackAccessTracker accessTracker = new();

        using VmState<TGasPolicy> state = VmState<TGasPolicy>.RentTopLevel(
            TGasPolicy.FromULong(frame.GasLimit),
            isStatic ? ExecutionType.STATICCALL : ExecutionType.TRANSACTION,
            env,
            in accessTracker,
            in snapshot);

        TransactionSubstate substate = VirtualMachine.ExecuteTransaction(state, WorldState, tracer);

        if (substate.ShouldRevert || substate.IsError)
        {
            WorldState.Restore(snapshot);
        }

        return substate;
    }

    private void ApplyApproval(FrameTxContext frameContext, Address resolvedTarget, IReleaseSpec spec)
    {
        // Approval validity (scope allowance, re-approval, target, prior execution approval, payer
        // balance) is enforced by the APPROVE handler, which reverts the frame on violation; a
        // signal only arrives here from a successfully completed frame.
        byte scope = frameContext.ApprovalScopeSignal;
        if (scope == 0) return;
        frameContext.ApprovalScopeSignal = 0;

        if ((scope & TxFrame.ApproveExecution) != 0)
        {
            frameContext.SenderApproved = true;
        }

        if ((scope & TxFrame.ApprovePayment) != 0)
        {
            // EIP8141: charge the max cost up front from the payer and consume the sender nonce.
            // Refund of unused gas is wired with full gas accounting in a later slice.
            WorldState.SubtractFromBalance(resolvedTarget, frameContext.MaxCost, spec);
            WorldState.IncrementNonce(frameContext.Sender);
            frameContext.Payer = resolvedTarget;
        }
    }
}
