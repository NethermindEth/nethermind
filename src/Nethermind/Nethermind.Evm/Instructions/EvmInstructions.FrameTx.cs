// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Evm.GasPolicy;
using Nethermind.Int256;

namespace Nethermind.Evm;

/// <summary>
/// EIP-8141 frame transaction introspection and approval opcodes. All six are registered only when
/// <c>IsEip8141Enabled</c>; each exceptional-halts when executed outside a frame transaction (i.e.
/// when the transaction-scoped <see cref="FrameTxContext"/> is absent).
/// https://eips.ethereum.org/EIPS/eip-8141
/// </summary>
public static unsafe partial class EvmInstructions
{
    /// <summary>APPROVE (0xaa): terminate the frame successfully and record the approval scope for the outer loop.</summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionApprove<TGasPolicy>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        FrameTxContext? ctx = vm.TxExecutionContext.FrameTxContext;
        if (ctx is null) return EvmExceptionType.BadInstruction;

        // Spec stack order (top to bottom): offset, length, scope.
        if (!stack.PopUInt256(out UInt256 offset, out UInt256 length, out UInt256 scope))
            return EvmExceptionType.StackUnderflow;

        TxFrame frame = ctx.CurrentFrame;
        Address resolvedTarget = ctx.ResolvedTarget(ctx.CurrentFrameIndex);

        // Only the resolved target (or a DELEGATECALL from it, which preserves ADDRESS) may approve.
        if (!vm.VmState.Env.ExecutingAccount.Equals(resolvedTarget))
            return EvmExceptionType.Revert;

        byte scopeByte = (byte)scope.u0;
        byte allowed = frame.AllowedApproveScope;
        // scope != 0 and every requested bit permitted by the frame flags.
        if (scope > TxFrame.ApproveScopeMask || scopeByte == 0 || (scopeByte & ~allowed) != 0)
            return EvmExceptionType.Revert;

        bool approvesExecution = (scopeByte & TxFrame.ApproveExecution) != 0;
        bool approvesPayment = (scopeByte & TxFrame.ApprovePayment) != 0;

        if (approvesExecution)
        {
            // Re-approval and non-sender targets revert the frame.
            if (ctx.SenderApproved || resolvedTarget != ctx.Sender) return EvmExceptionType.Revert;
        }

        if (approvesPayment)
        {
            // A second payer, payment before execution approval (unless this APPROVE grants both),
            // and an underfunded payer all revert the frame.
            if (ctx.Payer is not null) return EvmExceptionType.Revert;
            if (!approvesExecution && !ctx.SenderApproved) return EvmExceptionType.Revert;
            if (vm.WorldState.GetBalance(resolvedTarget) < ctx.MaxCost) return EvmExceptionType.Revert;
        }

        // Load the return data region (RETURN semantics). The outer loop applies the approval effects.
        // EIP8141-ISSUE: the spec does not define what happens to APPROVE's return data; loaded like
        // RETURN and left to the outer loop. Propose the spec state its disposition explicitly.
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
    [SkipLocalsInit]
    public static EvmExceptionType InstructionTxParam<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        FrameTxContext? ctx = vm.TxExecutionContext.FrameTxContext;
        if (ctx is null) return EvmExceptionType.BadInstruction;

