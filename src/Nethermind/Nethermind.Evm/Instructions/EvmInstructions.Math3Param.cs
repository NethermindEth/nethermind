// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Evm;
using Int256;

internal sealed partial class EvmInstructions
{
    public interface IOpMath3Param
    {
        virtual static long GasCost => GasCostOf.Mid;
        abstract static void Operation(in UInt256 a, in UInt256 b, in UInt256 c, out UInt256 result);
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionMath3Param<TOpMath>(IEvm _, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpMath : struct, IOpMath3Param
    {
        gasAvailable -= TOpMath.GasCost;

        if (!stack.PopUInt256(out UInt256 a)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 b)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 c)) return EvmExceptionType.StackUnderflow;

        if (c.IsZero)
        {
            stack.PushZero();
        }
        else
        {
            TOpMath.Operation(in a, in b, in c, out UInt256 result);
            stack.PushUInt256(in result);
        }

        return EvmExceptionType.None;
    }

    public struct OpAddMod : IOpMath3Param
    {
        public static void Operation(in UInt256 a, in UInt256 b, in UInt256 c, out UInt256 result) => UInt256.AddMod(in a, in b, in c, out result);
    }

    public struct OpMulMod : IOpMath3Param
    {
        public static void Operation(in UInt256 a, in UInt256 b, in UInt256 c, out UInt256 result) => UInt256.MultiplyMod(in a, in b, in c, out result);
    }
}
