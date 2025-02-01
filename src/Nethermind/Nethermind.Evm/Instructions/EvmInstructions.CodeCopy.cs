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
        abstract static ReadOnlySpan<byte> GetCode(VirtualMachine vm);
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionCodeCopy<TOpCodeCopy>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpCodeCopy : struct, IOpCodeCopy
    {
        if (!stack.PopUInt256(out UInt256 a) || !stack.PopUInt256(out UInt256 b) || !stack.PopUInt256(out UInt256 result)) goto StackUnderflow;
        gasAvailable -= GasCostOf.VeryLow + GasCostOf.Memory * EvmPooledMemory.Div32Ceiling(in result, out bool outOfGas);
        if (outOfGas) goto OutOfGas;

        if (!result.IsZero)
        {
            if (!UpdateMemoryCost(vm.EvmState, ref gasAvailable, in a, result)) goto OutOfGas;
            ZeroPaddedSpan slice = TOpCodeCopy.GetCode(vm).SliceWithZeroPadding(in b, (int)result);
            vm.EvmState.Memory.Save(in a, in slice);
            if (vm.TxTracer.IsTracingInstructions)
            {
                vm.TxTracer.ReportMemoryChange(a, in slice);
            }
        }

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    public struct OpCallDataCopy : IOpCodeCopy
    {
        public static ReadOnlySpan<byte> GetCode(VirtualMachine vm)
            => vm.EvmState.Env.InputData.Span;
    }

    public struct OpCodeCopy : IOpCodeCopy
    {
        public static ReadOnlySpan<byte> GetCode(VirtualMachine vm)
            => vm.EvmState.Env.CodeInfo.MachineCode.Span;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionExtCodeCopy(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        IReleaseSpec spec = vm.Spec;
        Address address = stack.PopAddress();
        if (address is null || !stack.PopUInt256(out UInt256 a) || !stack.PopUInt256(out UInt256 b) || !stack.PopUInt256(out UInt256 result)) goto StackUnderflow;

        gasAvailable -= spec.GetExtCodeCost() + GasCostOf.Memory * EvmPooledMemory.Div32Ceiling(in result, out bool outOfGas);
        if (outOfGas) goto OutOfGas;

        if (!ChargeAccountAccessGas(ref gasAvailable, vm, address)) goto OutOfGas;

        if (!result.IsZero)
        {
            if (!UpdateMemoryCost(vm.EvmState, ref gasAvailable, in a, result)) goto OutOfGas;

            ReadOnlySpan<byte> externalCode = vm.CodeInfoRepository.GetCachedCodeInfo(vm.WorldState, address, followDelegation: false, spec, out _).MachineCode.Span;
            if (spec.IsEofEnabled && EofValidator.IsEof(externalCode, out _))
            {
                externalCode = EofValidator.MAGIC;
            }
            ZeroPaddedSpan slice = externalCode.SliceWithZeroPadding(in b, (int)result);
            vm.EvmState.Memory.Save(in a, in slice);
            if (vm.TxTracer.IsTracingInstructions)
            {
                vm.TxTracer.ReportMemoryChange(a, in slice);
            }
        }

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionExtCodeSize(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        IReleaseSpec spec = vm.Spec;
        gasAvailable -= spec.GetExtCodeCost();

        Address address = stack.PopAddress();
        if (address is null) goto StackUnderflow;

        if (!ChargeAccountAccessGas(ref gasAvailable, vm, address)) goto OutOfGas;

        ReadOnlySpan<byte> codeSection = vm.EvmState.Env.CodeInfo.MachineCode.Span;
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
                if (!stack.PopLimbo()) goto StackUnderflow;
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

        ReadOnlySpan<byte> accountCode = vm.CodeInfoRepository.GetCachedCodeInfo(vm.WorldState, address, followDelegation: false, spec, out _).MachineCode.Span;
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
    // Jump forward to be unpredicted by the branch predictor
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }
}
