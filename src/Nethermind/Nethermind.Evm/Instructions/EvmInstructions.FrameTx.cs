// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Evm;

/// <summary>EIP-8141 frame introspection and approval opcodes, plus the EIP-8272 reference reader.
/// Each exceptional-halts outside a frame transaction, where <see cref="FrameTxContext"/> is absent.</summary>
public static unsafe partial class EvmInstructions
{
    /// <summary>APPROVE (0xaa): terminate the frame successfully and record the approval scope for the outer loop.</summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionApprove<TGasPolicy>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        FrameTxContext? ctx = vm.TxExecutionContext.FrameTxContext;
        if (ctx is null) return EvmExceptionType.BadInstruction;

        // Spec stack order (top to bottom): offset, length, scope.
        if (!stack.PopUInt256(out UInt256 offset, out UInt256 length, out UInt256 scope))
            return EvmExceptionType.StackUnderflow;

        TxFrame frame = ctx.CurrentFrame;

        // EIP-7906 forbids the call, not the permission bits: a POST_TX frame may carry an approval
        // scope it never exercises, so the ban belongs here rather than in envelope validation.
        if (frame.Mode == TxFrame.ModePostTx) return EvmExceptionType.BadInstruction;

        Address resolvedTarget = ctx.ResolvedTarget(ctx.CurrentFrameIndex);

        // Only the resolved target (or a DELEGATECALL from it, which preserves ADDRESS) may approve.
        if (!vm.VmState.Env.ExecutingAccount.Equals(resolvedTarget))
            return EvmExceptionType.Revert;

        byte scopeByte = (byte)scope.u0;
        byte allowed = frame.AllowedApproveScope;
        if (scope > TxFrame.ApproveScopeMask || scopeByte == 0 || (scopeByte & ~allowed) != 0)
            return EvmExceptionType.Revert;

        bool approvesExecution = (scopeByte & TxFrame.ApproveExecution) != 0;
        bool approvesPayment = (scopeByte & TxFrame.ApprovePayment) != 0;

        if (approvesExecution)
        {
            if (ctx.SenderApproved || resolvedTarget != ctx.Sender) return EvmExceptionType.Revert;
        }

        if (approvesPayment)
        {
            if (ctx.Payer is not null) return EvmExceptionType.Revert;
            // EIP-8141 ordering: payment may not be approved before execution, unless this same APPROVE grants both.
            if (!approvesExecution && !ctx.SenderApproved) return EvmExceptionType.Revert;
            if (vm.WorldState.GetBalance(resolvedTarget) < ctx.MaxCost) return EvmExceptionType.Revert;

            // Consumption happens at payment approval, so first use is charged against this frame's gas.
            if (ctx.NonceKeys is { } nonceKeys
                && !TGasPolicy.TryConsume(ref gas, KeyedNonceManager.FirstUseSurcharge(vm.WorldState, ctx.Sender, nonceKeys)))
            {
                return EvmExceptionType.OutOfGas;
            }
        }

        // EIP-8141 APPROVE: the memory region becomes the frame's return data, following RETURN semantics.
        if (!TGasPolicy.UpdateMemoryCost(ref gas, in offset, in length, ref vm.VmState.Memory) ||
            !vm.VmState.Memory.TryLoad(in offset, in length, out ReadOnlyMemory<byte> returnData))
        {
            return EvmExceptionType.OutOfGas;
        }

