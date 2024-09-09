// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core.Specs;
using Nethermind.Core;
using Nethermind.Evm.EvmObjectFormat;

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
            if (spec.IsEofEnabled && EofValidator.IsEof(externalCode, out _))
            {
                externalCode = EofValidator.MAGIC;
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

    [SkipLocalsInit]
    public static EvmExceptionType InstructionExtCodeSize(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        IReleaseSpec spec = vm.Spec;
        gasAvailable -= spec.GetExtCodeCost();

        Address address = stack.PopAddress();
        if (address is null) return EvmExceptionType.StackUnderflow;

        if (!ChargeAccountAccessGas(ref gasAvailable, vm, address)) return EvmExceptionType.OutOfGas;

        var codeSection = vm.State.Env.CodeInfo.MachineCode.Span;
        if (!vm.TxTracer.IsTracingInstructions && programCounter < codeSection.Length)
        {
            bool optimizeAccess = false;
            Instruction nextInstruction = (Instruction)codeSection[programCounter];
            // code.length is zero
            if (nextInstruction == Instruction.ISZERO)
            {
                optimizeAccess = true;
            }
            // code.length > 0 || code.length == 0
            else if ((nextInstruction == Instruction.GT || nextInstruction == Instruction.EQ) &&
                    stack.PeekUInt256IsZero())
            {
                optimizeAccess = true;
                if (!stack.PopLimbo()) return EvmExceptionType.StackUnderflow;
            }

            if (optimizeAccess)
            {
                // EXTCODESIZE ISZERO/GT/EQ peephole optimization.
                // In solidity 0.8.1+: `return account.code.length > 0;`
                // is is a common pattern to check if address is a contract
                // however we can just check the address's loaded CodeHash
                // to reduce storage access from trying to load the code

                programCounter++;
                // Add gas cost for ISZERO, GT, or EQ
                gasAvailable -= GasCostOf.VeryLow;

                // IsContract
                bool isCodeLengthNotZero = vm.WorldState.IsContract(address);
                if (nextInstruction == Instruction.GT)
                {
                    // Invert, to IsNotContract
                    isCodeLengthNotZero = !isCodeLengthNotZero;
                }

                if (!isCodeLengthNotZero)
                {
                    stack.PushOne();
                }
                else
                {
                    stack.PushZero();
                }
                return EvmExceptionType.None;
            }
        }

        ReadOnlySpan<byte> accountCode = vm.CodeInfoRepository.GetCachedCodeInfo(vm.WorldState, address, spec).MachineCode.Span;
        if (spec.IsEofEnabled && EofValidator.IsEof(accountCode, out _))
        {
            stack.PushUInt256(2);
        }
        else
        {
            UInt256 result = (UInt256)accountCode.Length;
            stack.PushUInt256(in result);
        }
        return EvmExceptionType.None;
    }
}
