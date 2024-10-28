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
using System.Runtime.Intrinsics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
namespace Nethermind.Evm.CodeAnalysis.IL.Patterns;
using static System.Runtime.CompilerServices.Unsafe;

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
        VirtualMachine<T>.UpdateMemoryCost(vmState, ref gasAvailable, location, 32);
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

        if (!VirtualMachine<VirtualMachine.NotTracing>.ChargeAccountAccessGas(ref gasAvailable, vmState, address, false, worldState, spec, NullTxTracer.Instance, true))
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
internal class PP : InstructionChunk
{
    public string Name => nameof(PP);
    public byte[] Pattern => [(byte)Instruction.POP, (byte)Instruction.POP];
    public byte CallCount { get; set; } = 0;

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = GasCostOf.Base + GasCostOf.Base;
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

        stack.Head -= 2;
        //stack.PopLimbo();
        //stack.PopLimbo();

        programCounter += 2;
    }
}
internal class P01P01SHL : InstructionChunk
{
    public string Name => nameof(P01P01SHL);
    public byte[] Pattern => [(byte)Instruction.PUSH1, (byte)Instruction.PUSH1, (byte)Instruction.SHL];
    public byte CallCount { get; set; } = 0;

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = GasCostOf.VeryLow * 3;
        return gasCost;
    }

    public void Invoke<T>(EvmState vmState, IBlockhashProvider blockhashProvider, IWorldState worldState, ICodeInfoRepository codeInfoRepository, IReleaseSpec spec,
        ref int programCounter,
        ref long gasAvailable,
        ref EvmStack<T> stack,
        ref ILChunkExecutionResult result) where T : struct, VirtualMachine.IIsTracing
    {
        CallCount++;

        if (!spec.ShiftOpcodesEnabled) result.ExceptionType = EvmExceptionType.BadInstruction;
        if (!VirtualMachine<T>.UpdateGas(GasCost(vmState, spec), ref gasAvailable))
            result.ExceptionType = EvmExceptionType.OutOfGas;

        stack.PushUInt256((UInt256)vmState.Env.CodeInfo.MachineCode.Span[programCounter + 1] << (int)((UInt256)vmState.Env.CodeInfo.MachineCode.Span[programCounter + 3]).u0);

        programCounter += 5;

    }
}
internal class PJ : InstructionChunk
{
    public string Name => nameof(PJ);
    public byte[] Pattern => [(byte)Instruction.POP, (byte)Instruction.JUMP];
    public byte CallCount { get; set; } = 0;

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = GasCostOf.Base + GasCostOf.Mid;
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

        //stack.Head--;
        stack.PopLimbo();
        stack.PopUInt256(out UInt256 jumpDestination);

        var jumpDestinationInt = (int)jumpDestination;

        if (jumpDestinationInt < vmState.Env.CodeInfo.MachineCode.Length && vmState.Env.CodeInfo.MachineCode.Span[jumpDestinationInt] == (byte)Instruction.JUMPDEST)
        {
            programCounter = jumpDestinationInt;
        }
        else
        {
            result.ExceptionType = EvmExceptionType.InvalidJumpDestination;
        }
    }
}
internal class S02P : InstructionChunk
{
    public string Name => nameof(S02P);
    public byte[] Pattern => [(byte)Instruction.SWAP2, (byte)Instruction.POP];
    public byte CallCount { get; set; } = 0;

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = GasCostOf.VeryLow + GasCostOf.Base;
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

        stack.Swap(3);
        stack.Head--;

        programCounter += 2;
    }
}
internal class S01P : InstructionChunk
{
    public string Name => nameof(S01P);
    public byte[] Pattern => [(byte)Instruction.SWAP1, (byte)Instruction.POP];
    public byte CallCount { get; set; } = 0;

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = GasCostOf.VeryLow + GasCostOf.Base;
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

        stack.Swap(2);
        stack.Head--;

        programCounter += 2;
    }
}
internal class P01SHL : InstructionChunk
{
    public string Name => nameof(P01SHL);
    public byte[] Pattern => [(byte)Instruction.PUSH1, (byte)Instruction.SHL];
    public byte CallCount { get; set; } = 0;

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = GasCostOf.VeryLow + GasCostOf.VeryLow;
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

        stack.PopUInt256(out UInt256 value);

