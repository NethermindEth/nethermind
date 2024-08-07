// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core.Extensions;
using Nethermind.Evm.EOF;

namespace Nethermind.Evm;
using Int256;

using Nethermind.Core;

internal sealed partial class EvmInstructions
{
    [SkipLocalsInit]
    public static EvmExceptionType InstructionReturnDataSize(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        if (!vm.Spec.ReturnDataOpcodesEnabled) return EvmExceptionType.BadInstruction;

        gasAvailable -= GasCostOf.Base;

        UInt256 result = (UInt256)vm.ReturnDataBuffer.Length;
        stack.PushUInt256(in result);

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

    [SkipLocalsInit]
    public static EvmExceptionType InstructionDataLoad(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        var codeInfo = vm.State.Env.CodeInfo;
        if (!vm.Spec.IsEofEnabled || codeInfo.Version == 0)
            return EvmExceptionType.BadInstruction;

        if (!UpdateGas(GasCostOf.DataLoad, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        stack.PopUInt256(out var a);
        ZeroPaddedSpan zpbytes = codeInfo.DataSection.SliceWithZeroPadding(a, 32);
        stack.PushBytes(zpbytes);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionDataLoadN(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        var codeInfo = vm.State.Env.CodeInfo;
        if (!vm.Spec.IsEofEnabled || codeInfo.Version == 0)
            return EvmExceptionType.BadInstruction;

        if (!UpdateGas(GasCostOf.DataLoadN, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        var offset = codeInfo.CodeSection.Span.Slice(programCounter, EvmObjectFormat.TWO_BYTE_LENGTH).ReadEthUInt16();
        ZeroPaddedSpan zpbytes = codeInfo.DataSection.SliceWithZeroPadding(offset, 32);
        stack.PushBytes(zpbytes);

        programCounter += EvmObjectFormat.TWO_BYTE_LENGTH;

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionDataSize(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        var codeInfo = vm.State.Env.CodeInfo;
        if (!vm.Spec.IsEofEnabled || codeInfo.Version == 0)
            return EvmExceptionType.BadInstruction;

        if (!UpdateGas(GasCostOf.DataSize, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        stack.PushUInt32(codeInfo.DataSection.Length);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionDataCopy(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        var codeInfo = vm.State.Env.CodeInfo;
        if (!vm.Spec.IsEofEnabled || codeInfo.Version == 0)
            return EvmExceptionType.BadInstruction;

        stack.PopUInt256(out UInt256 memOffset);
        stack.PopUInt256(out UInt256 offset);
        stack.PopUInt256(out UInt256 size);

        if (!UpdateGas(GasCostOf.DataCopy + GasCostOf.Memory * EvmPooledMemory.Div32Ceiling(in size), ref gasAvailable))
            return EvmExceptionType.OutOfGas;

        if (!size.IsZero)
        {
            if (!UpdateMemoryCost(vm.State, ref gasAvailable, in memOffset, size))
                return EvmExceptionType.OutOfGas;
            ZeroPaddedSpan dataSectionSlice = codeInfo.DataSection.SliceWithZeroPadding(offset, (int)size);
            vm.State.Memory.Save(in memOffset, dataSectionSlice);
            if (vm.TxTracer.IsTracingInstructions)
            {
                vm.TxTracer.ReportMemoryChange((long)memOffset, dataSectionSlice);
            }
        }

        stack.PushUInt32(codeInfo.DataSection.Length);

        return EvmExceptionType.None;
    }
}
