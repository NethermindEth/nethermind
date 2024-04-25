// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using static Nethermind.Evm.VirtualMachine;
using static System.Runtime.CompilerServices.Unsafe;

namespace Nethermind.Evm;
using Int256;

internal sealed partial class EvmInstructions
{
    public interface IOpMath2Param
    {
        virtual static long GasCost => GasCostOf.VeryLow;
        abstract static void Operation(in UInt256 a, in UInt256 b, out UInt256 result);
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionMath2Param<TOpMath, TTracingInstructions>(ref EvmStack<TTracingInstructions> stack, ref long gasAvailable)
        where TOpMath : struct, IOpMath2Param
        where TTracingInstructions : struct, IIsTracing
    {
        gasAvailable -= TOpMath.GasCost;

        if (!stack.PopUInt256(out UInt256 a)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 b)) return EvmExceptionType.StackUnderflow;

        TOpMath.Operation(in a, in b, out UInt256 result);

        stack.PushUInt256(in result);

        return EvmExceptionType.None;
    }

    public struct OpAdd : IOpMath2Param
    {
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result) => UInt256.Add(in a, in b, out result);
    }

    public struct OpSub : IOpMath2Param
    {
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result) => UInt256.Subtract(in a, in b, out result);
    }

    public struct OpMul : IOpMath2Param
    {
        public static long GasCost => GasCostOf.Low;
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result) => UInt256.Multiply(in a, in b, out result);
    }

    public struct OpDiv : IOpMath2Param
    {
        public static long GasCost => GasCostOf.Low;
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result)
        {
            if (b.IsZero)
            {
                result = default;
            }
            else
            {
                UInt256.Divide(in a, in b, out result);
            }
        }
    }

    public struct OpSDiv : IOpMath2Param
    {
        public static long GasCost => GasCostOf.Low;
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result)
        {
            if (b.IsZero)
            {
                result = default;
            }
            else if (As<UInt256, Int256>(ref AsRef(in b)) == Int256.MinusOne && a == P255)
            {
                result = P255;
            }
            else
            {
                SkipInit(out result);
                Int256.Divide(
                    in As<UInt256, Int256>(ref AsRef(in a)),
                    in As<UInt256, Int256>(ref AsRef(in b)),
                    out As<UInt256, Int256>(ref result));
            }
        }
    }

    public struct OpMod : IOpMath2Param
    {
        public static long GasCost => GasCostOf.Low;
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result) => UInt256.Mod(in a, in b, out result);
    }

    public struct OpSMod : IOpMath2Param
    {
        public static long GasCost => GasCostOf.Low;
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result)
        {
            if (b.IsZeroOrOne)
            {
                result = default;
            }
            else
            {
                SkipInit(out result);
                As<UInt256, Int256>(ref AsRef(in a))
                    .Mod(
                        in As<UInt256, Int256>(ref AsRef(in b)),
                        out As<UInt256, Int256>(ref result));
            }
        }
    }

    public struct OpLt : IOpMath2Param
    {
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result)
        {
            result = a < b ? UInt256.One : default;
        }
    }

    public struct OpGt : IOpMath2Param
    {
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result)
        {
            result = a > b ? UInt256.One : default;
        }
    }

    public struct OpSLt : IOpMath2Param
    {
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result)
        {
            result = As<UInt256, Int256>(ref AsRef(in a))
                .CompareTo(As<UInt256, Int256>(ref AsRef(in b))) < 0 ?
                UInt256.One :
                default;
        }
    }

    public struct OpSGt : IOpMath2Param
    {
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result)
        {
            result = As<UInt256, Int256>(ref AsRef(in a))
                .CompareTo(As<UInt256, Int256>(ref AsRef(in b))) > 0 ?
                UInt256.One :
                default;
        }
    }
}
