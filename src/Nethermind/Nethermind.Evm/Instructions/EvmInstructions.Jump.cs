// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Nethermind.Evm;
using Int256;

internal sealed partial class EvmInstructions
{
    [SkipLocalsInit]
    public static EvmExceptionType InstructionProgramCounter(IEvm _, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        gasAvailable -= GasCostOf.Base;
        stack.PushUInt32(programCounter - 1);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionJumpDest(IEvm _, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        gasAvailable -= GasCostOf.JumpDest;

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionJump(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        gasAvailable -= GasCostOf.Mid;
        if (!stack.PopUInt256(out UInt256 result)) return EvmExceptionType.StackUnderflow;
        if (!Jump(result, ref programCounter, in vm.State.Env)) return EvmExceptionType.InvalidJumpDestination;

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static EvmExceptionType InstructionJumpIf(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        gasAvailable -= GasCostOf.High;
        if (!stack.PopUInt256(out UInt256 result)) return EvmExceptionType.StackUnderflow;
        ref byte condition = ref stack.PopBytesByRef();
        if (Unsafe.IsNullRef(in condition)) return EvmExceptionType.StackUnderflow;
        if (Unsafe.As<byte, Vector256<byte>>(ref condition) != default)
        {
            if (!Jump(result, ref programCounter, in vm.State.Env)) return EvmExceptionType.InvalidJumpDestination;
        }

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    private static bool Jump(in UInt256 jumpDestination, ref int programCounter, in ExecutionEnvironment env)
    {
        bool isJumpDestination = true;
        if (jumpDestination > int.MaxValue)
        {
            // https://github.com/NethermindEth/nethermind/issues/140
            // TODO: add a test, validating inside the condition was not covered by existing tests and fails on 0xf435a354924097686ea88dab3aac1dd464e6a3b387c77aeee94145b0fa5a63d2 mainnet
            isJumpDestination = false;
        }
        else
        {
            int jumpDestinationInt = (int)jumpDestination.u0;
            if (!env.CodeInfo.ValidateJump(jumpDestinationInt))
            {
                // https://github.com/NethermindEth/nethermind/issues/140
                // TODO: add a test, validating inside the condition was not covered by existing tests and fails on 61363 Ropsten
                isJumpDestination = false;
            }
            else
            {
                programCounter = jumpDestinationInt;
            }
        }

        return isJumpDestination;
    }
}
