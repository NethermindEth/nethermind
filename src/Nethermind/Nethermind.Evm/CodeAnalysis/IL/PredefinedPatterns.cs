// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.State;
using System;
using System.Linq;
using static Nethermind.Evm.CodeAnalysis.IL.IlInfo;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;

namespace Nethermind.Evm.CodeAnalysis.IL.Patterns;

internal class MethodSelector : IPatternChunk
{
    public string Name => nameof(MethodSelector);
    public byte[] Pattern => [(byte)Instruction.PUSH1, (byte)Instruction.PUSH1, (byte)Instruction.MSTORE, (byte)Instruction.CALLVALUE, (byte)Instruction.DUP1];

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = GasCostOf.VeryLow + GasCostOf.VeryLow + GasCostOf.VeryLow + GasCostOf.Base + GasCostOf.VeryLow;
        return gasCost;
    }

    public void Invoke<T>(EvmState vmState, ulong chainId, in ExecutionEnvironment env, in TxExecutionContext txCtx, in BlockExecutionContext blkCtx, IBlockhashProvider blockhashProvider, IWorldState worldState, ICodeInfoRepository codeInfoRepository, IReleaseSpec spec, ref int programCounter, ref long gasAvailable, ref EvmStack<T> stack, ref ReadOnlyMemory<byte> returnDataBuffer, ITxTracer trace, ILogger logger, ref ILChunkExecutionState result) where T : struct, VirtualMachine.IIsTracing
    {
        if (!VirtualMachine<T, VirtualMachine.NotOptimizing>.UpdateGas(GasCost(vmState, spec), ref gasAvailable))
            result.ExceptionType = EvmExceptionType.OutOfGas;

        byte value = vmState.Env.CodeInfo.MachineCode.Span[programCounter + 1];
        byte location = vmState.Env.CodeInfo.MachineCode.Span[programCounter + 3];
        VirtualMachine<T, VirtualMachine.NotOptimizing>.UpdateMemoryCost(vmState, ref gasAvailable, location, 32);
        vmState.Memory.SaveByte(location, value);
        stack.PushUInt256(vmState.Env.Value);
        stack.PushUInt256(vmState.Env.Value);

        programCounter += 2 + 2 + 1 + 1 + 1;
    }
}

