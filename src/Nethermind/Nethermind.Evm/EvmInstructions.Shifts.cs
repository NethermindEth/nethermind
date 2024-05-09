// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core.Specs;
using static Nethermind.Evm.VirtualMachine;
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
    public static EvmExceptionType InstructionShift<TOpShift>(EvmState vmState, ref EvmStack stack, ref long gasAvailable)
        where TOpShift : struct, IOpShift
    {
        if (!vmState.Spec.ShiftOpcodesEnabled) return EvmExceptionType.BadInstruction;

        gasAvailable -= TOpShift.GasCost;

        if (!stack.PopUInt256(out UInt256 a)) return EvmExceptionType.StackUnderflow;
        if (a >= 256)
        {
            stack.PopLimbo();
            stack.PushZero();
        }
        else
        {
            if (!stack.PopUInt256(out UInt256 b)) return EvmExceptionType.StackUnderflow;
            TOpShift.Operation(in a, in b, out UInt256 result);
            stack.PushUInt256(in result);
        }

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionSar(EvmState vmState, ref EvmStack stack, ref long gasAvailable)
    {
        if (!vmState.Spec.ShiftOpcodesEnabled) return EvmExceptionType.BadInstruction;

        gasAvailable -= GasCostOf.VeryLow;

        if (!stack.PopUInt256(out UInt256 a)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 b)) return EvmExceptionType.StackUnderflow;

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
