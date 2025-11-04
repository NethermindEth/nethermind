// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm;
using Int256;

internal static partial class EvmInstructions
{
    public interface IOpMath3Param
    {
        virtual static long GasCost(IReleaseSpec spec) => GasCostOf.Mid;
        abstract static void Operation(in UInt256 a, in UInt256 b, in UInt256 c, out UInt256 result);
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionMath3Param<TOpMath, TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpMath : struct, IOpMath3Param
        where TTracingInst : struct, IFlag
    {
        gasAvailable -= TOpMath.GasCost(vm.Spec);

        if (!stack.PopUInt256(out UInt256 a) || !stack.PopUInt256(out UInt256 b) || !stack.PopUInt256(out UInt256 c)) goto StackUnderflow;

        if (c.IsZero)
        {
            stack.PushZero<TTracingInst>();
        }
        else
        {
            TOpMath.Operation(in a, in b, in c, out UInt256 result);
            stack.PushUInt256<TTracingInst>(in result);
        }

        return EvmExceptionType.None;
    StackUnderflow:
        // Jump forward to be unpredicted by the branch predictor
        return EvmExceptionType.StackUnderflow;
    }

    public struct OpAddMod : IOpMath3Param
    {
        public static long GasCost(IReleaseSpec spec) => spec.IsEip7904Enabled ? GasCostOf.FastOpcode : GasCostOf.Mid;
        public static void Operation(in UInt256 a, in UInt256 b, in UInt256 c, out UInt256 result) => UInt256.AddMod(in a, in b, in c, out result);
    }

    public struct OpMulMod : IOpMath3Param
    {
        public static long GasCost(IReleaseSpec spec) => spec.IsEip7904Enabled ? GasCostOf.MidOpcode : GasCostOf.Mid;
        public static void Operation(in UInt256 a, in UInt256 b, in UInt256 c, out UInt256 result) => UInt256.MultiplyMod(in a, in b, in c, out result);
    }
}
