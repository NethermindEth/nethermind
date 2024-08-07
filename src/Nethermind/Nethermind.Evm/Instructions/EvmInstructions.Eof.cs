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

    [SkipLocalsInit]
    public static EvmExceptionType InstructionRelativeJump(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        var codeInfo = vm.State.Env.CodeInfo;
        if (!vm.Spec.IsEofEnabled || codeInfo.Version == 0)
            return EvmExceptionType.BadInstruction;

        if (!UpdateGas(GasCostOf.RJump, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        short offset = codeInfo.CodeSection.Span.Slice(programCounter, EvmObjectFormat.TWO_BYTE_LENGTH).ReadEthInt16();
        programCounter += EvmObjectFormat.TWO_BYTE_LENGTH + offset;

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionRelativeJumpIf(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        var codeInfo = vm.State.Env.CodeInfo;
        if (!vm.Spec.IsEofEnabled || codeInfo.Version == 0)
            return EvmExceptionType.BadInstruction;

        if (!UpdateGas(GasCostOf.RJumpi, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        Span<byte> condition = stack.PopWord256();
        short offset = codeInfo.CodeSection.Span.Slice(programCounter, EvmObjectFormat.TWO_BYTE_LENGTH).ReadEthInt16();
        if (!condition.IsZero())
        {
            programCounter += offset;
        }
        programCounter += EvmObjectFormat.TWO_BYTE_LENGTH;

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionJumpTable(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        var codeInfo = vm.State.Env.CodeInfo;
        if (!vm.Spec.IsEofEnabled || codeInfo.Version == 0)
            return EvmExceptionType.BadInstruction;

        if (!UpdateGas(GasCostOf.RJumpv, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        stack.PopUInt256(out var a);
        var codeSection = codeInfo.CodeSection.Span;

        var count = codeSection[programCounter] + 1;
        var immediates = (ushort)(count * EvmObjectFormat.TWO_BYTE_LENGTH + EvmObjectFormat.ONE_BYTE_LENGTH);
        if (a < count)
        {
            int case_v = programCounter + EvmObjectFormat.ONE_BYTE_LENGTH + (int)a * EvmObjectFormat.TWO_BYTE_LENGTH;
            int offset = codeSection.Slice(case_v, EvmObjectFormat.TWO_BYTE_LENGTH).ReadEthInt16();
            programCounter += offset;
        }
        programCounter += immediates;

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionCallFunction(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        var codeInfo = vm.State.Env.CodeInfo;
        if (!vm.Spec.IsEofEnabled || codeInfo.Version == 0)
            return EvmExceptionType.BadInstruction;

        if (!UpdateGas(GasCostOf.Callf, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        var codeSection = codeInfo.CodeSection.Span;
        var index = (int)codeSection.Slice(programCounter, EvmObjectFormat.TWO_BYTE_LENGTH).ReadEthUInt16();
        (int inputCount, _, int maxStackHeight) = codeInfo.GetSectionMetadata(index);

        if (EvmObjectFormat.Eof1.MAX_STACK_HEIGHT - maxStackHeight + inputCount < stack.Head)
        {
            return EvmExceptionType.StackOverflow;
        }

        if (vm.State.ReturnStackHead == EvmObjectFormat.Eof1.RETURN_STACK_MAX_HEIGHT)
            return EvmExceptionType.InvalidSubroutineEntry;

        vm.State.ReturnStack[vm.State.ReturnStackHead++] = new EvmState.ReturnState
        {
            Index = vm.SectionIndex,
            Height = stack.Head - inputCount,
            Offset = programCounter + EvmObjectFormat.TWO_BYTE_LENGTH
        };

        vm.SectionIndex = index;
        programCounter = codeInfo.CodeSectionOffset(index).Start;

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionReturnFunction(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        var codeInfo = vm.State.Env.CodeInfo;
        if (!vm.Spec.IsEofEnabled || codeInfo.Version == 0)
            return EvmExceptionType.BadInstruction;

        if (!UpdateGas(GasCostOf.Retf, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        (_, int outputCount, _) = codeInfo.GetSectionMetadata(vm.SectionIndex);

        var stackFrame = vm.State.ReturnStack[--vm.State.ReturnStackHead];
        vm.SectionIndex = stackFrame.Index;
        programCounter = stackFrame.Offset;

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionJumpFunction(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        var codeInfo = vm.State.Env.CodeInfo;
        if (!vm.Spec.IsEofEnabled || codeInfo.Version == 0)
            return EvmExceptionType.BadInstruction;

        if (!UpdateGas(GasCostOf.Jumpf, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        var index = (int)codeInfo.CodeSection.Span.Slice(programCounter, EvmObjectFormat.TWO_BYTE_LENGTH).ReadEthUInt16();
        (int inputCount, _, int maxStackHeight) = codeInfo.GetSectionMetadata(index);

        if (EvmObjectFormat.Eof1.MAX_STACK_HEIGHT - maxStackHeight + inputCount < stack.Head)
        {
            return EvmExceptionType.StackOverflow;
        }
        vm.SectionIndex = index;
        programCounter = codeInfo.CodeSectionOffset(index).Start;

        return EvmExceptionType.None;
    }
}
