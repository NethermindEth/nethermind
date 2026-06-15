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

        // Local copy: UInt256 ops over an in-array ref defeat limb enregistration.
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

    /// <summary>
    /// Body of a fused <c>PUSH const; bitwise-op</c> pair over the stack-representation pool:
    /// one vector load per operand, no limb conversion.
    /// </summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
}
