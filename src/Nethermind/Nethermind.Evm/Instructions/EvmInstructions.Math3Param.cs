// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using Nethermind.Core;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.Evm;

using Int256;

public static partial class EvmInstructions
{
    public interface IOpMath3Param
    {
        virtual static long GasCost => GasCostOf.Mid;
        abstract static void Operation(in UInt256 a, in UInt256 b, in UInt256 c, out UInt256 result);
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionMath3Param<TGasPolicy, TOpMath, TTracingInst>(VirtualMachine<TGasPolicy> _, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpMath : struct, IOpMath3Param
        where TTracingInst : struct, IFlag
    {
        TGasPolicy.Consume(ref gas, TOpMath.GasCost);

        // Pop a and b, peek the third slot for in-place write; skips the push overflow check.
        ref byte topRef = ref stack.Pop2Peek32Bytes(out UInt256 a, out UInt256 b, out bool ok);
        if (!ok) goto StackUnderflow;

        EvmStack.ReadUInt256FromSlot(ref topRef, out UInt256 c);
        if (c.IsZero)
        {
            // c-slot already held c; overwrite with zero (matches PushZero semantics).
            Unsafe.As<byte, Vector256<byte>>(ref topRef) = default;
        }
        else
        {
            TOpMath.Operation(in a, in b, in c, out UInt256 result);
            EvmStack.WriteUInt256ToSlot(ref topRef, in result);
        }

        if (TTracingInst.IsActive) stack.ReportPushUInt256(ref topRef);
        return EvmExceptionType.None;
    StackUnderflow:
        // Jump forward to be unpredicted by the branch predictor
        return EvmExceptionType.StackUnderflow;
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
