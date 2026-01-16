// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.Evm;

using Int256;

internal static partial class EvmInstructions
{
    public interface IOpMath3Param
    {
        virtual static long GasCost => GasCostOf.Mid;
        abstract static void Operation(in UInt256 a, in UInt256 b, in UInt256 c, out UInt256 result);
    }

    [SkipLocalsInit]
    public static OpcodeResult InstructionMath3Param<TGasPolicy, TOpMath, TTracingInst>(VirtualMachine<TGasPolicy> _, ref EvmStack stack, ref TGasPolicy gas, int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpMath : struct, IOpMath3Param
        where TTracingInst : struct, IFlag
    {
        TGasPolicy.Consume(ref gas, TOpMath.GasCost);

        if (!stack.PopUInt256(
            out UInt256 a,
            out UInt256 b,
            out UInt256 c))
        {
            goto StackUnderflow;
        }

        if (c.IsZero)
        {
            return new(programCounter, stack.PushZero<TTracingInst>());
        }
        else
        {
            TOpMath.Operation(in a, in b, in c, out UInt256 result);
            return new(programCounter, stack.PushUInt256<TTracingInst>(in result));
        }
    StackUnderflow:
        // Jump forward to be unpredicted by the branch predictor
        return new(programCounter, EvmExceptionType.StackUnderflow);
    }

    public struct OpAddMod : IOpMath3Param
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Operation(in UInt256 a, in UInt256 b, in UInt256 c, out UInt256 result) => UInt256.AddMod(in a, in b, in c, out result);
    }

    public struct OpMulMod : IOpMath3Param
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Operation(in UInt256 a, in UInt256 b, in UInt256 c, out UInt256 result) => UInt256.MultiplyMod(in a, in b, in c, out result);
    }
}
