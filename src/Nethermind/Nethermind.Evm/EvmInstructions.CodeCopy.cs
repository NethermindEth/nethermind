// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Evm.Tracing;
using static Nethermind.Evm.VirtualMachine;


namespace Nethermind.Evm;
using Int256;

internal sealed partial class EvmInstructions
{
    public interface IOpCodeCopy
    {
        abstract static ReadOnlySpan<byte> GetCode(EvmState vmState);
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static EvmExceptionType InstructionCodeCopy<TOpCodeCopy, TTracingInstructions>(EvmState vmState, ref EvmStack<TTracingInstructions> stack, ref long gasAvailable, ITxTracer tracer)
        where TOpCodeCopy : struct, IOpCodeCopy
        where TTracingInstructions : struct, IIsTracing
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
            if (typeof(TTracingInstructions) == typeof(IsTracing))
            {
                tracer.ReportMemoryChange((long)a, in slice);
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
}