        stack.PushUInt256(value << (int)vmState.Env.CodeInfo.MachineCode.Span[programCounter + 2]);
        programCounter += 3;

    }
}
internal class P01D02 : InstructionChunk
{
    public string Name => nameof(P01D02);
    public byte[] Pattern => [(byte)Instruction.PUSH1, (byte)Instruction.DUP2];
    public byte CallCount { get; set; } = 0;

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = GasCostOf.VeryLow + GasCostOf.VeryLow;
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

        stack.PushUInt256(vmState.Env.CodeInfo.MachineCode.Span[programCounter + 1]);
        stack.Dup(2);

        programCounter += 3;

    }
}
internal class P01D03 : InstructionChunk
{
    public string Name => nameof(P01D03);
    public byte[] Pattern => [(byte)Instruction.PUSH1, (byte)Instruction.DUP3];
    public byte CallCount { get; set; } = 0;

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = GasCostOf.VeryLow + GasCostOf.VeryLow;
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

        stack.PushUInt256(vmState.Env.CodeInfo.MachineCode.Span[programCounter + 1]);
        stack.Dup(3);

        programCounter += 3;

    }
}
internal class S02S01 : InstructionChunk
{
    public string Name => nameof(S02S01);
    public byte[] Pattern => [(byte)Instruction.SWAP2, (byte)Instruction.SWAP1];
    public byte CallCount { get; set; } = 0;

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = GasCostOf.VeryLow + GasCostOf.VeryLow;
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

        stack.Swap(3);
        stack.Swap(2);

        programCounter += 2;

    }
}
internal class D01P04EQ : InstructionChunk
{
    public string Name => nameof(D01P04EQ);
    public byte[] Pattern => [(byte)Instruction.DUP1, (byte)Instruction.PUSH4, (byte)Instruction.EQ];
    public byte CallCount { get; set; } = 0;

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = 3 * GasCostOf.VeryLow;
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


        ReadOnlySpan<byte> fourByteSpan = vmState.Env.CodeInfo.MachineCode.Span.Slice(programCounter + 2, 4);

        Span<byte> word = stack.PeekWord256();

        Span<byte> paddedSpan = stackalloc byte[32];

        fourByteSpan.CopyTo(paddedSpan.Slice(28, 4));
        if (paddedSpan.SequenceEqual(word))
        {
            stack.PushOne();
        }
        else
        {
            stack.PushZero();
        }

        programCounter += 7;

    }
}
internal class D01P04GT : InstructionChunk
{
    public string Name => nameof(D01P04GT);
    public byte[] Pattern => [(byte)Instruction.DUP1, (byte)Instruction.PUSH4, (byte)Instruction.GT];
    public byte CallCount { get; set; } = 0;

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = 3 * GasCostOf.VeryLow;
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

        ReadOnlySpan<byte> fourByteSpan = vmState.Env.CodeInfo.MachineCode.Span.Slice(programCounter + 2, 4);

        UInt256 rhs = new UInt256(stack.PeekWord256(), true);
        UInt256 lhs = new UInt256(fourByteSpan, true);

        if (lhs > rhs)
        {
            stack.PushOne();
        }
        else
        {
            stack.PushZero();
        }

        programCounter += 7;

    }
}
internal class D02MST : InstructionChunk
{
    public string Name => nameof(D02MST);
    public byte[] Pattern => [(byte)Instruction.DUP2, (byte)Instruction.MSTORE];
    public byte CallCount { get; set; } = 0;

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = 2 * GasCostOf.VeryLow;
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


        stack.Dup(2);
        stack.PopUInt256(out UInt256 location);
        var bytes = stack.PopWord256();
        if (!VirtualMachine<T>.UpdateMemoryCost(vmState, ref gasAvailable, in location, (UInt256)32))
            result.ExceptionType = EvmExceptionType.OutOfGas;
        vmState.Memory.SaveWord(in location, bytes);
        stack.PushUInt256(location);

        programCounter += 2;

    }
}
internal class P01ADDS01D02MST : InstructionChunk
{
    public string Name => nameof(P01ADDS01D02MST);
    public byte[] Pattern => [(byte)Instruction.PUSH1, (byte)Instruction.ADD, (byte)Instruction.SWAP1, (byte)Instruction.DUP2, (byte)Instruction.MSTORE];
    public byte CallCount { get; set; } = 0;

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = 3 * GasCostOf.VeryLow;
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


        if (!stack.PopUInt256(out UInt256 location))
            result.ExceptionType = EvmExceptionType.StackUnderflow;

        location = location + (new UInt256(vmState.Env.CodeInfo.MachineCode.Span.Slice(programCounter + 3, 1)));

