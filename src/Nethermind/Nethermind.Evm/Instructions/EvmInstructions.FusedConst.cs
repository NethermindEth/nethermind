// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using static System.Runtime.CompilerServices.Unsafe;

namespace Nethermind.Evm;

using Int256;

public static partial class EvmInstructions
{
    /// <summary>
    /// Body of a fused <c>PUSH const; binary-op</c> pair: the op runs against the pre-decoded
    /// constant directly on the stack top — no push, no pop, one dispatch. Preserves the exact
    /// per-op failure order: the push's overflow at a full stack, then the op's underflow.
    /// </summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static EvmExceptionType FusedConstBinaryCore<TOpMath>(ref EvmStack stack, in UInt256 a)
        where TOpMath : struct, IOpMath2Param
    {
        if (stack.Head == EvmStack.MaxStackSize - 1)
            return EvmExceptionType.StackOverflow;

        ref byte topRef = ref stack.PeekBytesByRef();
        if (IsNullRef(ref topRef)) return EvmExceptionType.StackUnderflow;

        // Copy the pooled constant to a local: UInt256 ops over an in-array ref defeat limb
        // enregistration (defensive copies per operation).
        UInt256 aLocal = a;
        EvmStack.ReadUInt256FromSlot(ref topRef, out UInt256 b);
        TOpMath.Operation(in aLocal, in b, out UInt256 result);
        EvmStack.WriteUInt256ToSlot(ref topRef, in result);
        return EvmExceptionType.None;
    }

    /// <summary>
    /// Body of a fused <c>PUSH shift-amount; SHL/SHR</c> pair, mirroring
    /// <see cref="ShiftCore{TOpShift, TTracingInst}"/> against the pre-decoded amount.
    /// </summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static EvmExceptionType FusedConstShiftCore<TOpShift>(ref EvmStack stack, in UInt256 a)
        where TOpShift : struct, IOpShift
    {
        if (stack.Head == EvmStack.MaxStackSize - 1)
            return EvmExceptionType.StackOverflow;

        ref byte topRef = ref stack.PeekBytesByRef();
        if (IsNullRef(ref topRef)) return EvmExceptionType.StackUnderflow;

        // Mirrors ShiftCore: amounts of 256 or more shift everything out.
        if (!a.IsUint64 || a.u0 >= 256)
        {
            EvmStack.WriteUInt256ToSlot(ref topRef, in UInt256.Zero);
            return EvmExceptionType.None;
        }

        UInt256 aLocal = a;
        EvmStack.ReadUInt256FromSlot(ref topRef, out UInt256 b);
        TOpShift.Operation(in aLocal, in b, out UInt256 result);
        EvmStack.WriteUInt256ToSlot(ref topRef, in result);
        return EvmExceptionType.None;
    }

    /// <summary>Bitwise AND over UInt256 for the fused-const path.</summary>
    public struct OpAndFused : IOpMath2Param
    {
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result) => result = a & b;
    }

    /// <summary>Bitwise OR over UInt256 for the fused-const path.</summary>
    public struct OpOrFused : IOpMath2Param
    {
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result) => result = a | b;
    }

    /// <summary>Bitwise XOR over UInt256 for the fused-const path.</summary>
    public struct OpXorFused : IOpMath2Param
    {
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result) => result = a ^ b;
    }

    /// <summary>Equality over UInt256 for the fused-const path.</summary>
    public struct OpEqFused : IOpMath2Param
    {
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result)
            => result = a == b ? UInt256.One : default;
    }
}
