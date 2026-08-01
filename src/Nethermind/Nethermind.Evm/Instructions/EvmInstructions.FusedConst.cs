// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using static System.Runtime.CompilerServices.Unsafe;

namespace Nethermind.Evm;

using Int256;

public static partial class EvmInstructions
{
    /// <summary>
    /// Fused <c>PUSH const; binary-op</c>: runs against the pre-decoded constant on the stack top —
    /// no push/pop, one dispatch. Preserves per-op failure order: push overflow before op underflow.
    /// </summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static EvmExceptionType FusedConstBinaryCore<TOpMath>(ref EvmStack stack, UInt256 a)
        where TOpMath : struct, IOpMath2Param
    {
        if (stack.Head == EvmStack.MaxStackSize - 1)
            return EvmExceptionType.StackOverflow;

        ref byte topRef = ref stack.PeekBytesByRef();
        if (IsNullRef(ref topRef)) return EvmExceptionType.StackUnderflow;

        EvmStack.ReadUInt256FromSlot(ref topRef, out UInt256 b);
        TOpMath.Operation(in a, in b, out UInt256 result);
        EvmStack.WriteUInt256ToSlot(ref topRef, in result);
        return EvmExceptionType.None;
    }

    /// <summary>Fused <c>PUSH shift-amount; SHL/SHR</c>, mirroring <see cref="ShiftCore{TOpShift, TTracingInst}"/>.</summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static EvmExceptionType FusedConstShiftCore<TOpShift>(ref EvmStack stack, UInt256 a)
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

        EvmStack.ReadUInt256FromSlot(ref topRef, out UInt256 b);
        TOpShift.Operation(in a, in b, out UInt256 result);
        EvmStack.WriteUInt256ToSlot(ref topRef, in result);
        return EvmExceptionType.None;
    }

    /// <summary>
    /// Fused <c>PUSH const; bitwise-op</c> over the stack-representation pool: one vector load per
    /// operand, no limb conversion.
    /// </summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static EvmExceptionType FusedConstBitwiseCore<TOpBitwise>(ref EvmStack stack, ref byte constantSlot)
        where TOpBitwise : struct, IOpBitwise
    {
        if (stack.Head == EvmStack.MaxStackSize - 1)
            return EvmExceptionType.StackOverflow;

        ref byte topRef = ref stack.PeekBytesByRef();
        if (IsNullRef(ref topRef)) return EvmExceptionType.StackUnderflow;

        EvmWord a = ReadUnaligned<EvmWord>(ref constantSlot);
        EvmWord b = ReadUnaligned<EvmWord>(ref topRef);
        WriteUnaligned(ref topRef, TOpBitwise.Operation(in a, in b));
        return EvmExceptionType.None;
    }
    /// <summary>
    /// Fused <c>SUB; AND</c> - masking a difference. Three words in, one out, with no intermediate
    /// write to the stack. One depth check covers both halves: the depths it rejects are exactly
    /// the depths at which one of the two would have underflowed, and an exceptional halt discards
    /// the stack either way.
    /// </summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static EvmExceptionType FusedSubAndCore(ref EvmStack stack)
    {
        if (stack.Head < 3) return EvmExceptionType.StackUnderflow;

        ref byte top = ref stack.PeekBytesByRef();
        EvmStack.ReadUInt256FromSlot(ref top, out UInt256 a);
        ref byte second = ref Subtract(ref top, EvmStack.WordSize);
        EvmStack.ReadUInt256FromSlot(ref second, out UInt256 b);
        OpSub.Operation(in a, in b, out UInt256 difference);

        ref byte third = ref Subtract(ref second, EvmStack.WordSize);
        EvmStack.ReadUInt256FromSlot(ref third, out UInt256 mask);
        UInt256 result = difference & mask;

        EvmStack.WriteUInt256ToSlot(ref third, in result);
        stack.Head -= 2;
        return EvmExceptionType.None;
    }

}