        if (!VirtualMachine<T>.UpdateMemoryCost(vmState, ref gasAvailable, location, 32))
            result.ExceptionType = EvmExceptionType.OutOfGas;

        vmState.Memory.SaveWord(location, stack.PopWord256());
        stack.PushUInt256(location);

        programCounter += 6;

    }
}
internal class P01MLD01S02SUB : InstructionChunk
{
    public string Name => nameof(P01MLD01S02SUB);
    public byte[] Pattern => [(byte)Instruction.PUSH1, (byte)Instruction.MLOAD, (byte)Instruction.DUP1, (byte)Instruction.SWAP2, (byte)Instruction.SUB];
    public byte CallCount { get; set; } = 0;

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = 5 * GasCostOf.VeryLow;
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


        stack.PopUInt256(out UInt256 a);

        var value = (UInt256)vmState.Env.CodeInfo.MachineCode.Span[programCounter + 1];
        var bytes = vmState.Memory.LoadSpan(in value);
        if (!VirtualMachine<T>.UpdateMemoryCost(vmState, ref gasAvailable, in value, 32))
            result.ExceptionType = EvmExceptionType.OutOfGas;
        var b = new UInt256(bytes, true);
        UInt256.Subtract(in a, in b, out var c);
        stack.PushBytes(bytes);
        stack.PushUInt256(c);
        programCounter += 6;

    }
}
internal class P01CDLP01SHRD01P04 : InstructionChunk
{
    public string Name => nameof(P01CDLP01SHRD01P04);
    public byte[] Pattern => [(byte)Instruction.PUSH1, (byte)Instruction.CALLDATALOAD, (byte)Instruction.PUSH1, (byte)Instruction.SHR, (byte)Instruction.DUP1, (byte)Instruction.PUSH4];
    public byte CallCount { get; set; } = 0;

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = 6 * GasCostOf.VeryLow;
        return gasCost;
    }

    public void Invoke<T>(EvmState vmState, IBlockhashProvider blockhashProvider, IWorldState worldState, ICodeInfoRepository codeInfoRepository, IReleaseSpec spec,
        ref int programCounter,
        ref long gasAvailable,
        ref EvmStack<T> stack,
        ref ILChunkExecutionResult result) where T : struct, VirtualMachine.IIsTracing
    {
        CallCount++;

        if (!spec.ShiftOpcodesEnabled) result.ExceptionType = EvmExceptionType.BadInstruction;

        if (!VirtualMachine<T>.UpdateGas(GasCost(vmState, spec), ref gasAvailable))
            result.ExceptionType = EvmExceptionType.OutOfGas;


        byte pos = vmState.Env.CodeInfo.MachineCode.Span[programCounter + 1];
        UInt256 _pos = new UInt256(pos);
        byte a = vmState.Env.CodeInfo.MachineCode.Span[programCounter + 4];
        var data = vmState.Env.InputData.SliceWithZeroPadding(_pos, 32).Span;
        Span<byte> paddedSpan = stackalloc byte[32];

        data.CopyTo(paddedSpan);
        var b = new UInt256(paddedSpan, true);
        var rightShift = b >> (int)a;
        stack.PushUInt256(rightShift);
        stack.PushUInt256(rightShift);

        ReadOnlySpan<byte> fourByteSpan = vmState.Env.CodeInfo.MachineCode.Span.Slice(programCounter + 8, 4);

        stack.PushLeftPaddedBytes(fourByteSpan, 4);

        programCounter += 12;

    }
}
internal class P00CDLP01SHRD01P04 : InstructionChunk
{
    public string Name => nameof(P00CDLP01SHRD01P04);
    public byte[] Pattern => [(byte)Instruction.PUSH0, (byte)Instruction.CALLDATALOAD, (byte)Instruction.PUSH1, (byte)Instruction.SHR, (byte)Instruction.DUP1, (byte)Instruction.PUSH4];
    public byte CallCount { get; set; } = 0;

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = GasCostOf.Base + (5 * GasCostOf.VeryLow);
        return gasCost;
    }

    public void Invoke<T>(EvmState vmState, IBlockhashProvider blockhashProvider, IWorldState worldState, ICodeInfoRepository codeInfoRepository, IReleaseSpec spec,
        ref int programCounter,
        ref long gasAvailable,
        ref EvmStack<T> stack,
        ref ILChunkExecutionResult result) where T : struct, VirtualMachine.IIsTracing
    {
        CallCount++;

        if (!spec.ShiftOpcodesEnabled) result.ExceptionType = EvmExceptionType.BadInstruction;

        if (!spec.IncludePush0Instruction)
            result.ExceptionType = EvmExceptionType.BadInstruction; ;

        if (!VirtualMachine<T>.UpdateGas(GasCost(vmState, spec), ref gasAvailable))
            result.ExceptionType = EvmExceptionType.OutOfGas;

        byte a = vmState.Env.CodeInfo.MachineCode.Span[programCounter + 3];
        var data = vmState.Env.InputData.SliceWithZeroPadding(0, 32).Span;

        Span<byte> paddedSpan = stackalloc byte[32];
        data.CopyTo(paddedSpan);
        var b = new UInt256(paddedSpan, true);
        var rightShift = b >> (int)a;

        stack.PushUInt256(rightShift);
        stack.PushUInt256(rightShift);
        stack.PushLeftPaddedBytes(vmState.Env.CodeInfo.MachineCode.Span.Slice(programCounter + 7, 4), 4);

        programCounter += 1 + 1 + 2 + 1 + 1 + 5;

    }
}
internal class D04D02LTIZP02 : InstructionChunk
{
    public string Name => nameof(D04D02LTIZP02);
    public byte[] Pattern => [(byte)Instruction.DUP4, (byte)Instruction.DUP2, (byte)Instruction.LT, (byte)Instruction.ISZERO, (byte)Instruction.PUSH2];
    public byte CallCount { get; set; } = 0;

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = 5 * GasCostOf.VeryLow;
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

