// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Evm;
using Int256;

internal sealed partial class EvmInstructions
{
    [SkipLocalsInit]
    public static EvmExceptionType InstructionReturnDataSize(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        if (!vm.Spec.ReturnDataOpcodesEnabled) return EvmExceptionType.BadInstruction;

        gasAvailable -= GasCostOf.Base;

        stack.PushUInt32(vm.ReturnDataBuffer.Length);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionReturnDataCopy(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        if (!vm.Spec.ReturnDataOpcodesEnabled) return EvmExceptionType.BadInstruction;

        if (!stack.PopUInt256(out UInt256 a)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 b)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 c)) return EvmExceptionType.StackUnderflow;
        gasAvailable -= GasCostOf.VeryLow + GasCostOf.Memory * EvmPooledMemory.Div32Ceiling(in c);

        ReadOnlyMemory<byte> returnDataBuffer = vm.ReturnDataBuffer;
        if (vm.State.Env.CodeInfo.Version == 0 && (UInt256.AddOverflow(c, b, out UInt256 result) || result > returnDataBuffer.Length))
        {
            return EvmExceptionType.AccessViolation;
        }

        if (!c.IsZero)
        {
            if (!UpdateMemoryCost(vm.State, ref gasAvailable, in a, c)) return EvmExceptionType.OutOfGas;
            ZeroPaddedSpan slice = returnDataBuffer.Span.SliceWithZeroPadding(b, (int)c);
            vm.State.Memory.Save(in a, in slice);
            if (vm.TxTracer.IsTracingInstructions)
            {
                vm.TxTracer.ReportMemoryChange((long)a, in slice);
            }
        }

        return EvmExceptionType.None;
    }
}
