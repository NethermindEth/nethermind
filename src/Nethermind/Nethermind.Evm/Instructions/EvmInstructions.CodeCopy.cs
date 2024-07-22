// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core.Specs;
using Nethermind.Core;
using Nethermind.Evm.EOF;

namespace Nethermind.Evm;
using Int256;

internal sealed partial class EvmInstructions
{
    public interface IOpCodeCopy
    {
        abstract static ReadOnlySpan<byte> GetCode(IEvm vm);
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionCodeCopy<TOpCodeCopy>(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpCodeCopy : struct, IOpCodeCopy
    {
        if (!stack.PopUInt256(out UInt256 a)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 b)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 result)) return EvmExceptionType.StackUnderflow;
        gasAvailable -= GasCostOf.VeryLow + GasCostOf.Memory * EvmPooledMemory.Div32Ceiling(in result);

        if (!result.IsZero)
        {
            if (!UpdateMemoryCost(vm.State, ref gasAvailable, in a, result)) return EvmExceptionType.OutOfGas;
            ZeroPaddedSpan slice = TOpCodeCopy.GetCode(vm).SliceWithZeroPadding(in b, (int)result);
            vm.State.Memory.Save(in a, in slice);
            if (vm.TxTracer.IsTracingInstructions)
            {
              vm.TxTracer.ReportMemoryChange((long)a, in slice);
            }
        }

        return EvmExceptionType.None;
    }

    public struct OpCallDataCopy : IOpCodeCopy
    {
        public static ReadOnlySpan<byte> GetCode(IEvm vm)
            => vm.State.Env.InputData.Span;
    }

    public struct OpCodeCopy : IOpCodeCopy
    {
        public static ReadOnlySpan<byte> GetCode(IEvm vm)
            => vm.State.Env.CodeInfo.MachineCode.Span;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionExtCodeCopy(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        IReleaseSpec spec = vm.Spec;
        Address address = stack.PopAddress();
        if (address is null) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 a)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 b)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 result)) return EvmExceptionType.StackUnderflow;

        gasAvailable -= spec.GetExtCodeCost() + GasCostOf.Memory * EvmPooledMemory.Div32Ceiling(in result);

        if (!ChargeAccountAccessGas(ref gasAvailable, vm, address)) return EvmExceptionType.OutOfGas;

        if (!result.IsZero)
        {
            if (!UpdateMemoryCost(vm.State, ref gasAvailable, in a, result)) return EvmExceptionType.OutOfGas;

            ReadOnlySpan<byte> externalCode = vm.CodeInfoRepository.GetCachedCodeInfo(vm.WorldState, address, spec).MachineCode.Span;
            if (spec.IsEofEnabled && EvmObjectFormat.IsEof(externalCode, out _))
            {
                externalCode = EvmObjectFormat.MAGIC;
            }
            ZeroPaddedSpan slice = externalCode.SliceWithZeroPadding(in b, (int)result);
            vm.State.Memory.Save(in a, in slice);
            if (vm.TxTracer.IsTracingInstructions)
            {
                vm.TxTracer.ReportMemoryChange((long)a, in slice);
            }
        }

        return EvmExceptionType.None;
    }
}
