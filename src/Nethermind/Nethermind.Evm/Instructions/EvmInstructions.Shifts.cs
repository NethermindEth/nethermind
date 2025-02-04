// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using static System.Runtime.CompilerServices.Unsafe;

namespace Nethermind.Evm;
using Int256;

internal sealed partial class EvmInstructions
{
    public interface IOpShift
    {
        virtual static long GasCost => GasCostOf.VeryLow;
        abstract static void Operation(in UInt256 a, in UInt256 b, out UInt256 result);
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionShift<TOpShift>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpShift : struct, IOpShift
    {
        gasAvailable -= TOpShift.GasCost;

        if (!stack.PopUInt256(out UInt256 a)) goto StackUnderflow;
        if (a >= 256)
        {
            if (!stack.PopLimbo()) goto StackUnderflow;
            stack.PushZero();
        }
        else
        {
            if (!stack.PopUInt256(out UInt256 b)) goto StackUnderflow;
            TOpShift.Operation(in a, in b, out UInt256 result);
            stack.PushUInt256(in result);
        }

        return EvmExceptionType.None;
    // Reduce inline code returns, also jump forward to be unpredicted by the branch predictor
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionSar(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        gasAvailable -= GasCostOf.VeryLow;

        if (!stack.PopUInt256(out UInt256 a) || !stack.PopUInt256(out UInt256 b)) goto StackUnderflow;

        if (a >= 256)
        {
            if (As<UInt256, Int256>(ref b).Sign >= 0)
            {
                stack.PushZero();
            }
            else
            {
                stack.PushSignedInt256(in Int256.MinusOne);
            }
        }
        else
        {
            As<UInt256, Int256>(ref b).RightShift((int)a, out Int256 result);
            stack.PushUInt256(in As<Int256, UInt256>(ref result));
        }

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    public struct OpShl : IOpShift
    {
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result)
            => result = b << (int)a.u0;
    }

    public struct OpShr : IOpShift
    {
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result)
            => result = b >> (int)a.u0;
    }
}