        TGasPolicy.Consume<BaseGasCost>(ref gas);
        if (!stack.PopUInt256(out UInt256 param)) return EvmExceptionType.StackUnderflow;
        if (param > 0x0B) return EvmExceptionType.BadInstruction;

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
            _ => EvmExceptionType.BadInstruction,
        };
    }

    /// <summary>FRAMEDATALOAD (0xb1): load a 32-byte word from another frame's data.</summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionFrameDataLoad<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        FrameTxContext? ctx = vm.TxExecutionContext.FrameTxContext;
        if (ctx is null) return EvmExceptionType.BadInstruction;

        TGasPolicy.Consume<VeryLowGasCost>(ref gas);
        // EIP8141-ISSUE: no explicit stack table (audit L-8); the prose lists offset, frameIndex —
        // read top-to-bottom, so offset is on top (matching CALLDATALOAD).
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
    public static EvmExceptionType InstructionFrameDataCopy<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        FrameTxContext? ctx = vm.TxExecutionContext.FrameTxContext;
        if (ctx is null) return EvmExceptionType.BadInstruction;

        // EIP8141-ISSUE: no explicit stack table (audit L-8); the prose lists memOffset, dataOffset,
        // length, frameIndex — read top-to-bottom like the SIGPARAM copy list, so frameIndex is deepest.
        if (!stack.PopUInt256(out UInt256 memOffset, out UInt256 dataOffset, out UInt256 length, out UInt256 frameIndex))
            return EvmExceptionType.StackUnderflow;
        if (frameIndex >= (UInt256)ctx.Frames.Length) return EvmExceptionType.BadInstruction;

        return DataCopyCore<TGasPolicy, TTracingInst>(vm, ref gas, in memOffset, in dataOffset, in length, ctx.Frames[(int)frameIndex.u0].Data.Span);
    }

    /// <summary>FRAMEPARAM (0xb3): read a frame-scoped field.</summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionFrameParam<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        FrameTxContext? ctx = vm.TxExecutionContext.FrameTxContext;
        if (ctx is null) return EvmExceptionType.BadInstruction;

        TGasPolicy.Consume<BaseGasCost>(ref gas);
        // Spec stack order: frameIndex on top, param second.
        if (!stack.PopUInt256(out UInt256 frameIndex, out UInt256 param)) return EvmExceptionType.StackUnderflow;
        if (frameIndex >= (UInt256)ctx.Frames.Length) return EvmExceptionType.BadInstruction;
        if (param > 0x08) return EvmExceptionType.BadInstruction;

        int index = (int)frameIndex.u0;
        TxFrame frame = ctx.Frames[index];
        return param.u0 switch
        {
            0x00 => stack.PushAddress<TTracingInst>(ctx.ResolvedTarget(index)),
            0x01 => stack.PushUInt256<TTracingInst>((UInt256)frame.GasLimit),
            0x02 => stack.PushUInt32<TTracingInst>(frame.Mode),
            0x03 => stack.PushUInt32<TTracingInst>(frame.Flags),
            0x04 => stack.PushUInt256<TTracingInst>((UInt256)frame.Data.Length),
            0x05 => FrameStatus<TTracingInst>(ctx, index, ref stack),
            0x06 => stack.PushUInt32<TTracingInst>(frame.AllowedApproveScope),
            0x07 => stack.PushUInt32<TTracingInst>((uint)(frame.IsAtomicBatch ? 1 : 0)),
            0x08 => stack.PushUInt256<TTracingInst>(frame.Value),
            _ => EvmExceptionType.BadInstruction,
        };
    }

    private static EvmExceptionType FrameStatus<TTracingInst>(FrameTxContext ctx, int index, ref EvmStack stack)
        where TTracingInst : struct, IFlag
    {
        // Reading the status of the current or a future frame is an exceptional halt.
        if (!ctx.IsFrameCompleted(index)) return EvmExceptionType.BadInstruction;
        // 0 failure, 1 success, 2 skipped by a failed atomic batch (ethereum/EIPs#11953).
        uint status = ctx.WasFrameSkipped(index) ? 2u : ctx.HasFrameSucceeded(index) ? 1u : 0u;
        return stack.PushUInt32<TTracingInst>(status);
    }

    /// <summary>SIGPARAM (0xb4): read a signature-scoped field, or copy ARBITRARY signature bytes.</summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionSigParam<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        FrameTxContext? ctx = vm.TxExecutionContext.FrameTxContext;
        if (ctx is null) return EvmExceptionType.BadInstruction;

        // Spec stack order: signatureIndex on top, param second.
        if (!stack.PopUInt256(out UInt256 signatureIndex, out UInt256 param)) return EvmExceptionType.StackUnderflow;
        if (signatureIndex >= (UInt256)ctx.Signatures.Length) return EvmExceptionType.BadInstruction;
        if (param > 0x04) return EvmExceptionType.BadInstruction;

        int index = (int)signatureIndex.u0;
        TxFrameSignature signature = ctx.Signatures[index];

        if (param.u0 == 0x04)
        {
            if (signature.Scheme != TxFrameSignature.SchemeArbitrary) return EvmExceptionType.BadInstruction;
            // Spec stack order after signatureIndex/param: length, dataOffset, memOffset.
            // EIP8141-ISSUE: this is the reverse of the CALLDATACOPY operand order — likely a spec
            // oversight worth pinning with an explicit stack table; implemented as written.
            if (!stack.PopUInt256(out UInt256 length, out UInt256 dataOffset, out UInt256 memOffset))
                return EvmExceptionType.StackUnderflow;
            return DataCopyCore<TGasPolicy, TTracingInst>(vm, ref gas, in memOffset, in dataOffset, in length, signature.Signature.Span);
        }

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
            0x03 => stack.PushUInt256<TTracingInst>((UInt256)signature.Signature.Length),
            _ => EvmExceptionType.BadInstruction,
        };
    }
}