        stack.Dup(4);
        stack.PopUInt256(out var rhs);
        UInt256 lhs = new UInt256(stack.PeekWord256(), true);

        if (lhs < rhs)
        {
            stack.PushZero();
        }
        else
        {
            stack.PushOne();
        }

        stack.PushLeftPaddedBytes(vmState.Env.CodeInfo.MachineCode.Span.Slice(programCounter + 5, 2), 2);

        programCounter += 4 + 3;
    }
}
internal class P20ANDP20ANDD02 : InstructionChunk
{
    public string Name => nameof(P20ANDP20ANDD02);
    public byte[] Pattern => [(byte)Instruction.PUSH20, (byte)Instruction.AND, (byte)Instruction.PUSH20, (byte)Instruction.AND, (byte)Instruction.DUP2];
    public byte CallCount { get; set; } = 0;

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = 5 * GasCostOf.VeryLow;
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

        ref byte bytesRef = ref stack.PopBytesByRef();
        Vector256<byte> aVec = ReadUnaligned<Vector256<byte>>(ref bytesRef);
        var twentyByteSpan = vmState.Env.CodeInfo.MachineCode.Span.Slice(programCounter + 1, 20);
        Span<byte> paddedSpan = stackalloc byte[32];
        Span<byte> paddedSpan2 = stackalloc byte[32];
        twentyByteSpan.CopyTo(paddedSpan.Slice(12, 20));
        twentyByteSpan = vmState.Env.CodeInfo.MachineCode.Span.Slice(programCounter + 23, 20);
        twentyByteSpan.CopyTo(paddedSpan2.Slice(12, 20));
        Vector256<byte> bVec = ReadUnaligned<Vector256<byte>>(ref paddedSpan[0]);
        Vector256<byte> cVec = ReadUnaligned<Vector256<byte>>(ref paddedSpan2[0]);
        WriteUnaligned(ref stack.PushBytesRef(), Vector256.BitwiseAnd(Vector256.BitwiseAnd(aVec, bVec), cVec));
        stack.Dup(2);
        programCounter += 21 + 1 + 21 + 1 + 1;

    }
}
internal class GASP01MSTP01CDSLT : InstructionChunk
{
    public string Name => nameof(GASP01MSTP01CDSLT);
    public byte[] Pattern => [(byte)Instruction.GAS, (byte)Instruction.PUSH1, (byte)Instruction.MSTORE, (byte)Instruction.PUSH1, (byte)Instruction.CALLDATASIZE, (byte)Instruction.LT];
    public byte CallCount { get; set; } = 0;

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = (2 * GasCostOf.Base) + (4 * GasCostOf.VeryLow);
        return gasCost;
    }

    public void Invoke<T>(EvmState vmState, IBlockhashProvider blockhashProvider, IWorldState worldState, ICodeInfoRepository codeInfoRepository, IReleaseSpec spec,
        ref int programCounter,
        ref long gasAvailable,
        ref EvmStack<T> stack,
        ref ILChunkExecutionResult result) where T : struct, VirtualMachine.IIsTracing
    {
        CallCount++;

        var gasAvailableAtPC = gasAvailable - GasCostOf.Base;

        if (!VirtualMachine<T>.UpdateGas(GasCost(vmState, spec), ref gasAvailable))
            result.ExceptionType = EvmExceptionType.OutOfGas;

        var location = (UInt256)vmState.Env.CodeInfo.MachineCode.Span[programCounter + 2];

        Span<byte> wordSpan = stackalloc byte[32];
        wordSpan.Clear();

        Span<byte> longSpan = stackalloc byte[8];

        BitConverter.TryWriteBytes(longSpan, gasAvailableAtPC);

        if (BitConverter.IsLittleEndian)
        {
            longSpan.Reverse();
        }

        longSpan.CopyTo(wordSpan.Slice(24, 8));

        if (!VirtualMachine<T>.UpdateMemoryCost(vmState, ref gasAvailable, location, 32))
            result.ExceptionType = EvmExceptionType.OutOfGas;

        vmState.Memory.SaveWord(location, wordSpan);

        var a = (UInt256)vmState.Env.InputData.Length;
        var b = (UInt256)vmState.Env.CodeInfo.MachineCode.Span[programCounter + 5];

        if (a < b)
        {
            stack.PushOne();
        }
        else
        {
            stack.PushZero();
        }

        programCounter += 1 + 2 + 1 + 2 + 1 + 1;

    }
}
internal class GASP01P01MSTCV : InstructionChunk
{
    public string Name => nameof(GASP01P01MSTCV);
    public byte[] Pattern => [(byte)Instruction.GAS, (byte)Instruction.PUSH1, (byte)Instruction.PUSH1, (byte)Instruction.MSTORE, (byte)Instruction.CALLVALUE];
    public byte CallCount { get; set; } = 0;

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = (2 * GasCostOf.Base) + (3 * GasCostOf.VeryLow);
        return gasCost;
    }

    public void Invoke<T>(EvmState vmState, IBlockhashProvider blockhashProvider, IWorldState worldState, ICodeInfoRepository codeInfoRepository, IReleaseSpec spec,
        ref int programCounter,
        ref long gasAvailable,
        ref EvmStack<T> stack,
        ref ILChunkExecutionResult result) where T : struct, VirtualMachine.IIsTracing
    {
        CallCount++;

        var gasAvailableAtPC = gasAvailable - GasCostOf.Base;

        if (!VirtualMachine<T>.UpdateGas(GasCost(vmState, spec), ref gasAvailable))
            result.ExceptionType = EvmExceptionType.OutOfGas;

        stack.PushUInt256((UInt256)gasAvailableAtPC);

        byte value = vmState.Env.CodeInfo.MachineCode.Span[programCounter + 2];
        byte location = vmState.Env.CodeInfo.MachineCode.Span[programCounter + 4];

        if (!VirtualMachine<T>.UpdateMemoryCost(vmState, ref gasAvailable, location, 32))
            result.ExceptionType = EvmExceptionType.OutOfGas;

        vmState.Memory.SaveByte((UInt256)location + 31, value);

        stack.PushUInt256(vmState.Env.Value);
        programCounter += 1 + 2 + 2 + 1 + 1;

    }
}
internal class MSTP01S01KECSL : InstructionChunk
{
    public string Name => nameof(MSTP01S01KECSL);
    public byte[] Pattern => [(byte)Instruction.MSTORE, (byte)Instruction.PUSH1, (byte)Instruction.SWAP1, (byte)Instruction.KECCAK256, (byte)Instruction.SLOAD];
    public byte CallCount { get; set; } = 0;

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = 3 * GasCostOf.VeryLow + GasCostOf.Sha3 + spec.GetSLoadCost();
        if (spec.UseHotAndColdStorage)
        {
            gasCost += GasCostOf.WarmStateRead;
        }
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

