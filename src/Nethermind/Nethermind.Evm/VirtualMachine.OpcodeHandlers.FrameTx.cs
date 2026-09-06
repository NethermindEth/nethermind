// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Evm;

public unsafe partial class VirtualMachine<TGasPolicy>
{
    private readonly struct ApproveOpcode : IOpcodeBody
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionApprove<TGasPolicy>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct TxParamOpcode<TTracingInst, TEip8250, TEip8272> : IOpcodeBody
        where TTracingInst : struct, IFlag
        where TEip8250 : struct, IFlag
        where TEip8272 : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionTxParam<TGasPolicy, TTracingInst, TEip8250, TEip8272>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct FrameDataLoadOpcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionFrameDataLoad<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct FrameDataCopyOpcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionFrameDataCopy<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct FrameParamOpcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionFrameParam<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct SigParamOpcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionSigParam<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct SigDataCopyOpcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionSigDataCopy<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct RecentRootRefLoadOpcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionRecentRootRefLoad<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct TxTraceOpcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionTxTrace<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct TxDiffOpcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionTxDiff<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }

    private readonly struct EventDataCopyOpcode<TTracingInst> : IOpcodeBody where TTracingInst : struct, IFlag
    {
        public static EvmExceptionType Execute(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, ref nint programCounter) =>
            EvmInstructions.InstructionEventDataCopy<TGasPolicy, TTracingInst>(vm, ref stack, ref gas, ref programCounter);
    }
}
