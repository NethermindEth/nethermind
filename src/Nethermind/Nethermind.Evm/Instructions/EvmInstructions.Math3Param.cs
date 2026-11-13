// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.Evm;

using Int256;

public static partial class EvmInstructions
{
    public interface IOpMath3Param : IGasCost
    {
        static ulong IGasCost.GasCost => GasCostOf.Mid;
        abstract static void Operation(in UInt256 a, in UInt256 b, in UInt256 c, out UInt256 result);
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EvmExceptionType InstructionMath3Param<TGasPolicy, TOpMath, TTracingInst>(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> _)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpMath : struct, IOpMath3Param
        where TTracingInst : struct, IFlag
    {
        if (!TGasPolicy.UpdateGas<TOpMath>(ref gas)) return EvmExceptionType.OutOfGas;

        if (!stack.EnsureDepth(3)) return EvmExceptionType.StackUnderflow;
        return Math3ParamCore<TOpMath, TTracingInst>(ref stack);
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static EvmExceptionType Math3ParamCore<TOpMath, TTracingInst>(ref EvmStack stack)
        where TOpMath : struct, IOpMath3Param
        where TTracingInst : struct, IFlag
    {
        ref byte topRef = ref stack.Pop2Peek32BytesUnchecked();

        if (!EvmStack.IsSlotZero(ref topRef))
        {
            // The result goes to the modulus slot and the Int256 routines are not guaranteed alias-safe, so only
            // the modulus is copied to a local. The popped slots stay intact until the next push, so a and b are
            // read in place and the result is written straight into the slot.
            EvmStack.ReadUInt256FromSlot(ref topRef, out UInt256 c);
            TOpMath.Operation(
                in Unsafe.As<byte, UInt256>(ref Unsafe.Add(ref topRef, 2 * EvmStack.WordSize)),
                in Unsafe.As<byte, UInt256>(ref Unsafe.Add(ref topRef, EvmStack.WordSize)),
                in c,
                out Unsafe.As<byte, UInt256>(ref topRef));
        }

        if (TTracingInst.IsActive) stack.ReportPushWord(ref topRef);
        return EvmExceptionType.None;
    }

    public struct OpAddMod : IOpMath3Param
    {
        public static void Operation(in UInt256 a, in UInt256 b, in UInt256 c, out UInt256 result) => AddMod256(in a, in b, in c, out result);
    }

    public struct OpMulMod : IOpMath3Param
    {
        public static void Operation(in UInt256 a, in UInt256 b, in UInt256 c, out UInt256 result) => MulMod256(in a, in b, in c, out result);
    }
}