        stack.PopUInt256(out var location);
        var bytes = stack.PopWord256();

        if (!VirtualMachine<T>.UpdateMemoryCost(vmState, ref gasAvailable, location, 32))
            result.ExceptionType = EvmExceptionType.OutOfGas;

        vmState.Memory.SaveWord(in location, bytes);

        stack.PopUInt256(out location);
        var length = (UInt256)vmState.Env.CodeInfo.MachineCode.Span[programCounter + 2];

        bytes = vmState.Memory.LoadSpan(in location, length);

        var cost = GasCostOf.Sha3Word * EvmPooledMemory.Div32Ceiling(in length, out bool outOfGas);
        if (outOfGas) result.ExceptionType = EvmExceptionType.OutOfGas;

        if (!VirtualMachine<T>.UpdateGas(cost, ref gasAvailable))
            result.ExceptionType = EvmExceptionType.OutOfGas;

        Span<byte> hash = stackalloc byte[32];
        KeccakCache.ComputeTo(bytes, out As<byte, ValueHash256>(ref hash[0]));

        StorageCell storageCell = new(vmState.Env.ExecutingAccount, new UInt256(hash, true));
        ReadOnlySpan<byte> value = worldState.Get(in storageCell);
        stack.PushBytes(value);

        programCounter += 1 + 2 + 1 + 1 + 1;

    }
}
internal class D02D02ADDMLD04D03ADDMST : InstructionChunk
{
    public string Name => nameof(D02D02ADDMLD04D03ADDMST);
    public byte[] Pattern => [(byte)Instruction.DUP2, (byte)Instruction.DUP2, (byte)Instruction.ADD, (byte)Instruction.MLOAD, (byte)Instruction.DUP4, (byte)Instruction.DUP3, (byte)Instruction.ADD, (byte)Instruction.MSTORE];
    public byte CallCount { get; set; } = 0;

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = 8 * GasCostOf.VeryLow;
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
        stack.Dup(1);
        stack.PopUInt256(out UInt256 stackItem1);
        stack.Dup(2);
        stack.PopUInt256(out UInt256 stackItem2);
        stack.Dup(3);
        stack.PopUInt256(out UInt256 stackItem3);