internal class IsContractCheck : IPatternChunk
{
    public string Name => nameof(IsContractCheck);
    public byte[] Pattern => [(byte)Instruction.EXTCODESIZE, (byte)Instruction.DUP1, (byte)Instruction.ISZERO];

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = spec.GetExtCodeCost() + GasCostOf.VeryLow + GasCostOf.VeryLow;
        return gasCost;
    }

    public void Invoke<T>(EvmState vmState, ulong chainId, in ExecutionEnvironment env, in TxExecutionContext txCtx, in BlockExecutionContext blkCtx, IBlockhashProvider blockhashProvider, IWorldState worldState, ICodeInfoRepository codeInfoRepository, IReleaseSpec spec, ref int programCounter, ref long gasAvailable, ref EvmStack<T> stack, ref ReadOnlyMemory<byte> returnDataBuffer, ITxTracer trace, ILogger logger, ref ILChunkExecutionState result) where T : struct, VirtualMachine.IIsTracing
    {
        if (!VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.NotOptimizing>.UpdateGas(GasCost(vmState, spec), ref gasAvailable))
            result.ExceptionType = EvmExceptionType.OutOfGas;

        Address address = stack.PopAddress();

        if (!VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.NotOptimizing>.ChargeAccountAccessGas(ref gasAvailable, vmState, address, false, worldState, spec, NullTxTracer.Instance, true))
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
internal class EmulatedStaticJump : IPatternChunk
{
    public string Name => nameof(EmulatedStaticJump);
    public byte[] Pattern => [(byte)Instruction.PUSH2, (byte)Instruction.JUMP];

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = GasCostOf.VeryLow + GasCostOf.Mid;
        return gasCost;
    }

    public void Invoke<T>(EvmState vmState, ulong chainId, in ExecutionEnvironment env, in TxExecutionContext txCtx, in BlockExecutionContext blkCtx, IBlockhashProvider blockhashProvider, IWorldState worldState, ICodeInfoRepository codeInfoRepository, IReleaseSpec spec, ref int programCounter, ref long gasAvailable, ref EvmStack<T> stack, ref ReadOnlyMemory<byte> returnDataBuffer, ITxTracer trace, ILogger logger, ref ILChunkExecutionState result) where T : struct, VirtualMachine.IIsTracing
    {
        if (!VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.NotOptimizing>.UpdateGas(GasCost(vmState, spec), ref gasAvailable))
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
internal class EmulatedStaticCJump : IPatternChunk
{
    public string Name => nameof(EmulatedStaticCJump);
    public byte[] Pattern => [(byte)Instruction.PUSH2, (byte)Instruction.JUMPI];

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = GasCostOf.VeryLow + GasCostOf.High;
        return gasCost;
    }

    public void Invoke<T>(EvmState vmState, ulong chainId, in ExecutionEnvironment env, in TxExecutionContext txCtx, in BlockExecutionContext blkCtx, IBlockhashProvider blockhashProvider, IWorldState worldState, ICodeInfoRepository codeInfoRepository, IReleaseSpec spec, ref int programCounter, ref long gasAvailable, ref EvmStack<T> stack, ref ReadOnlyMemory<byte> returnDataBuffer, ITxTracer trace, ILogger logger, ref ILChunkExecutionState result) where T : struct, VirtualMachine.IIsTracing
    {
        if (!VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.NotOptimizing>.UpdateGas(GasCost(vmState, spec), ref gasAvailable))
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
internal class PP : IPatternChunk
{
    public string Name => nameof(PP);
    public byte[] Pattern => [(byte)Instruction.POP, (byte)Instruction.POP];

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = GasCostOf.Base + GasCostOf.Base;
        return gasCost;
    }

    public void Invoke<T>(EvmState vmState, ulong chainId, in ExecutionEnvironment env, in TxExecutionContext txCtx, in BlockExecutionContext blkCtx, IBlockhashProvider blockhashProvider, IWorldState worldState, ICodeInfoRepository codeInfoRepository, IReleaseSpec spec, ref int programCounter, ref long gasAvailable, ref EvmStack<T> stack, ref ReadOnlyMemory<byte> returnDataBuffer, ITxTracer trace, ILogger logger, ref ILChunkExecutionState result) where T : struct, VirtualMachine.IIsTracing
    {
        if (!VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.NotOptimizing>.UpdateGas(GasCost(vmState, spec), ref gasAvailable))
            result.ExceptionType = EvmExceptionType.OutOfGas;

        stack.Head -= 2;
        //stack.PopLimbo();
        //stack.PopLimbo();

        programCounter += 2;
    }
}
internal class P01P01SHL : IPatternChunk
{
    public string Name => nameof(P01P01SHL);
    public byte[] Pattern => [(byte)Instruction.PUSH1, (byte)Instruction.PUSH1, (byte)Instruction.SHL];

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = GasCostOf.VeryLow * 3;
        return gasCost;
    }

    public void Invoke<T>(EvmState vmState, ulong chainId, in ExecutionEnvironment env, in TxExecutionContext txCtx, in BlockExecutionContext blkCtx, IBlockhashProvider blockhashProvider, IWorldState worldState, ICodeInfoRepository codeInfoRepository, IReleaseSpec spec, ref int programCounter, ref long gasAvailable, ref EvmStack<T> stack, ref ReadOnlyMemory<byte> returnDataBuffer, ITxTracer trace, ILogger logger, ref ILChunkExecutionState result) where T : struct, VirtualMachine.IIsTracing
    {
        if (!VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.NotOptimizing>.UpdateGas(GasCost(vmState, spec), ref gasAvailable))
            result.ExceptionType = EvmExceptionType.OutOfGas;

        stack.PushUInt256((UInt256)vmState.Env.CodeInfo.MachineCode.Span[programCounter + 1] << (int)((UInt256)vmState.Env.CodeInfo.MachineCode.Span[programCounter + 3]).u0);

        programCounter += 5;
    }
}
internal class PJ : IPatternChunk
{
    public string Name => nameof(PJ);
    public byte[] Pattern => [(byte)Instruction.POP, (byte)Instruction.JUMP];

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = GasCostOf.Base + GasCostOf.Mid;
        return gasCost;
    }

    public void Invoke<T>(EvmState vmState, ulong chainId, in ExecutionEnvironment env, in TxExecutionContext txCtx, in BlockExecutionContext blkCtx, IBlockhashProvider blockhashProvider, IWorldState worldState, ICodeInfoRepository codeInfoRepository, IReleaseSpec spec, ref int programCounter, ref long gasAvailable, ref EvmStack<T> stack, ref ReadOnlyMemory<byte> returnDataBuffer, ITxTracer trace, ILogger logger, ref ILChunkExecutionState result) where T : struct, VirtualMachine.IIsTracing
    {
        if (!VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.NotOptimizing>.UpdateGas(GasCost(vmState, spec), ref gasAvailable))
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
internal class S02P : IPatternChunk
{
    public string Name => nameof(S02P);
    public byte[] Pattern => [(byte)Instruction.SWAP2, (byte)Instruction.POP];

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = GasCostOf.VeryLow + GasCostOf.Base;
        return gasCost;
    }

    public void Invoke<T>(EvmState vmState, ulong chainId, in ExecutionEnvironment env, in TxExecutionContext txCtx, in BlockExecutionContext blkCtx, IBlockhashProvider blockhashProvider, IWorldState worldState, ICodeInfoRepository codeInfoRepository, IReleaseSpec spec, ref int programCounter, ref long gasAvailable, ref EvmStack<T> stack, ref ReadOnlyMemory<byte> returnDataBuffer, ITxTracer trace, ILogger logger, ref ILChunkExecutionState result) where T : struct, VirtualMachine.IIsTracing
    {
        if (!VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.NotOptimizing>.UpdateGas(GasCost(vmState, spec), ref gasAvailable))
            result.ExceptionType = EvmExceptionType.OutOfGas;

        stack.Swap(3);
        stack.Head--;

        programCounter += 2;
    }
}
internal class S01P : IPatternChunk
{
    public string Name => nameof(S01P);
    public byte[] Pattern => [(byte)Instruction.SWAP1, (byte)Instruction.POP];

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = GasCostOf.VeryLow + GasCostOf.Base;
        return gasCost;
    }

    public void Invoke<T>(EvmState vmState, ulong chainId, in ExecutionEnvironment env, in TxExecutionContext txCtx, in BlockExecutionContext blkCtx, IBlockhashProvider blockhashProvider, IWorldState worldState, ICodeInfoRepository codeInfoRepository, IReleaseSpec spec, ref int programCounter, ref long gasAvailable, ref EvmStack<T> stack, ref ReadOnlyMemory<byte> returnDataBuffer, ITxTracer trace, ILogger logger, ref ILChunkExecutionState result) where T : struct, VirtualMachine.IIsTracing
    {
        if (!VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.NotOptimizing>.UpdateGas(GasCost(vmState, spec), ref gasAvailable))
            result.ExceptionType = EvmExceptionType.OutOfGas;

        stack.Swap(2);
        stack.Head--;

        programCounter += 2;
    }
}
internal class P01SHL : IPatternChunk
{
    public string Name => nameof(P01SHL);
    public byte[] Pattern => [(byte)Instruction.PUSH1, (byte)Instruction.SHL];

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = GasCostOf.VeryLow + GasCostOf.VeryLow;
        return gasCost;
    }

    public void Invoke<T>(EvmState vmState, ulong chainId, in ExecutionEnvironment env, in TxExecutionContext txCtx, in BlockExecutionContext blkCtx, IBlockhashProvider blockhashProvider, IWorldState worldState, ICodeInfoRepository codeInfoRepository, IReleaseSpec spec, ref int programCounter, ref long gasAvailable, ref EvmStack<T> stack, ref ReadOnlyMemory<byte> returnDataBuffer, ITxTracer trace, ILogger logger, ref ILChunkExecutionState result) where T : struct, VirtualMachine.IIsTracing
    {
        if (!VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.NotOptimizing>.UpdateGas(GasCost(vmState, spec), ref gasAvailable))
            result.ExceptionType = EvmExceptionType.OutOfGas;

        stack.PopUInt256(out UInt256 value);

        stack.PushUInt256(value << (int)vmState.Env.CodeInfo.MachineCode.Span[programCounter + 2]);
        programCounter += 3;
    }
}
internal class P01D02 : IPatternChunk
{
    public string Name => nameof(P01D02);
    public byte[] Pattern => [(byte)Instruction.PUSH1, (byte)Instruction.DUP2];

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = GasCostOf.VeryLow + GasCostOf.VeryLow;
        return gasCost;
    }

    public void Invoke<T>(EvmState vmState, ulong chainId, in ExecutionEnvironment env, in TxExecutionContext txCtx, in BlockExecutionContext blkCtx, IBlockhashProvider blockhashProvider, IWorldState worldState, ICodeInfoRepository codeInfoRepository, IReleaseSpec spec, ref int programCounter, ref long gasAvailable, ref EvmStack<T> stack, ref ReadOnlyMemory<byte> returnDataBuffer, ITxTracer trace, ILogger logger, ref ILChunkExecutionState result) where T : struct, VirtualMachine.IIsTracing
    {
        if (!VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.NotOptimizing>.UpdateGas(GasCost(vmState, spec), ref gasAvailable))
            result.ExceptionType = EvmExceptionType.OutOfGas;

        stack.PushUInt256(vmState.Env.CodeInfo.MachineCode.Span[programCounter + 1]);
        stack.Dup(2);

        programCounter += 3;
    }
}
internal class P01D03 : IPatternChunk
{
    public string Name => nameof(P01D03);
    public byte[] Pattern => [(byte)Instruction.PUSH1, (byte)Instruction.DUP3];

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = GasCostOf.VeryLow + GasCostOf.VeryLow;
        return gasCost;
    }

    public void Invoke<T>(EvmState vmState, ulong chainId, in ExecutionEnvironment env, in TxExecutionContext txCtx, in BlockExecutionContext blkCtx, IBlockhashProvider blockhashProvider, IWorldState worldState, ICodeInfoRepository codeInfoRepository, IReleaseSpec spec, ref int programCounter, ref long gasAvailable, ref EvmStack<T> stack, ref ReadOnlyMemory<byte> returnDataBuffer, ITxTracer trace, ILogger logger, ref ILChunkExecutionState result) where T : struct, VirtualMachine.IIsTracing
    {
        if (!VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.NotOptimizing>.UpdateGas(GasCost(vmState, spec), ref gasAvailable))
            result.ExceptionType = EvmExceptionType.OutOfGas;

        stack.PushUInt256(vmState.Env.CodeInfo.MachineCode.Span[programCounter + 1]);
        stack.Dup(3);

        programCounter += 3;
    }
}
internal class S02S01 : IPatternChunk
{
    public string Name => nameof(S02S01);
    public byte[] Pattern => [(byte)Instruction.SWAP2, (byte)Instruction.SWAP1];

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = GasCostOf.VeryLow + GasCostOf.VeryLow;
        return gasCost;
    }

    public void Invoke<T>(EvmState vmState, ulong chainId, in ExecutionEnvironment env, in TxExecutionContext txCtx, in BlockExecutionContext blkCtx, IBlockhashProvider blockhashProvider, IWorldState worldState, ICodeInfoRepository codeInfoRepository, IReleaseSpec spec, ref int programCounter, ref long gasAvailable, ref EvmStack<T> stack, ref ReadOnlyMemory<byte> returnDataBuffer, ITxTracer trace, ILogger logger, ref ILChunkExecutionState result) where T : struct, VirtualMachine.IIsTracing
    {
        if (!VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.NotOptimizing>.UpdateGas(GasCost(vmState, spec), ref gasAvailable))
            result.ExceptionType = EvmExceptionType.OutOfGas;

        stack.Swap(3);
        stack.Swap(2);

        programCounter += 2;
    }
}
internal class D01P04EQ : IPatternChunk
{
    public string Name => nameof(D01P04EQ);
    public byte[] Pattern => [(byte)Instruction.DUP1, (byte)Instruction.PUSH4, (byte)Instruction.EQ];


    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = 3 * GasCostOf.VeryLow;
        return gasCost;
    }

    public void Invoke<T>(EvmState vmState, ulong chainId, in ExecutionEnvironment env, in TxExecutionContext txCtx, in BlockExecutionContext blkCtx, IBlockhashProvider blockhashProvider, IWorldState worldState, ICodeInfoRepository codeInfoRepository, IReleaseSpec spec, ref int programCounter, ref long gasAvailable, ref EvmStack<T> stack, ref ReadOnlyMemory<byte> returnDataBuffer, ITxTracer trace, ILogger logger, ref ILChunkExecutionState result) where T : struct, VirtualMachine.IIsTracing
    {
        if (!VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.NotOptimizing>.UpdateGas(GasCost(vmState, spec), ref gasAvailable))
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
internal class D01P04GT : IPatternChunk
{
    public string Name => nameof(D01P04GT);
    public byte[] Pattern => [(byte)Instruction.DUP1, (byte)Instruction.PUSH4, (byte)Instruction.GT];

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = 3 * GasCostOf.VeryLow;
        return gasCost;
    }

    public void Invoke<T>(EvmState vmState, ulong chainId, in ExecutionEnvironment env, in TxExecutionContext txCtx, in BlockExecutionContext blkCtx, IBlockhashProvider blockhashProvider, IWorldState worldState, ICodeInfoRepository codeInfoRepository, IReleaseSpec spec, ref int programCounter, ref long gasAvailable, ref EvmStack<T> stack, ref ReadOnlyMemory<byte> returnDataBuffer, ITxTracer trace, ILogger logger, ref ILChunkExecutionState result) where T : struct, VirtualMachine.IIsTracing
    {
        if (!VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.NotOptimizing>.UpdateGas(GasCost(vmState, spec), ref gasAvailable))
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
internal class D02MST : IPatternChunk
{
    public string Name => nameof(D02MST);
    public byte[] Pattern => [(byte)Instruction.DUP2, (byte)Instruction.MSTORE];

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = 2 * GasCostOf.VeryLow;
        return gasCost;
    }

    public void Invoke<T>(EvmState vmState, ulong chainId, in ExecutionEnvironment env, in TxExecutionContext txCtx, in BlockExecutionContext blkCtx, IBlockhashProvider blockhashProvider, IWorldState worldState, ICodeInfoRepository codeInfoRepository, IReleaseSpec spec, ref int programCounter, ref long gasAvailable, ref EvmStack<T> stack, ref ReadOnlyMemory<byte> returnDataBuffer, ITxTracer trace, ILogger logger, ref ILChunkExecutionState result) where T : struct, VirtualMachine.IIsTracing
    {
        if (!VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.NotOptimizing>.UpdateGas(GasCost(vmState, spec), ref gasAvailable))
            result.ExceptionType = EvmExceptionType.OutOfGas;


        stack.Dup(2);
        stack.PopUInt256(out UInt256 location);
        var bytes = stack.PopWord256();
        if (!VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.NotOptimizing>.UpdateMemoryCost(vmState, ref gasAvailable, in location, (UInt256)32))
            result.ExceptionType = EvmExceptionType.OutOfGas;
        vmState.Memory.SaveWord(in location, bytes);
        stack.PushUInt256(location);

        programCounter += 2;
    }
}
internal class P01ADDS01D02MST : IPatternChunk
{
    public string Name => nameof(P01ADDS01D02MST);
    public byte[] Pattern => [(byte)Instruction.PUSH1, (byte)Instruction.ADD, (byte)Instruction.SWAP1, (byte)Instruction.DUP2, (byte)Instruction.MSTORE];

    public long GasCost(EvmState vmState, IReleaseSpec spec)
    {
        long gasCost = 3 * GasCostOf.VeryLow;
        return gasCost;
    }
    public void Invoke<T>(EvmState vmState, ulong chainId, in ExecutionEnvironment env, in TxExecutionContext txCtx, in BlockExecutionContext blkCtx, IBlockhashProvider blockhashProvider, IWorldState worldState, ICodeInfoRepository codeInfoRepository, IReleaseSpec spec, ref int programCounter, ref long gasAvailable, ref EvmStack<T> stack, ref ReadOnlyMemory<byte> returnDataBuffer, ITxTracer trace, ILogger logger, ref ILChunkExecutionState result) where T : struct, VirtualMachine.IIsTracing
    {
        if (!VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.NotOptimizing>.UpdateGas(GasCost(vmState, spec), ref gasAvailable))
            result.ExceptionType = EvmExceptionType.OutOfGas;


        if (!stack.PopUInt256(out UInt256 location))
            result.ExceptionType = EvmExceptionType.StackUnderflow;

        location = location + (new UInt256(vmState.Env.CodeInfo.MachineCode.Span.Slice(programCounter + 3, 1)));

        if (!VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.NotOptimizing>.UpdateMemoryCost(vmState, ref gasAvailable, location, 32))
            result.ExceptionType = EvmExceptionType.OutOfGas;

        vmState.Memory.SaveWord(location, stack.PopWord256());
        stack.PushUInt256(location);

        programCounter += 6;
    }
}
