// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using Nethermind.Core.Specs;
using static Nethermind.Evm.VirtualMachine;
using static System.Runtime.CompilerServices.Unsafe;

namespace Nethermind.Evm;
using Int256;

internal sealed partial class EvmInstructions
{
    [SkipLocalsInit]
    public static (EvmExceptionType, int) InstructionJump<TTracingInstructions>(EvmState vmState, ref EvmStack<TTracingInstructions> stack, ref long gasAvailable, int programCounter)
        where TTracingInstructions : struct, IIsTracing
    {
        gasAvailable -= GasCostOf.Mid;
        if (!stack.PopUInt256(out UInt256 result)) return (EvmExceptionType.StackUnderflow, programCounter);
        if (!Jump(result, ref programCounter, in vmState.Env)) return (EvmExceptionType.InvalidJumpDestination, programCounter);

        return (EvmExceptionType.None, programCounter);
    }

    [SkipLocalsInit]
    public static (EvmExceptionType, int) InstructionJumpI<TTracingInstructions>(EvmState vmState, ref EvmStack<TTracingInstructions> stack, ref long gasAvailable, int programCounter)
        where TTracingInstructions : struct, IIsTracing
    {
        gasAvailable -= GasCostOf.High;
        if (!stack.PopUInt256(out UInt256 result)) return (EvmExceptionType.StackUnderflow, programCounter);
        if (As<byte, Vector256<byte>>(ref stack.PopBytesByRef()) != default)
        {
            if (!Jump(result, ref programCounter, in vmState.Env)) return (EvmExceptionType.InvalidJumpDestination, programCounter);
        }

        return (EvmExceptionType.None, programCounter);
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionBeginSub(ref long gasAvailable, IReleaseSpec spec)
    {
        if (!spec.SubroutinesEnabled) return EvmExceptionType.BadInstruction;

        // why do we even need the cost of it?
        gasAvailable -= GasCostOf.Base;

        return EvmExceptionType.InvalidSubroutineEntry;
    }

    [SkipLocalsInit]
    public static (EvmExceptionType, int) InstructionReturnSub<TTracingInstructions>(EvmState vmState, ref EvmStack<TTracingInstructions> stack, ref long gasAvailable, int programCounter, IReleaseSpec spec)
        where TTracingInstructions : struct, IIsTracing
    {
        if (!spec.SubroutinesEnabled) return (EvmExceptionType.BadInstruction, programCounter);

        gasAvailable -= GasCostOf.Low;

        if (vmState.ReturnStackHead == 0)
        {
            return (EvmExceptionType.InvalidSubroutineReturn, programCounter);
        }

        programCounter = vmState.ReturnStack[--vmState.ReturnStackHead];

        return (EvmExceptionType.None, programCounter);
    }

    [SkipLocalsInit]
    public static (EvmExceptionType, int) InstructionJumpSub<TTracingInstructions>(EvmState vmState, ref EvmStack<TTracingInstructions> stack, ref long gasAvailable, int programCounter, IReleaseSpec spec)
        where TTracingInstructions : struct, IIsTracing
    {
        if (!spec.SubroutinesEnabled) return (EvmExceptionType.BadInstruction, programCounter);

        gasAvailable -= GasCostOf.High;

        if (vmState.ReturnStackHead == EvmStack.ReturnStackSize) return (EvmExceptionType.StackOverflow, programCounter);

        vmState.ReturnStack[vmState.ReturnStackHead++] = programCounter;

        if (!stack.PopUInt256(out var result)) return (EvmExceptionType.StackUnderflow, programCounter);
        if (!Jump(result, ref programCounter, in vmState.Env, isSubroutine: true)) return (EvmExceptionType.InvalidJumpDestination, programCounter);
        programCounter++;

        return (EvmExceptionType.None, programCounter);
    }

    [SkipLocalsInit]
    public static bool Jump(in UInt256 jumpDest, ref int programCounter, in ExecutionEnvironment env, bool isSubroutine = false)
    {
        if (jumpDest > int.MaxValue)
        {
            // https://github.com/NethermindEth/nethermind/issues/140
            // TODO: add a test, validating inside the condition was not covered by existing tests and fails on 0xf435a354924097686ea88dab3aac1dd464e6a3b387c77aeee94145b0fa5a63d2 mainnet
            return false;
        }

        int jumpDestInt = (int)jumpDest;
        if (!env.CodeInfo.ValidateJump(jumpDestInt, isSubroutine))
        {
            // https://github.com/NethermindEth/nethermind/issues/140
            // TODO: add a test, validating inside the condition was not covered by existing tests and fails on 61363 Ropsten
            return false;
        }

        programCounter = jumpDestInt;
        return true;
    }
}