        var mloadLocation = stackItem1 + stackItem2;
        var mstoreLocation = stackItem1 + stackItem3;

        if (!(VirtualMachine<T>.UpdateMemoryCost(vmState, ref gasAvailable, mloadLocation, 32)
                    && VirtualMachine<T>.UpdateMemoryCost(vmState, ref gasAvailable, mstoreLocation, 32)))
            result.ExceptionType = EvmExceptionType.OutOfGas;

        var bytes = vmState.Memory.LoadSpan(in mloadLocation);
        vmState.Memory.SaveWord(mstoreLocation, bytes);

        programCounter += 8;

    }
}
internal class D02S01SHRD03D02MUL : InstructionChunk
{
    public string Name => nameof(D02S01SHRD03D02MUL);
    public byte[] Pattern => [(byte)Instruction.DUP2, (byte)Instruction.SWAP1, (byte)Instruction.SHR, (byte)Instruction.DUP3, (byte)Instruction.DUP2, (byte)Instruction.MUL];
    public byte CallCount { get; set; } = 0;

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = 6 * GasCostOf.VeryLow;
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


        stack.PopUInt256(out UInt256 stackItem1);
        stack.Dup(1);
        stack.PopUInt256(out UInt256 stackItem2);
        stack.Dup(2);
        stack.PopUInt256(out UInt256 stackItem3);

        UInt256 shrValue;

        if (!spec.ShiftOpcodesEnabled) result.ExceptionType = EvmExceptionType.BadInstruction;

        if (stackItem1 >= 256)
        {
            stack.PopLimbo();
            shrValue = 0;
        }
        else
        {
            shrValue = stackItem2 >> (int)stackItem1.u0;
        }

        stack.PushUInt256(shrValue);
        stack.PushUInt256(shrValue * stackItem3);

        programCounter += 6;

    }
}
internal class S04S01S04ADDS03P01ADD : InstructionChunk
{
    public string Name => nameof(S04S01S04ADDS03P01ADD);
    public byte[] Pattern => [(byte)Instruction.SWAP4, (byte)Instruction.SWAP1, (byte)Instruction.SWAP4, (byte)Instruction.ADD, (byte)Instruction.SWAP3, (byte)Instruction.PUSH1, (byte)Instruction.ADD];
    public byte CallCount { get; set; } = 0;

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = 7 * GasCostOf.VeryLow;
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


        var a = (UInt256)vmState.Env.CodeInfo.MachineCode.Span[programCounter + 6];
        stack.PopUInt256(out UInt256 stackItem1);
        stack.PopUInt256(out UInt256 stackItem2);
        stack.PopUInt256(out UInt256 stackItem3);
        stack.PopUInt256(out UInt256 stackItem4);
        stack.PopUInt256(out UInt256 stackItem5);
        stack.PushUInt256(stackItem5 + stackItem1);
        stack.PushUInt256(stackItem4);
        stack.PushUInt256(stackItem3);
        stack.PushUInt256(a + stackItem2);

        programCounter += 8;

    }
}