        vm.ReturnData = returnData.ToArray();
        ctx.ApprovalScopeSignal = scopeByte;
        // Stop (not None): APPROVE exits the current call frame successfully, and the dispatch
        // loop only polls ReturnData for opcodes at CREATE and above.
        return EvmExceptionType.Stop;
    }

    /// <summary>TXPARAM (0xb0): read a transaction-scoped field.</summary>
    /// <typeparam name="TEip8250">Whether the fork defines the keyed-nonce indices 0x0D, 0x0E, 0x10 and 0x11.</typeparam>
    /// <typeparam name="TEip8272">Whether the fork defines the recent-root reference count at index 0x0F.</typeparam>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionTxParam<TGasPolicy, TTracingInst, TEip8250, TEip8272>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
        where TEip8250 : struct, IFlag
        where TEip8272 : struct, IFlag
    {
        FrameTxContext? ctx = vm.TxExecutionContext.FrameTxContext;
        if (ctx is null) return EvmExceptionType.BadInstruction;

        TGasPolicy.Consume<BaseGasCost>(ref gas);
        if (!stack.PopUInt256(out UInt256 param)) return EvmExceptionType.StackUnderflow;
        if (param > 0x11U) return EvmExceptionType.BadInstruction;

        byte[][]? blobHashes = vm.TxExecutionContext.BlobVersionedHashes;
        return param.u0 switch
        {
            0x00 => stack.PushUInt32<TTracingInst>((uint)TxType.FrameTx),
            0x01 => stack.PushUInt256<TTracingInst>(ctx.Nonce),
            0x02 => stack.PushAddress<TTracingInst>(ctx.Sender),
            0x03 => stack.PushUInt256<TTracingInst>(ctx.MaxPriorityFeePerGas),
            0x04 => stack.PushUInt256<TTracingInst>(ctx.MaxFeePerGas),
            0x05 => stack.PushUInt256<TTracingInst>(ctx.MaxFeePerBlobGas),
            0x06 => stack.PushUInt256<TTracingInst>(ctx.MaxCost),
            0x07 => stack.PushUInt256<TTracingInst>((UInt256)(blobHashes?.Length ?? 0)),
            0x08 => stack.PushBytes<TTracingInst>(ctx.SigHash.BytesAsSpan),
            0x09 => stack.PushUInt256<TTracingInst>((UInt256)ctx.Frames.Length),
            0x0A => stack.PushUInt256<TTracingInst>((UInt256)ctx.CurrentFrameIndex),
            0x0B => stack.PushUInt256<TTracingInst>((UInt256)ctx.Signatures.Length),
            // The two extensions claim disjoint indices, so each is gated on its own fork rather than
            // on one shared ceiling: 0x0F must stay undefined on a chain with EIP-8250 but not EIP-8272.
            0x0C => stack.PushUInt256<TTracingInst>((UInt256)(ulong)Math.Max(0, TGasPolicy.GetStateReservoir(in gas))),
            0x0D when TEip8250.IsActive => stack.PushUInt256<TTracingInst>((UInt256)(ctx.NonceKeys?.Length ?? 1)),
            0x0E when TEip8250.IsActive => stack.PushBytes<TTracingInst>(ctx.NonceKeysHash.BytesAsSpan),
            0x10 when TEip8250.IsActive => stack.PushUInt256<TTracingInst>(ctx.NonceKeys is { } keys ? keys[0] : UInt256.Zero),
            0x11 when TEip8250.IsActive => stack.PushUInt256<TTracingInst>(ctx.LegacyNonce),
            0x0F when TEip8272.IsActive => stack.PushUInt256<TTracingInst>((UInt256)ctx.RecentRootReferences.Length),
            _ => EvmExceptionType.BadInstruction,
        };
    }

    /// <summary>RECENTROOTREFLOAD (0xb6): read one field of a declared recent-root reference.</summary>
    /// <remarks>Reads the signed envelope, not the predeploy's storage, and it was checked against the
    /// pre-state before any frame ran, so the opcode is legal in every frame mode including <c>VERIFY</c>.</remarks>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionRecentRootRefLoad<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        FrameTxContext? ctx = vm.TxExecutionContext.FrameTxContext;
        if (ctx is null) return EvmExceptionType.BadInstruction;

        TGasPolicy.Consume<VeryLowGasCost>(ref gas);
        // Spec stack order: field on top, index second — the reverse of FRAMEPARAM and SIGPARAM.
        if (!stack.PopUInt256(out UInt256 field, out UInt256 index)) return EvmExceptionType.StackUnderflow;
        if (index >= (UInt256)ctx.RecentRootReferences.Length || field > 2) return EvmExceptionType.BadInstruction;

        RecentRootReference reference = ctx.RecentRootReferences[(int)index.u0];
        return field.u0 switch
        {
            0 => stack.PushBytes<TTracingInst>(reference.SourceId.BytesAsSpan),
            1 => stack.PushUInt256<TTracingInst>((UInt256)reference.Slot),
            _ => stack.PushBytes<TTracingInst>(reference.Root.BytesAsSpan),
        };
    }

    /// <summary>FRAMEDATALOAD (0xb1): load a 32-byte word from another frame's data.</summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionFrameDataLoad<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        FrameTxContext? ctx = vm.TxExecutionContext.FrameTxContext;
        if (ctx is null) return EvmExceptionType.BadInstruction;

        TGasPolicy.Consume<VeryLowGasCost>(ref gas);
        // Spec stack order: offset on top, frameIndex second (matching CALLDATALOAD).
        if (!stack.PopUInt256(out UInt256 offset, out UInt256 frameIndex)) return EvmExceptionType.StackUnderflow;
        if (frameIndex >= (UInt256)ctx.Frames.Length) return EvmExceptionType.BadInstruction;

        ReadOnlySpan<byte> data = ctx.Frames[(int)frameIndex.u0].Data.Span;
        if (!offset.IsUint64 || offset.u0 >= (uint)data.Length)
        {
            return stack.PushZero<TTracingInst>();
        }

        uint available = (uint)data.Length - (uint)offset.u0;
        uint copiedLength = available >= 32 ? 32u : available;
        return stack.PushRightPaddedBytes<TTracingInst>(
            ref Unsafe.Add(ref MemoryMarshal.GetReference(data), (nint)offset.u0),
            copiedLength);
    }

    /// <summary>FRAMEDATACOPY (0xb2): copy another frame's data into memory (CALLDATACOPY semantics).</summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionFrameDataCopy<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        FrameTxContext? ctx = vm.TxExecutionContext.FrameTxContext;
        if (ctx is null) return EvmExceptionType.BadInstruction;

        // Spec stack order: memOffset, dataOffset, length, frameIndex (top to bottom, matching CALLDATACOPY).
        if (!stack.PopUInt256(out UInt256 memOffset, out UInt256 dataOffset, out UInt256 length, out UInt256 frameIndex))
            return EvmExceptionType.StackUnderflow;
        if (frameIndex >= (UInt256)ctx.Frames.Length) return EvmExceptionType.BadInstruction;

        return DataCopyCore<TGasPolicy, TTracingInst>(vm, ref gas, in memOffset, in dataOffset, in length, ctx.Frames[(int)frameIndex.u0].Data.Span);
    }

    /// <summary>FRAMEPARAM (0xb3): read a frame-scoped field.</summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionFrameParam<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        FrameTxContext? ctx = vm.TxExecutionContext.FrameTxContext;
        if (ctx is null) return EvmExceptionType.BadInstruction;

        TGasPolicy.Consume<BaseGasCost>(ref gas);
        // Spec stack order: frameIndex on top, param second.
        if (!stack.PopUInt256(out UInt256 frameIndex, out UInt256 param)) return EvmExceptionType.StackUnderflow;
        if (frameIndex >= (UInt256)ctx.Frames.Length) return EvmExceptionType.BadInstruction;
        if (param > 0x0B) return EvmExceptionType.BadInstruction;

        int index = (int)frameIndex.u0;
        TxFrame frame = ctx.Frames[index];
        return param.u0 switch
        {
            0x00 => stack.PushAddress<TTracingInst>(ctx.ResolvedTarget(index)),
            0x01 => stack.PushUInt256<TTracingInst>((UInt256)frame.ExecutionGasLimit),
            0x02 => stack.PushUInt32<TTracingInst>(frame.Mode),
            0x03 => stack.PushUInt32<TTracingInst>(frame.Flags),
            0x04 => stack.PushUInt256<TTracingInst>((UInt256)frame.Data.Length),
            0x05 => FrameStatus<TTracingInst>(ctx, index, ref stack),
            0x06 => stack.PushUInt32<TTracingInst>(frame.AllowedApproveScope),
            0x07 => stack.PushUInt32<TTracingInst>((uint)(frame.IsAtomicBatch ? 1 : 0)),
            0x08 => stack.PushUInt256<TTracingInst>(frame.Value),
            0x09 => stack.PushUInt256<TTracingInst>((UInt256)frame.StateGasLimit),
            0x0A => FrameExecutionGasUsed<TTracingInst>(ctx, index, ref stack),
            0x0B => FrameStateGasUsed<TTracingInst>(ctx, index, ref stack),
            _ => EvmExceptionType.BadInstruction,
        };
    }

    private static EvmExceptionType FrameExecutionGasUsed<TTracingInst>(FrameTxContext ctx, int index, ref EvmStack stack)
        where TTracingInst : struct, IFlag
    {
        if (!ctx.IsFrameCompleted(index)) return EvmExceptionType.BadInstruction;
        return stack.PushUInt256<TTracingInst>((UInt256)ctx.ExecutionGasUsedFor(index));
    }

    private static EvmExceptionType FrameStateGasUsed<TTracingInst>(FrameTxContext ctx, int index, ref EvmStack stack)
        where TTracingInst : struct, IFlag
    {
        if (!ctx.IsFrameCompleted(index)) return EvmExceptionType.BadInstruction;
        return stack.PushUInt256<TTracingInst>((UInt256)ctx.StateGasUsedFor(index));
    }

    private static EvmExceptionType FrameStatus<TTracingInst>(FrameTxContext ctx, int index, ref EvmStack stack)
        where TTracingInst : struct, IFlag
    {
        if (!ctx.IsFrameCompleted(index)) return EvmExceptionType.BadInstruction;
        // 0 failure, 1 success, 2 skipped by a failed atomic batch.
        uint status = ctx.WasFrameSkipped(index) ? 2u : ctx.HasFrameSucceeded(index) ? 1u : 0u;
        return stack.PushUInt32<TTracingInst>(status);
    }

    /// <summary>SIGPARAM (0xb4): read a signature-scoped field.</summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionSigParam<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        FrameTxContext? ctx = vm.TxExecutionContext.FrameTxContext;
        if (ctx is null) return EvmExceptionType.BadInstruction;

        // Spec stack order: signatureIndex on top, param second.
        if (!stack.PopUInt256(out UInt256 signatureIndex, out UInt256 param)) return EvmExceptionType.StackUnderflow;
        if (signatureIndex >= (UInt256)ctx.Signatures.Length) return EvmExceptionType.BadInstruction;
        if (param > 0x03) return EvmExceptionType.BadInstruction;

        int index = (int)signatureIndex.u0;
        TxFrameSignature signature = ctx.Signatures[index];

        TGasPolicy.Consume<BaseGasCost>(ref gas);
        return param.u0 switch
        {
            0x00 => signature.Scheme == TxFrameSignature.SchemeArbitrary
                ? EvmExceptionType.BadInstruction
                : stack.PushAddress<TTracingInst>(ctx.ResolvedSigner(index)),
            0x01 => stack.PushUInt32<TTracingInst>(signature.Scheme),
            0x02 => signature.Msg.IsEmpty
                ? stack.PushZero<TTracingInst>()
                : stack.PushBytes<TTracingInst>(signature.Msg.Span),
            0x03 => signature.Scheme == TxFrameSignature.SchemeArbitrary
                ? stack.PushUInt256<TTracingInst>((UInt256)signature.Signature.Length)
                : EvmExceptionType.BadInstruction,
            _ => EvmExceptionType.BadInstruction,
        };
    }

    /// <summary>SIGDATACOPY (0xb5): copy an ARBITRARY signature's raw bytes into memory (CALLDATACOPY semantics).</summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionSigDataCopy<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        FrameTxContext? ctx = vm.TxExecutionContext.FrameTxContext;
        if (ctx is null) return EvmExceptionType.BadInstruction;

        // Spec stack order: memOffset, dataOffset, length, signatureIndex (top to bottom, matching CALLDATACOPY).
        if (!stack.PopUInt256(out UInt256 memOffset, out UInt256 dataOffset, out UInt256 length, out UInt256 signatureIndex))
            return EvmExceptionType.StackUnderflow;
        if (signatureIndex >= (UInt256)ctx.Signatures.Length) return EvmExceptionType.BadInstruction;

        TxFrameSignature signature = ctx.Signatures[(int)signatureIndex.u0];
        if (signature.Scheme != TxFrameSignature.SchemeArbitrary) return EvmExceptionType.BadInstruction;

        return DataCopyCore<TGasPolicy, TTracingInst>(vm, ref gas, in memOffset, in dataOffset, in length, signature.Signature.Span);
    }
}
