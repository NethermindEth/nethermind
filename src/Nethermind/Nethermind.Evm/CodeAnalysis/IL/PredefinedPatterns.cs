// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.State;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static Nethermind.Evm.CodeAnalysis.IL.IlInfo;
using Nethermind.Evm.Tracing;

namespace Nethermind.Evm.CodeAnalysis.IL.Patterns;

internal class MethodSelector : InstructionChunk
{
    public string Name => nameof(MethodSelector);
    public byte[] Pattern => [(byte)Instruction.PUSH1, (byte)Instruction.PUSH1, (byte)Instruction.MSTORE, (byte)Instruction.CALLVALUE, (byte)Instruction.DUP1];
    public byte CallCount { get; set; } = 0;

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = GasCostOf.VeryLow + GasCostOf.VeryLow + GasCostOf.VeryLow + GasCostOf.Base + GasCostOf.VeryLow;
        return gasCost;
    }

    public void Invoke<T>(EvmState vmState, IBlockhashProvider blockhashProvider, IWorldState worldState, ICodeInfoRepository codeInfoRepository, IReleaseSpec spec,
        ref int programCounter,
        ref long gasAvailable,
        ref EvmStack<T> stack,
        ref ILChunkExecutionResult result) where T : struct, VirtualMachine.IIsTracing
    {
        CallCount++;

        if (!VirtualMachine<T>.UpdateGas(GasCost(vmState, spec), ref gasAvailable))
            result.ExceptionType = EvmExceptionType.OutOfGas;

        byte value = vmState.Env.CodeInfo.MachineCode.Span[programCounter + 1];
        byte location = vmState.Env.CodeInfo.MachineCode.Span[programCounter + 3];
        VirtualMachine<T>.UpdateMemoryCost(ref vmState.Memory, ref gasAvailable, location, 32);
        vmState.Memory.SaveByte(location, value);
        stack.PushUInt256(vmState.Env.Value);
        stack.PushUInt256(vmState.Env.Value);

        programCounter += 2 + 2 + 1 + 1 + 1;
    }
}

internal class IsContractCheck : InstructionChunk
{
    public string Name => nameof(IsContractCheck);
    public byte[] Pattern => [(byte)Instruction.EXTCODESIZE, (byte)Instruction.DUP1, (byte)Instruction.ISZERO];
    public byte CallCount { get; set; } = 0;

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = spec.GetExtCodeCost() + GasCostOf.VeryLow + GasCostOf.VeryLow;
        return gasCost;
    }

    public void Invoke<T>(EvmState vmState, IBlockhashProvider blockhashProvider, IWorldState worldState, ICodeInfoRepository codeInfoRepository, IReleaseSpec spec,
        ref int programCounter,
        ref long gasAvailable,
        ref EvmStack<T> stack,
        ref ILChunkExecutionResult result) where T : struct, VirtualMachine.IIsTracing
    {
        CallCount++;

        if (!VirtualMachine<T>.UpdateGas(GasCost(vmState, spec), ref gasAvailable))
            result.ExceptionType = EvmExceptionType.OutOfGas;

        Address address = stack.PopAddress();

        if (!VirtualMachine<VirtualMachine.NotTracing>.ChargeAccountAccessGas(ref gasAvailable, vmState, address, spec, NullTxTracer.Instance))
            result.ExceptionType = EvmExceptionType.OutOfGas;

        int contractCodeSize = codeInfoRepository.GetCachedCodeInfo(worldState, address, spec).MachineCode.Length;
        stack.PushUInt32(contractCodeSize);
        if (contractCodeSize == 0)
        {
            stack.PushOne();
        }
        else
        {
            stack.PushZero();
        }

        programCounter += 3;
    }

}
internal class EmulatedStaticJump : InstructionChunk
{
    public string Name => nameof(EmulatedStaticJump);
    public byte[] Pattern => [(byte)Instruction.PUSH2, (byte)Instruction.JUMP];
    public byte CallCount { get; set; } = 0;

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = GasCostOf.VeryLow + GasCostOf.Mid;
        return gasCost;
    }

    public void Invoke<T>(EvmState vmState, IBlockhashProvider blockhashProvider, IWorldState worldState, ICodeInfoRepository codeInfoRepository, IReleaseSpec spec,
        ref int programCounter,
        ref long gasAvailable,
        ref EvmStack<T> stack,
        ref ILChunkExecutionResult result) where T : struct, VirtualMachine.IIsTracing
    {
        CallCount++;

        if (!VirtualMachine<T>.UpdateGas(GasCost(vmState, spec), ref gasAvailable))
            result.ExceptionType = EvmExceptionType.OutOfGas;

        int jumpdestionation = (vmState.Env.CodeInfo.MachineCode.Span[programCounter + 1] << 8) | vmState.Env.CodeInfo.MachineCode.Span[programCounter + 2];
        if (jumpdestionation < vmState.Env.CodeInfo.MachineCode.Length && vmState.Env.CodeInfo.MachineCode.Span[jumpdestionation] == (byte)Instruction.JUMPDEST)
        {
            programCounter = jumpdestionation;
        }
        else
        {
            result.ExceptionType = EvmExceptionType.InvalidJumpDestination;
        }
    }

}
internal class EmulatedStaticCJump : InstructionChunk
{
    public string Name => nameof(EmulatedStaticCJump);
    public byte[] Pattern => [(byte)Instruction.PUSH2, (byte)Instruction.JUMPI];
    public byte CallCount { get; set; } = 0;

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = GasCostOf.VeryLow + GasCostOf.High;
        return gasCost;
    }

    public void Invoke<T>(EvmState vmState, IBlockhashProvider blockhashProvider, IWorldState worldState, ICodeInfoRepository codeInfoRepository, IReleaseSpec spec,
        ref int programCounter,
        ref long gasAvailable,
        ref EvmStack<T> stack,
        ref ILChunkExecutionResult result) where T : struct, VirtualMachine.IIsTracing
    {
        CallCount++;

        if (!VirtualMachine<T>.UpdateGas(GasCost(vmState, spec), ref gasAvailable))
            result.ExceptionType = EvmExceptionType.OutOfGas;

        stack.PopUInt256(out UInt256 condition);
        int jumpdestionation = (vmState.Env.CodeInfo.MachineCode.Span[programCounter + 1] << 8) | vmState.Env.CodeInfo.MachineCode.Span[programCounter + 2];
        if (!condition.IsZero)
        {
            if (jumpdestionation < vmState.Env.CodeInfo.MachineCode.Length && vmState.Env.CodeInfo.MachineCode.Span[jumpdestionation] == (byte)Instruction.JUMPDEST)
            {
                programCounter = jumpdestionation;
            }
            else
            {
                result.ExceptionType = EvmExceptionType.InvalidJumpDestination;
            }
        }
        else
        {
            programCounter += 4;
        }
    }
}
