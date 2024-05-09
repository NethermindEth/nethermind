// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Evm.Tracing;
using static Nethermind.Evm.VirtualMachine;

namespace Nethermind.Evm;
using Int256;

using Nethermind.Core.Specs;
using Nethermind.Core;

internal sealed partial class EvmInstructions
{
    public interface IOpCodeCopy
    {
        abstract static ReadOnlySpan<byte> GetCode(EvmState vmState);
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionCodeCopy<TOpCodeCopy>(EvmState vmState, ref EvmStack stack, ref long gasAvailable)
        where TOpCodeCopy : struct, IOpCodeCopy
    {
        if (!stack.PopUInt256(out UInt256 a)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 b)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 result)) return EvmExceptionType.StackUnderflow;
        gasAvailable -= GasCostOf.VeryLow + GasCostOf.Memory * EvmPooledMemory.Div32Ceiling(in result);

        if (!result.IsZero)
        {
            if (!UpdateMemoryCost(vmState, ref gasAvailable, in a, result)) return EvmExceptionType.OutOfGas;
            ZeroPaddedSpan slice = TOpCodeCopy.GetCode(vmState).SliceWithZeroPadding(in b, (int)result);
            vmState.Memory.Save(in a, in slice);
            if (vmState.TxTracer.IsTracingInstructions)
            {
                vmState.TxTracer.ReportMemoryChange((long)a, in slice);
            }
        }

        return EvmExceptionType.None;
    }

    public struct OpCallDataCopy : IOpCodeCopy
    {
        public static ReadOnlySpan<byte> GetCode(EvmState vmState)
            => vmState.Env.InputData.Span;
    }

    public struct OpCallCopy : IOpCodeCopy
    {
        public static ReadOnlySpan<byte> GetCode(EvmState vmState)
            => vmState.Env.CodeInfo.MachineCode.Span;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionExtCodeCopy(EvmState vmState, ref EvmStack stack, ref long gasAvailable)
    {
        IReleaseSpec spec = vmState.Spec;
        Address address = stack.PopAddress();
        if (address is null) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 a)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 b)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 result)) return EvmExceptionType.StackUnderflow;

        gasAvailable -= spec.GetExtCodeCost() + GasCostOf.Memory * EvmPooledMemory.Div32Ceiling(in result);

        if (!ChargeAccountAccessGas(ref gasAvailable, vmState, address, spec)) return EvmExceptionType.OutOfGas;

        if (!result.IsZero)
        {
            if (!UpdateMemoryCost(vmState, ref gasAvailable, in a, result)) return EvmExceptionType.OutOfGas;

            ReadOnlySpan<byte> externalCode = VirtualMachine.GetCachedCodeInfo(vmState.WorldState, address, spec).MachineCode.Span;
            ZeroPaddedSpan slice = externalCode.SliceWithZeroPadding(in b, (int)result);
            vmState.Memory.Save(in a, in slice);
            if (vmState.TxTracer.IsTracingInstructions)
            {
                vmState.TxTracer.ReportMemoryChange((long)a, in slice);
            }
        }

        return EvmExceptionType.None;
    }
}
